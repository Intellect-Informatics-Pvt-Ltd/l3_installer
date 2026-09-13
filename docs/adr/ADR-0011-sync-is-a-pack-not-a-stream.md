# ADR-0011: Sync is a pack, not a stream

**Status:** Accepted
**Date:** 2026-09-13
**Deciders:** Programme owner (ruling), Architecture (design)
**Supersedes:** ADR-0006 for the offline node. ADR-0006 stays the record for the *connected*
estate, where the transactional outbox feeds intra-node Kafka between modules.
**Relates to:** ADR-0003 (Kafka conditional), ADR-0012 (deferred approvals), ADR-0013
(migration runner)

---

## Context

The installer's `Sync.Agent` was written against a **National Level Data Repository** reached
over Kafka and HTTPS: `OutboxRelay` (MySQL → local Kafka), `SyncAgentWorker` (Kafka → NLDR),
`InboxProcessor` (NLDR → PACS), `IReconciliationEngine`. As at 2026-09-13:

- "NLDR" appears in **no file in the L2-R2 estate** outside this repository. The one mention in
  `thoughts/shared/plans/2026-08-23-integration-approach.md:317` is a hosting location for AP's
  eKYC endpoint, not a data counterparty. Gate G1 of the assessment ("is there an NLDR
  programme?") has had no answer since 2026-08-29.
- `ISyncTransport`, `IReconciliationEngine` and `IFileSyncTransport` have zero implementations.
  `OutboxRelay.DrainAsync` returns 0; `InboxProcessor.ApplyEvent` logs and returns;
  `Sync.Agent/Program.cs` throws at startup because `ConnectivityMonitor` requires an
  `ISyncTransport` nobody registers.
- On the L2-R2 side, **nothing moves business data off a node today**. Every outbox
  (`OutboxMessages`, `orchestration_outbox_messages`) is intra-node eventing between modules,
  and `Orchestration:Enabled` / `Messaging:Enabled` are off in every module that carries them.
- The deployment model the estate actually runs is **one central instance per state or DCCB**
  serving many PACS as tenants (`PacsId`). A per-society node is a new topology, and the only
  thing above it that exists is that state instance.

Continuing to build a stream against a counterparty that does not exist would ship code that
has never met the thing it talks to. That is the failure mode the estate's own doctrine —
*counted, never assumed* — exists to prevent.

## Decision

**Data leaves and re-enters an offline PACS node only as packs.** A pack is a file. It is
carried over whatever exists — an intermittent link, a WireGuard peer when one is up, or a USB
stick in a bag — and it is exactly as trustworthy on every carrier, because its integrity is in
the file, not in the channel.

### The two pack types

| Pack | Direction | Produced by | Consumed by | Contents |
|---|---|---|---|---|
| **Ledger pack** | node → state | `Sync.Agent` (`LedgerPackExporter`), on a schedule or on demand | the state instance (`l2r2 pack ingest`, owed) | rows in the society's `PacsId` scope changed since the previous pack's watermark, including *pending-external* requests under ADR-0012 |
| **Policy pack** | state → node | the state instance (owed) | `Sync.Agent` (`PolicyPackApplier`) | masters and product definitions owned above the PACS, decisions on deferred approvals, configuration (`.epcfg` revisions), and nothing that changes state policy in `appsettings.<STATE>.json` — that stays under the LOCKED state-variation doctrine |

The **site data pack** (`.epdata`, produced by `l2r2 db carve-out`) is the zeroth policy pack:
the society's entire scope at birth. It uses the same envelope.

### The envelope

Every pack carries, in this order, and the reader checks them in this order:

1. `manifest.json` — `pacs_id`, `state`, `pack_type`, `pack_seq` (monotone per `(pacs_id,
   pack_type)`), `prev_pack_hash` (SHA-256 of the previous pack's manifest; the genesis value for
   `pack_seq = 1`), `watermark_from` / `watermark_to` (the `AuditChain` sequence range for a ledger
   pack; the source snapshot for a policy pack), `schema_fingerprint` (the node's
   `MySqlSchemaFingerprinter` hash, so a pack is never applied across a schema it was not cut
   from), `produced_at`, `producer_version`, and a **row-count table** — one entry per table with
   the number of rows in the pack.
2. `manifest.sig` — detached CMS (PKCS#7) over `manifest.json`, produced by `CmsCodeSigner`,
   verified by `SignatureVerifier` against the pinned signer the same way a medium is. The node
   signs ledger packs with the site key issued in its `.epcfg`; the state signs policy packs with
   the release key. A pack whose signer is not the expected one is refused before a byte of data
   is read.
3. `data/<table>.sql` — mysqldump-compatible `INSERT ... ON DUPLICATE KEY UPDATE` per table, so
   application is idempotent at the row level as well as at the pack level. Each file's SHA-256
   is in the manifest.

### The rules the reader enforces

- **Sequence.** A pack is applied only if `pack_seq` is exactly the last applied + 1 **and**
  `prev_pack_hash` equals the hash of the last applied manifest. A gap (`N+2` without `N+1`) is
  refused and named; a replay (`N` again) is acknowledged and not re-applied. Exactly-once is by
  `(pacs_id, pack_type, pack_seq)`, recorded in a `sync_pack_ledger` table on both sides.
- **Signature before content.** Nothing under `data/` is opened until `manifest.sig` verifies.
- **Counts.** After application, `SELECT COUNT(*)` per table over the pack's key set must equal
  the manifest's row count. A mismatch rolls the pack back and is a refusal, not a warning.
- **Scope.** A ledger pack whose `pacs_id` differs from the node's `.epcfg` is refused. A ledger
  pack contains only rows whose `PacsId` is the node's — enforced by the exporter reading
  `db/pacs-table-classification.json`, and by a guard that fails when a `PacsId`-carrying table
  in the baseline is missing from that file.
- **Schema.** A pack cut against a different `schema_fingerprint` is refused; upgrade first
  (ADR-0013).

### What is frozen

`Components:Sync:Mode` takes `packs` (default) or `stream`. `stream` is refused with exit 4
naming this ADR. `OutboxRelay`, `SyncAgentWorker`'s Kafka path, `ISyncTransport` and
`IFileSyncTransport` are **not deleted** — deleting them would hide that the question was
asked — but they are not registered, not built upon, and marked frozen in `tasks.md` §20–§22.
If an NLDR programme materialises, it consumes ledger packs; nothing in this decision needs to
be undone for that.

## Consequences

- **Buildable and testable now.** Every piece a pack needs exists: `AuditChain` for the
  watermark, `CmsCodeSigner`/`SignatureVerifier` for the envelope, `BackupEngine`'s mysqldump
  path for the row format, `MySqlSchemaFingerprinter` for the schema check, and a live MySQL in
  CI. The state-side ingest is the one piece that is owed, and it is a consumer of a format that
  is fixed here.
- **RPO is the last pack; RTO is a restore.** Both are stated in the NABARD note in those terms.
  A lost USB stick leaks nothing (the pack is encrypted with the backup key at rest on removable
  media) and loses nothing (the exporter re-cuts from the watermark; the re-cut is byte-identical
  because the manifest carries no timestamp inside the hashed region).
- **Reconciliation is counts, not a second engine.** G12 (`IReconciliationEngine`) closes as: the
  state compares the row counts it ingested with what the node reports in its next pack's
  `watermark_from`; a disagreement is a drift finding in the estate's `live-*` vocabulary.
- **Approvals get a carrier.** ADR-0012's *pending-external* rows travel up in the ledger pack
  and the decisions travel back in the policy pack. Without this ADR, ADR-0012 has no transport.
- **The 290 MB Kafka + JRE payload is now genuinely optional** for an offline node, not merely
  conditional: nothing in the pack path uses it.
- **What this does not solve:** real-time integrations (Aadhaar OTP, eKYC, land records, CBS
  push). Those are unavailable offline by design and say so — see the operating-model note.
