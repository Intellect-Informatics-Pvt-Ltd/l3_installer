# ADR-0012: Approvals above the PACS are deferred, never bypassed

**Status:** Accepted
**Date:** 2026-09-13
**Deciders:** Programme owner (ruling), Architecture (design). The one open policy question is
NABARD's — see *What stays open*.
**Relates to:** ADR-0011 (the carrier), L2-R2 `docs/state-variation-doctrine.md` (LOCKED),
L2-R2 `docs/adr/ADR-0004-rbac-org-and-action-matrix.md`

---

## Context

The 2026-09-13 review (assessment §12, G29) found four business flows in L2-R2 that cannot
complete on a node with no link to the DCCB or the state:

| Flow | Where | Who or what is outside the PACS | Today's failure mode |
|---|---|---|---|
| KCC crop-loan sanction → DCCB core banking | `l3_Loans/.../SanctionRepository.SaveCropLoanSanction.PipelinePersist.cs:648-686` | the DCCB CBS gateway | synchronous HTTP; on any non-success the sanction is **rolled back** and compensated. Shipped `CBSIntegration:enabled=true`, gated per PACS by `ln_pacwiseparameters` ParameterID 15 |
| T12 / OTB / audit-certificate approval | `l3_FAS/.../T12ApprovalRepository.cs:195-389`, `l3_ERPClient/.../AadharApprovalController.cs` | approvers at user level 1 (State) and 2 (DCCB), **and** a UIDAI OTP through the AP APCOB/GSWS endpoints | the OTP call returns `"false"`; the approval cannot proceed. No toggle exists |
| DMS legacy-document request | `l3_DMS/Controllers/DMSUploadController.cs:400-414` (`RequestApprovedByDCCBUser`) | a DCCB user | the request waits forever on the node |
| SMS / e-mail on transaction events | `l3_uniteCommonAPI/.../SMSRepository.cs:150-272`, `l3_ERPClient/.../EmailHelper.cs` | the SMS gateway, SMTP | fail-soft: logged and dropped |

None of these has an offline path, and two of them (the CBS rollback and the OTP gate) turn a
connectivity fault into a *business* refusal that the operator cannot distinguish from a wrong
entry. The estate has no approval SLA, ageing or escalate-to column anywhere
(`thoughts/shared/plans/2026-09-03-rbac-approval-escalation-sweep.md:719`), so nothing today
would even notice that a request had been waiting for a week.

## Decision

**A request that needs a party above the PACS is recorded, carried, decided elsewhere, and the
decision is carried back. It is never bypassed on the node, and the node never guesses.**

### The state

Each of the three approval flows gains one status value, **`pending-external`**, on its existing
request row (`fa_otbApprovalDetails` / `fa_AuditApprovalDetails` / `fa_t14ApprovalDetails`, the
DMS request, the sanction). The row records: who raised it, when (node clock, and the
`AuditChain` sequence, which is what the SLA is measured from), which pack carried it
(`sync_pack_ledger` reference, filled by the exporter), and the decision when it returns.

The user sees it as what it is: *submitted, awaiting the DCCB / state; left the node in pack N
on date D*. Not "failed", not "approved".

### The three cuts, smallest first

1. **CBS push becomes queued when the PACS parameter says so.** `ParameterID = 15 = 'n'` — which
   the carve-out sets for an offline site — already stops the push. The change is that the
   sanction is then persisted with `CbsStatus = Queued` instead of being treated as if no
   integration existed, so the state instance pushes it when the ledger pack arrives and the
   result (`Accepted` / `Rejected` with the CBS reason) returns in the policy pack. The
   synchronous path and its rollback stay exactly as they are for a connected site.
2. **Aadhaar-OTP-gated approvals gain a named switch.** `Approvals:AadhaarOtp:Required`, default
   **true** (best practice ships as the default — RULE-FA). The offline profile sets it **false**,
   and every approval taken under `false` is stamped in the audit record with the switch name,
   the node identity and the pack it will travel in — a deviation via a named switch, with audit
   and reconciliation, which is precisely the shape the state-variation doctrine allows. The
   approval is still by the State/DCCB approver, on the state instance, when the pack arrives;
   the switch removes the OTP *transport*, not the *approver*.
3. **DMS requests and the notification channels ride the same rail.** The DMS request becomes
   `pending-external` and is approved on the state instance. SMS and e-mail go through
   `utils-traceability`'s `DeferredJournalSink` (built, adopted by 0 repos) and are replayed by
   whichever side has a link — the state instance sends on the node's behalf from the ledger
   pack if the node has none.

### Time

The **SLA clock for an external approval starts when the ledger pack carrying it is ingested at
the state**, not when the PACS operator pressed Submit. The node's clock is not trusted for
this; the ingest timestamp is. Both timestamps are kept, so the *transit* delay is visible as
its own number — that number is what a pack-cadence SLA measures.

Escalation follows the ladder already drafted in
`docs/product/L2-R2-rbac-defaults-review-note-2026-09-11.md` (PACS CEO 24 h → DCCB 48 h) and
the OpsBuddy N6 escalation model; both ship **inactive** and are the state's to activate. A pack
overdue by the configured cadence raises the state SPOC alert; that is the only escalation an
*offline* node can trigger, and it triggers by absence.

## Consequences

- A connectivity fault is no longer a business refusal. The operator can finish the day; the
  DCCB decides in its own time; the audit trail says which was which.
- No local override exists. There is no "approve provisionally" button in this decision. That
  is deliberate: an override is a policy NABARD would have to own, with a limit, a ratification
  rule and a reconciliation; none of that exists, and pretending it does would be the worst
  outcome — a local approval nobody above the PACS ever sees.
- Every cut is behind a switch that ships in its best-practice position; a connected site sees
  no change. The offline profile is where the switches move, and the carve-out is what writes
  the offline profile.
- The estate owes: the `pending-external` status on the three request tables (baseline change,
  through `build/generate-baseline-ddl.py`, not a module `CREATE TABLE`), the `CbsStatus`
  column, the `Approvals:AadhaarOtp:Required` option, `DeferredJournalSink` adoption in FAS and
  uniteCommonAPI, and the state-side pushes when packs arrive.

## What stays open — for NABARD

1. **Provisional approval within a limit.** Whether a PACS CEO may ever approve locally below a
   state-set threshold, to be ratified later. Recommendation: **no**, for the reasons above. If
   the answer is yes, it is a new ADR with the limit table, the ratification flow and the
   reconciliation report designed before a line is written.
2. **Aadhaar OTP fallback policy.** Whether an approval taken under
   `Approvals:AadhaarOtp:Required=false` needs a compensating control (for example, the
   approver's OTP at the state instance when the pack arrives). Recommendation: yes, at the
   state — it costs nothing offline and restores the control where the link exists.
3. **The pack cadence** the SLA is measured against — daily outbound is recommended, weekly is
   the floor below which the state SPOC is alerted.
