# l3_installer

> No curated summary for this repo yet. What it does is best read from its 3 controller file(s); add a line to `PURPOSE` in `build/generate-module-readmes.py` rather than editing this file.

## At a glance

| | |
|---|---|
| Current branch | `baseline/net10-retarget` |
| HEAD | `8901d37 F3: the database bootstrap, with Redis default and Kafka conditional` |
| C# files | 178 |
| Controllers / HTTP endpoints | 3 / 5 |
| SQL files / tables declared | 12 / 1071 |
| Test projects | `Harness.ChaosTests`, `Harness.ContractTests`, `Harness.IntegrationTests`, `Harness.LongOfflineTests`, `Installer.IntegrationTests`, `Installer.UnitTests` |

## Where this sits relative to `r2-dev-stable`

`r2-dev-stable` is the single integration branch: **every state's code merges onto it and nowhere else**, and one codebase serves all 30 states. A state branch exists only while that state's work is in flight.

**This repo has no state branches.** Everything it contains is on `r2-dev-stable` and is common to every state.

## Tables this repo declares

These are the tables named by a `CREATE TABLE` in this repo's own `db/**.sql`. They are **also** in `db/stable_baseline_ddl.sql` in the platform repo, which is what actually provisions a database — a module's `CREATE TABLE IF NOT EXISTS` never fires on a real estate, because every state already carries every product's tables.

- `ack_log`
- `agc_accountagentmapping`
- `agc_accountagentmappingcontrolrecords`
- `agc_agentdefinecommissionpolicycontrolrecords`
- `agc_agentdevicemapping`
- `agc_agentdevicemappingcontrolrecord`
- `agc_agentproductmapping`
- `agc_agentproductmappingcontrolrecords`
- `agc_agentreassigneddetails`
- `agc_agentregistrationcontrolrecords`
- `agc_agentregistrationdetails`
- `agc_agentsecuritydetails`
- `agc_agentsecuritydetailscontrolrecords`
- `agc_agentsuspension`
- `agc_agentsuspensionmaster`
- `agc_agenttypemaster`
- `agc_applicationnumbers`
- `agc_commissioncalculation`
- `agc_commissiontypes`
- `agc_defineagentcommission`
- `agc_guarantordetails`
- `agc_monthcalendar`
- `agc_parameterpacspecific`
- `agc_parameters`
- `agc_reasonmaster`
- `agc_tasktypemaster`
- `aggregatedcounter`
- `apiservicedetails`
- `ast_appreciation`
- `ast_assetallocatedetails`
- `ast_assetappreciationordepreciation`
- `ast_assetdeallocatedetails`
- `ast_assetinsurancedetails`
- `ast_categorymaster`
- `ast_commonitems`
- `ast_depreciationdetails`
- `ast_frequencymode`
- `ast_frequnecytypemaster`
- `ast_hardwareuploadstatus`
- `ast_itemmaster`
- …and 1031 more

## FAS & voucher integrity — what `r2-dev-stable` fixed and the switches that govern it

This repo touches the voucher pipeline (**1 file(s)** call the FAS/VoucherProcessing surface or name the voucher tables — measured, see the foot of the file). Every module that posts money rides the same hardened engines, so the platform-level fixes and their switches matter HERE, not just in l3_FAS:

- **Voucher numbering (TD-134).** Numbers come from a per-PACS/branch serialised allocator (advisory lock + counter row `fa_voucherno_counter`). The pre-fix engine could mint the SAME number for two concurrent postings — and returned the duplicate it had just detected. If this module mints any number itself (MAX+1 in its own SQL), that is TD-138 debt: fold it onto the FAS allocator, never extend it.
- **Module-local MAX+1 allocators (TD-138, 2026-08-21).** The surviving local allocators were audited against real databases, and two of them **had never run**: l3_Loans' bulk-disbursement series query named `VoucherDate` and `Pacid`, columns `fa_vouchermain` does not have (`ERROR 1054`, reproduced on an L1 copy and on a migrated baseline). The rest wrapped the date column in `DATE(...)`, which disqualifies `idx_fa_vouchermain_pacs_branch_date` and scans the estate's largest table on every allocation; they now use half-open ranges that select the identical rows (proven row-for-row on 24,952 real vouchers). **What is NOT fixed:** these allocators still read a MAX rather than incrementing a counter, so two concurrent bulk runs can still collide. That is the open half of TD-138.
- **Posting rollup (TD-135).** The ledger-ancestor rollup is one transaction: a posting updates the whole chain or fails whole and says so. A voucher this module submits can therefore FAIL LOUDLY where it used to half-post — handle the error; do not retry blindly (the voucher number is already allocated; the reversal path is governed).
- **Reconciliation (TD-139, P0).** `ops/l2r2 db voucher-recon` (DBA, estate-wide) and *FAS → Balance Corrections → Voucher Number Reconciliation* (admin screen, PACS-scoped) run the same 7 read-only checks: duplicate numbers in scope/year, details without header, headless vouchers, unbalanced vouchers, allocated-never-used, counter sanity. **Empty is the expected outcome.** Findings involving THIS module's vouchers route to the maker–checker correction flow — nothing edits posted history in place.
- **Pre-posting correction (Punjab Phase 2).** An UNPOSTED voucher can be corrected in place via `IPrepareVoucher/CorrectVoucherTemp` instead of delete-and-recapture. Posted vouchers are refused to the governed reversal. If this module has capture screens, adopt the endpoint rather than growing a local edit.

**The switches** (all live in FAS config; defaults are the shipped best practice — RULE-FA: deviations only by named switch, and every refusal names its switch):

| Switch | Default | Flipping it means |
|---|---|---|
| `Fas:VoucherNumbering:UseCounterAllocator` | `true` | `false` = step-1 legacy engine (advisory lock + FOR UPDATE fence), byte-for-byte; flip-safe both ways |
| `Fas:VoucherNumbering:MaxAllocationAttempts` | `5` | bound of honest retries before a LOUD failure — never returns a proven-duplicate number |
| `Fas:VoucherNumbering:AllocationLockTimeoutSeconds` | `15` | wait behind another allocator before failing the posting loudly |
| `Fas:LedgerRollupAtomicity:Enabled` | `true` | `false` = legacy per-level rollup — the half-posted-ledger risk; exists for rollback only |
| `Fas:PrePostingCorrection:Enabled` | `true` | `false` = corrections refused naming this switch; delete-and-recapture is the only pre-posting path |
| `Fas:PrePostingCorrection:BalanceTolerance` | `0.005` | Dr−Cr tolerance; `0` = exact balance required |
| `Fas:VoucherUndo:Mode` | `ReversalOnly` | post-posting correction is ALWAYS a governed reversal — no switch loosens this |

Every switch is flip-safe without data action (a config deploy, not a migration). End-to-end verification per state: [ops/VOUCHER-VERIFICATION-RUNBOOK.md](../ops/VOUCHER-VERIFICATION-RUNBOOK.md) in the platform repo; the design record (disproven designs included) is `l3_FAS/docs/TD-134-135-voucher-numbering-and-rollup.md`.

## Design documents

Hand-written design records in `docs/` — the WHY behind the changes the change log
below only dates. Read these before modifying the subsystems they cover.

- [Voucher Deletion, Amendment, and Data Integrity Analysis](docs/deletionsenerio.md)
- [ePACS Offline Installer — Engineering Guide](docs/engineering-guide.md)

## Change log — measured from git, newest first

Every entry below is read from this repo's own commits: **what** changed (the subject), **why** (the commit body's own first paragraph), **which files**, and the register / state-customization **ids** it carries. When a maintenance question arrives as a TD-xx or a state id (KA/AS/TN/WBxxxx), the index maps it straight to the commits, and each commit to its files.

### Commits

**`8901d37`** 2026-08-29 — F3: the database bootstrap, with Redis default and Kafka conditional

> Bundling the database is the reason this framework exists - an offline node must run no database that was not delivered and verified with the installation. It was the part with no implementation at all. Installer.Actions/Database now does it, ordered by what is irreversible:

Files: `.kiro/specs/epacs-offline-installer/tasks.md`, `docs/adr/ADR-0002-garnet-over-redis.md`, `docs/adr/ADR-0003-kafka-kraft-single-node.md`, `samples/service-map.yaml`, `src/Installer.Actions/Database/IDatabaseBootstrapper.cs`, `src/Installer.Actions/Database/IProcessRunner.cs`, `src/Installer.Actions/Database/MyIniWriter.cs`, `src/Installer.Actions/Database/MySqlBootstrapper.cs`, `src/Installer.Actions/Database/ProcessRunner.cs`, `src/Installer.Actions/Database/TableNameCaseGuard.cs` — and 9 more

**`6588854`** 2026-08-29 — F1: the composition root — the installer runs end to end for the first time

> Installer.CLI was a Console.WriteLine and `// TODO: Wire up full installer pipeline with DI`. Nine libraries sat unassembled, so no claim anywhere in tasks.md had ever been executed. This wires them together.

Files: `.kiro/specs/epacs-offline-installer/tasks.md`, `Directory.Packages.props`, `README.md`, `src/Installer.CLI/CliOptions.cs`, `src/Installer.CLI/Installer.CLI.csproj`, `src/Installer.CLI/Program.cs`, `src/Installer.Core/DependencyInjection/DenyAllOverrideTokenValidator.cs`, `src/Installer.Core/DependencyInjection/InstallerServiceCollectionExtensions.cs`, `src/Installer.Core/Installer.Core.csproj`, `src/Installer.Core/Logging/LogEvents.cs` — and 13 more

**`6ba2eba`** 2026-08-29 — Baseline the installer framework to r2-dev-stable: .NET 10, and CI that builds it

> The framework targeted .NET 8 while the payload it is meant to bundle (L2-R2) is .NET 10. Checked out as a sibling inside the L2-R2 workspace, whose global.json pins 10.0.302, it did not build at all: AnalysisLevel was `latest-recommended`, so the analyser set came from whichever SDK the machine had, and TreatWarningsAsErrors turned that difference into 58 errors.

Files: `.github/workflows/build.yml`, `.github/workflows/ci.yml`, `.kiro/specs/epacs-offline-installer/tasks.md`, `AGENTS.md`, `Directory.Build.props`, `Directory.Packages.props`, `README.md`, `docs/adr/ADR-0001-wix-v4-burn-bootstrapper.md`, `docs/adr/ADR-0002-garnet-over-redis.md`, `docs/adr/ADR-0003-kafka-kraft-single-node.md` — and 77 more

**`c8b63fd`** 2026-08-07 — Standardize NuGet package workflow and authentication

Files: `.github/workflows/build.yml`, `NuGet.Config`, `build_push_script.sh`

**`dda1006`** 2026-05-18 — fix(harness): NU1507 NuGet source mapping + Dockerfiles + team handoff docs

> - Fix NU1507 build error: add <clear/> and <packageSourceMapping> to   NuGet.Config so machine-level sources (e.g. GitHub Packages) don't   conflict with central package management - Add Dockerfiles for all 7 harness services (multi-stage build,   aspnet:8.0 runtime image) - Add .dockerignore for fast Docker builds - Add §6.1 "Docker-Based Testing — Team Handoff" to harness/README.md   with step-by-step setup, port map, fault injection reference, and   troubleshooting guide

Files: `harness/.dockerignore`, `harness/NuGet.Config`, `harness/README.md`, `harness/src/Nldr.Api/Dockerfile`, `harness/src/Nldr.DashboardUi/Dockerfile`, `harness/src/Nldr.SyncWorker/Dockerfile`, `harness/src/Pacs.Fas.Api/Dockerfile`, `harness/src/Pacs.Loans.Api/Dockerfile`, `harness/src/Pacs.OperatorUi/Dockerfile`, `harness/src/Pacs.SyncWorker/Dockerfile`

**`5a73cda`** 2026-05-15 — feat(M12): harness native deployment + installer integration + docs

> - Add win-x64 self-contained publish profiles (Directory.Build.props) - Add harness service-map.yaml (7 services with health/recovery) - Add installer-manifest-stub.yaml (CI payload entries) - Add HarnessConfigGenerator (generates appsettings from .epcfg) - Add HarnessServiceMapLoader (parses/filters by group) - Add HarnessSmokeTest (post-install health verification) - Add --demo flag to Installer.CLI (installs NLDR-side too) - Add publish-win-x64.ps1 (build + ZIP script) - Add appsettings.Installer.json (reference native config) - Add ADR-0001 through ADR-0008 (architecture decisions) - Add TESTERS-README.md (1150-line QA handoff guide) - Rewrite AGENTS.md (comprehensive AI assistant contex

Files: `.DS_Store`, `.gitignore`, `.kiro/specs/epacs-offline-installer/.config.kiro`, `.kiro/specs/epacs-offline-installer/design.md`, `.kiro/specs/epacs-offline-installer/requirements.md`, `.kiro/specs/epacs-offline-installer/tasks.md`, `.kiro/specs/epacs-sync-test-harness/.config.kiro`, `.kiro/specs/epacs-sync-test-harness/design.md`, `.kiro/specs/epacs-sync-test-harness/requirements.md`, `.qoder/.DS_Store` — and 456 more

**`6afe492`** 2026-05-14 — Standardize NuGet GitHub Packages token placeholder

Files: `NuGet.Config`

**`d203d67`** 2026-05-14 — Add standard NuGet GitHub package config

Files: `NuGet.Config`

**`e37765e`** 2026-05-08 — removed redundent

Files: `ePACS_SAD_v1.0.docx`, `ePACS_SAD_v1.0.pdf`, `ePACS_SAD_v1.1.docx`, `ePACS_SAD_v1.1.pdf`, `ePACS_SAD_v1.2.docx`, `ePACS_SAD_v1.2.pdf`

**`8609d95`** 2026-05-08 — Version 1.0 of the ePACS Offline installer

Files: `.DS_Store`, `.editorconfig`, `.gitignore`, `.qoder/.DS_Store`, `.qoder/plans/ePACS_Offline_Installer_Plan_ad7e33f1.md`, `AGENTS.md`, `Directory.Build.props`, `README.md`, `docs/AP_DDL.sql`, `docs/deletionsenerio.md` — and 480 more

**`d370526`** 2026-05-04 — Initial commit

Files: `.gitignore`, `README.md`

## How to run it

```bash
git clone <this repo> && cd l3_installer
git checkout r2-dev-stable
dotnet build ePACS.Installer.sln
dotnet test ePACS.Installer.sln
```

The database comes from the platform repo, not from here:

```bash
# in l2r2-platform-build
mysql -u root -p <empty_database> < db/stable_baseline_ddl.sql
```

It **refuses a non-empty schema** by design. Verify by counting, not by exit code:

```bash
mysql -u root -p -N -e "SELECT COUNT(*) FROM information_schema.tables \
  WHERE table_schema='<db>' AND table_type='BASE TABLE';"
```

## State READMEs — append, never fork

This file is generated ON `r2-dev-stable` and flows to every state branch through the sync merges, so state branches keep the full base context and history. A state branch that needs its own notes APPENDS a section **below this line** — never edits the generated body above — so the note survives regeneration and merges back cleanly when the state's work lands on stable:

```markdown
<!-- STATE APPENDIX (r2-dev-XX) — keep everything state-specific below this marker -->
```

---

*Generated by `build/generate-module-readmes.py` in the platform repo. Do not hand-edit: the next run overwrites it. Numbers above were measured when it ran, so re-run it after a state branch moves.*
