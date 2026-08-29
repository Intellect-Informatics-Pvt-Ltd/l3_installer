# Tasks: ePACS Offline Installer

> **STATUS AUDITED 2026-08-29.** Every item below was re-checked against the code by locating
> the named type or file. Items previously marked `[x]` against code that does not exist have
> been corrected. **Do not re-tick an item without the artefact named in its evidence note.**
>
> **What this repository is.** An installer *framework* — a payload-agnostic chassis for
> installing a bundled ePACS stack (application + MySQL + cache + eventing) onto an offline
> Windows PACS node, with the database inside the bundle so no untrusted database sits outside
> the installation. `harness/` is a deliberate stand-in payload used to exercise the chassis
> before the real L2-R2 stack is pointed at it. It is **not** the product and must not ship to
> a node that runs the product.
>
> **Legend**
> - `[x]` — implemented; the named artefact exists and does the job
> - `[~]` — **partial**; the artefact exists but does not yet do the job (evidence note says how)
> - `[ ]` — **not implemented**; no code exists
>
> **Framework maturity:** ~10,200 LOC, **99 installer tests + 16 harness contract tests**, 0
> integration tests. **The product runs end to end** as of 2026-08-29: verification, prechecks,
> topology load, install and uninstall execute through a composed pipeline with a checkpoint at
> every phase. Payload bundling, the database bootstrap, and the upgrade/restore/repair engines
> are still absent — and the CLI now says so, with a distinct exit code, instead of returning 0.
>
> **Technology baseline (2026-08-29):** aligned to `r2-dev-stable`. **.NET 10** (`net10.0`, SDK
> pinned 10.0.302 via `global.json` — the same pin as the L2-R2 workspace), central package
> management, CI on Linux + Windows. Still to align, and blocked on the runtime-target
> decision: MySQL 8.4 with `lower_case_table_names=0`, Redis rather than Garnet (ADR-0002), and
> Kafka as a conditional payload rather than a mandatory 290 MB (ADR-0003).

---

## The four framework gaps that block the stated intent

These are not individual tasks — they are the structural items that decide whether the chassis
can accept the L2-R2 stack. Each is expanded in the phases below.

| # | Gap | Why it blocks the intent | Task refs |
|---|---|---|---|
| ~~**F1**~~ | ~~No composition root.~~ **CLOSED 2026-08-29.** `Installer.Core/DependencyInjection/AddInstaller()` assembles the graph; `Installer.Core/Pipeline/InstallerPipeline` drives verify → precheck → topology → install → checkpoint; `Installer.CLI` builds the host and maps outcomes to exit codes. **The product now runs end to end.** | Closed. See "F1 closure" below for the four defects the first execution exposed. | 12.x |
| ~~**F2**~~ | ~~No loader for the canonical service map.~~ **CLOSED 2026-08-29.** `Installer.Actions/Topology/ServiceMapLoader` (YamlDotNet), 17 tests, two of them contract tests against the maps that ship. | Closed. The pipeline loads the topology before the first mutation, so a bad map fails while the machine is still clean. | 8.6, 8.7 |
| **F3** | **No database bootstrap.** Nothing runs `mysqld --initialize`, writes `my.ini`, sets the root password, or creates the `healthcheck` user that `samples/service-map.yaml` pings. `BackupEngine.BackupDatabaseAsync` writes a text file that says "MySQL dump placeholder". | "Bundle the DB so nothing sits outside and nothing gets tampered with" is the reason this framework exists, and it is the part with no implementation. | 8.x (new 8.11), 15.2 |
| **F4** | **No config templates.** `ConfigGenerator` scans for `*.template.*`; **no such file exists in the repository**. `ServicesOptions` is a fixed six-property class (MySql, Cache, Eventing, Web, Sync, Agent) with no collection, so it cannot describe N application services. | Site-specific configuration generation is implemented but inert, and cannot express a multi-service payload. | 2.3, 2.8, 8.5 |

---

## F1 closure — what running it for the first time exposed

The composition root landed on 2026-08-29 and the product executed end to end for the first
time. Four defects surfaced within minutes, all of them in code that had been marked complete.
They are recorded here because the pattern matters more than the individual fixes: **every one
was invisible precisely because nothing had ever run.**

1. **`ValidateOnBuild` refused to build the container.** `IInstallerStateMachine` was registered
   as a singleton, but its constructor takes an `InstallerMode` and a target version — runtime
   values that come from `ModeDetector` and the verified manifest. Replaced with
   `IInstallerStateMachineFactory`, created once the mode and version are known.

   This forced a correction that turned out to matter on its own: **verification now precedes
   the first checkpoint.** The checkpoint stamps the target version and a recovery run reads
   that field to decide what to resume, so the version must come from a manifest whose signature
   and hashes have already been checked. Nothing has changed on the machine during verification,
   so there is nothing to lose by having no checkpoint yet.

2. **The concurrency guard did not guard.** See §6.4 — it never owned the mutex, and threw on
   release every single run.

3. **`/mode:upgrade` returned exit 0.** Having done nothing at all. On a PACS node that reads as
   "upgrade succeeded", and the next thing anyone does is decommission the old media. Modes with
   no engine now return **exit 4** with a message naming the missing engine and its task number.
   Backup says explicitly that a backup taken now would restore nothing.

4. **The `.epcfg` was never opened.** Every site-specific value the installer claimed to honour
   came from a default, including the PacsId it would have installed under.

### What the pipeline will not do

Worth stating positively, because refusing is most of what an installer should be good at:

| Situation | Outcome | Reason |
|---|---|---|
| Another installer holds the lock | 5, names the holder's PID | Two processes registering services is unrecoverable |
| Payload fails signature or hash | 2, before anything is touched | The only tamper-evidence in force (ADR-0001) |
| Any blocking precheck | 1, before anything is touched | A half-installed node is worse than none |
| Malformed service map | 2, before anything is touched | Loaded ahead of the first mutation on purpose |
| Mode with no engine | 4 | Never 0 — see defect 3 above |
| Install with no `.epcfg` | 5 | The node would install under a defaulted identity |
| Unsigned `.epcfg` | 64 unless explicitly allowed | It decides identity, data root, ports, backup targets |
| Purge without token + typed confirmation | 64 at the prompt | Caught before anything is stopped |
| Purge with a token | Refused by `DenyAllOverrideTokenValidator` | Validation is unimplemented; a permissive stub would expose an irreversible operation before its gate was written |
| Anything, without `--apply` | Dry run, 0 | Dry run is the default |

### Honesty carried in the output

The installer reports, in its own operator-facing text, that health verification is not
implemented and that a service starting without error is not the same as a service being
healthy. There is a test asserting that sentence is present. When §13.3 lands, the test fails
and the claim has to be updated with it — which is the point.

---

## Phase 0: Repository Scaffolding & Architecture Foundation

- [x] 1. Create solution structure and project scaffolding
  - [x] 1.1 Create the solution with all projects — 10 src projects + 2 test projects exist *(created on .NET 8; retargeted to net10.0 on 2026-08-29 — see X2)*
  - [~] 1.2 Create packaging directory structure — **only `config-templates/` and `error-catalog/` exist. No `wix/`, no `payloads/`, no `scripts/`.**
  - [x] 1.3 Create tests directory structure
  - [x] 1.4 Create samples directory (release-manifest.yaml, service-map.yaml, site-config-pack.epcfg)
  - [x] 1.5 Create docs directory with ADR folder structure
  - [x] 1.6 Create AGENTS.md
  - [x] 1.7 Create README.md — *exists; corrected 2026-08-29 to describe what is built*

- [~] 2. Define configuration models and appsettings schema
  - [x] 2.1 InstallerOptions (DataRoot, BinaryRoot, TempRoot, StateFile)
  - [x] 2.2 PrecheckOptions
  - [~] 2.3 ServicesOptions — **fixed six-property shape. No collection of application services, so a payload with N services cannot be described. See F4.**
  - [x] 2.4 MonitoringOptions
  - [x] 2.5 BackupOptions
  - [x] 2.6 LogRotationOptions
  - [x] 2.7 appsettings.json + appsettings.Production.json
  - [ ] 2.8 appsettings.template.json — **no `*.template.*` file exists anywhere in the repo. `ConfigGenerator` therefore has nothing to act on. See F4.**

- [x] 3. Define core data contracts
  - [x] 3.1 ReleaseManifest · [x] 3.2 SiteConfigPack · [x] 3.3 ServiceMap · [x] 3.4 InstallationState · [x] 3.5 HealthCheckResult

- [~] 4. Create error catalog and error handling infrastructure
  - [x] 4.1 `packaging/error-catalog/installer.yaml`
  - [x] 4.2 `packaging/error-catalog/core.yaml`
  - [ ] 4.3 Wire IErrorFactory and IErrorCatalog in DI — **`IInstallerErrorFactory` has no implementation, and there is no DI registration for the installer at all (F1).**
  - [x] 4.4 InstallerException subclasses — six subclasses present

---

## Phase 1: Installer Core — State Machine & Manifest Verification

- [~] 5. Implement ManifestVerifier — *the strongest component in the repo*
  - [x] 5.1 Release manifest YAML parser (YamlDotNet, underscored naming)
  - [~] 5.2 Signature verification — **detached CMS/PKCS#7 is real and checks chain + thumbprint + timestamp. `VerifyAuthenticode()` returns `Failure("not yet implemented")` on every call, including on Windows.**
  - [x] 5.3 SHA-256 payload hash verification, per file, against manifest
  - [ ] 5.4 USB media integrity check (full archive hash before extraction) — **no caller computes an archive-level hash.**
  - [x] 5.5 Unit tests — 15 facts across ManifestParserTests + HashVerifierTests

- [~] 6. Implement Installer.Core state machine
  - [x] 6.1 State enum · [x] 6.2 Checkpoint persistence (fsync'd state.json) · [x] 6.3 Recovery mode
  - [x] 6.4 Concurrent execution guard — **rewritten 2026-08-29, because the original did not guard.** It used `new Mutex(initiallyOwned: false, ...)` and treated `createdNew == true` as "acquired". That constructor creates the mutex *without owning it*, so nothing was ever held and two installers would both have proceeded to register services. Because nothing was owned, `ReleaseMutex()` then threw on **every** run — caught and logged as a warning, which is how a guard that never worked stayed invisible for a year. Now an exclusive lock file (`FileShare.None`, the only share mode that excludes on Unix as well as Windows) with the holder's PID in a readable sidecar. 7 tests.
  - [x] 6.5 Mode detection (fresh/upgrade/repair from junction target)
  - [ ] 6.6 Unit tests for state transitions, checkpoint persistence, recovery — **no state machine test file exists.**

- [~] 7. Implement Installer.Actions — Precheck Suite
  - [x] 7.1 OS version · [x] 7.2 Disk space · [x] 7.3 RAM · [x] 7.4 Port availability · [x] 7.5 Admin rights · [x] 7.6 Pending reboot
  - [ ] 7.7 AV exclusion detection — **no class.**
  - [x] 7.8 Existing installation detection — implemented in `ModeDetector`, not as an `IPrecheck`
  - [~] 7.9 `.epcfg` signature validation — **presence is now gated; cryptographic verification is not.** `SiteConfigLoader` refuses a pack whose signature is absent or is the sample's `BASE64_ENCODED_SIGNATURE_PLACEHOLDER`, unless `--allow-unsigned-config` is passed (which logs a warning naming the risk). Real verification needs a canonical serialisation of the document minus the signature field, plus a byte-oriented overload on `ISignatureVerifier`, which today takes file paths. **Do not tick this until that exists** — the current gate stops an accident, not an attacker.
  - [ ] 7.10 Temp staging relocation — **`ResolvedTempRoot` is a default path; there is no threshold check and no relocation.**
  - [~] 7.11 Unit tests per precheck — **5 facts in `PrecheckRunnerTests`; the individual checks are not covered.**

- [~] 8. Implement Installer.Actions — Fresh Install
  - [x] 8.1 Data root creation with subdirectories from config
  - [ ] 8.2 NTFS ACL application — **`DataRootInitializer.cs:10` says "handled separately"; `IAclEngine` has no implementation.**
  - [x] 8.3 Payload extraction, resumable via a progress manifest
  - [x] 8.4 Binary deployment, side-by-side `releases/<ver>/` + `current` link — *note: the flip is delete-then-create, which is **not** the atomic commit the design claims; see 17.6*
  - [~] 8.5 Config generation from templates — **`ConfigGenerator` is implemented and generic (token substitution, atomic write-then-rename), but no template file exists for it to process. Inert until 2.8.**
  - [x] 8.6 Windows service registration from a service map, with recovery actions
  - [x] 8.7 Service start in dependency order
  - [ ] 8.8 Firewall rules — **`IFirewallManager` has no implementation.**
  - [ ] 8.9 Windows Update reboot suppression — **no code.**
  - [ ] 8.10 Integration tests for fresh install — **`Installer.IntegrationTests/UnitTest1.cs` is one placeholder fact.**
  - [ ] **8.11 (NEW) Database bootstrap.** Initialize the bundled MySQL data directory, generate `my.ini` from configuration, set the root password from the generated secret, create the `healthcheck` user the service map's health check authenticates as, and impose the baseline schema. **See F3 — this is the core of the bundling intent and none of it exists.**

- [~] 9. Implement Installer.Actions — Uninstall
  - [x] 9.1 Stop in reverse order · [x] 9.2 Deregister · [x] 9.3 Binary removal · [x] 9.4 Data preserved by default
  - [~] 9.5 Governance token verification for purge — **flow and typed-confirmation check are written, but `IOverrideTokenValidator` has no implementation, so `UninstallAction` cannot be constructed.**
  - [ ] 9.6 Final support bundle before removal — **`UninstallAction` does not reference the collector.**
  - [ ] 9.7 Unit tests for uninstall flow and token verification

- [~] 10. Implement Installer.Agent (v1)
  - [x] 10.1 Worker with configurable loop · [x] 10.2 Health polling · [x] 10.3 Disk monitoring · [x] 10.4 Log rotation · [x] 10.6 Config drift (SHA-256)
  - [ ] 10.5 Support bundle auto-generation on critical failure — **the agent never references the collector.**
  - [ ] 10.7 Clock drift detection — **no monitor. The five registered monitors are DiskSpace, ConfigDrift, LogRotation, FileSync, Heartbeat.**
  - [ ] 10.8 Unit tests per monitor

- [~] 11. Implement SupportBundle collector
  - [~] 11.1 Log collection with redaction — **local regex redaction (password, connection string, Aadhaar, phone). Not `IRedactionEngine` from `Intellect.Erp.Observability` as AC-6.3 requires.**
  - [x] 11.2–11.6 Service status, versions, OS/disk/RAM, config, correlation filtering
  - [~] 11.7 Encrypted ZIP packaging — **`ZipFile.CreateFromDirectory`, plaintext. No encryption.**
  - [ ] 11.8 Unit tests for redaction and packaging

- [~] 12. Implement Installer.CLI (silent mode)
  - [x] 12.1 CLI argument parser — rewritten as a testable `CliOptions` type. Accepts both `/flag:value` and `--flag=value`; an unrecognised argument is an **error**, never ignored (a silently dropped `--apply` means an operator believes they installed something and did not). 16 tests.
  - [x] 12.2 `.epcfg` loading and validation — `Installer.Core/SiteConfig/SiteConfigLoader`. Validates required fields and constrains the `state_code` shape, because that value becomes `ASPNETCORE_ENVIRONMENT` in every service and a wrong one runs another state's configuration *without failing*. 12 tests.
  - [x] 12.3 Exit code mapping — from a `PipelineOutcome` enum, not from string-matching exception messages. Added **4 (mode not implemented)** and **5 (refused)**; 64 usage, 130 cancelled. Every code is documented in `--help`, and a test asserts that.
  - [~] 12.4 File-only logging in quiet mode — **quiet now genuinely emits nothing**, but there is still no file sink. Wiring Serilog via `Intellect.Erp.Observability` remains open; telling an operator to "see the log file" when none exists would be worse than saying there is none yet.
  - [ ] 12.5 Integration tests for silent install — still none; needs a VM (W9).
  - [x] **12.6 Composition root.** `AddInstaller()` + `InstallerPipeline`, built with `ValidateOnBuild` — which caught a real defect on its first run. See F1 closure.
  - [x] **12.7 (NEW) Dry run is the default.** Every mode reports what it would do and changes nothing until `--apply`, mirroring the estate's own `ops/l2r2` contract. The pipeline stops at the last read-only point — after verification, prechecks and topology load.

- [~] 13. Implement health endpoints and smoke test
  - [~] 13.1 Health endpoint contract — **defined in the service map as `/health/live` + `/health/ready`. No payload in this repository or in L2-R2 serves those paths.**
  - [~] 13.2 Smoke test runner — **`HarnessSmokeTest` polls health endpoints. It does not create/verify/delete a test record as AC-1.8 requires.**
  - [ ] 13.3 Health check aggregator — the pipeline reaches the Health phase and states plainly, in its own operator-facing output, that health verification is not implemented and that a service starting without error is not the same as a service being healthy. A test asserts that sentence is present, so the claim changes on the day the aggregator lands.
  - [ ] 13.4 Unit tests for health aggregation

- [~] 14. Create AGENTS.md and documentation
  - [x] 14.1 AGENTS.md
  - [~] 14.2 ADR-0001 through ADR-0006 — **written; ADR-0001 (WiX Burn) describes a component that does not exist and was moved to Proposed on 2026-08-29.**
  - [ ] 14.3 operator-quick-start.md — **not present.**
  - [ ] 14.4 security-baseline.md — **not present.**

---

## Phase 2: Upgrade, Backup, Restore, Repair

- [~] 15. Implement Backup Engine
  - [x] 15.1 `IBackupEngine` + `BackupManifest` model
  - [ ] 15.2 MySQL logical backup — **`BackupDatabaseAsync` writes `mysql-dump.sql` containing the literal text "-- MySQL dump placeholder". No `mysqldump` is invoked. See F3.**
  - [ ] 15.3 Attachment backup (tar + per-file SHA-256) — TODO at `BackupEngine.cs:307`
  - [x] 15.4 Config backup — real file copy. *Keys backup copies metadata only.*
  - [ ] 15.5 Sync state export — placeholder JSON at `BackupEngine.cs:300`
  - [ ] 15.6 Backup encryption (AES-256-GCM) — **no code.**
  - [ ] 15.7 Backup manifest signing — `ManifestSigned = false // TODO`
  - [x] 15.8 Backup target validation (exists, writable, space) — real
  - [ ] 15.9 Backup verification — `ManifestSignatureValid = false`, `DumpReadable = true` are hardcoded
  - [ ] 15.10 Unit tests

- [ ] 16. Implement Restore Engine — **`IRestoreEngine` declared; zero implementing types. 16.1–16.8 all unimplemented.**

- [ ] 17. Implement Upgrade Engine — **`IUpgradeEngine` declared; zero implementing types. 17.1–17.9 all unimplemented.**
  - Note for 17.6: the "atomic junction flip" the interface documents is not achievable with
    `BinaryDeployer.SwitchCurrentAsync`'s delete-then-create sequence. A power cut between the
    two leaves no `current` at all. Design a rename-based swap before implementing.

- [ ] 18. Implement Repair Mode — **no repair code exists. 18.1–18.6 unimplemented.**

- [ ] 19. Implement Schema Fingerprinting — **`ISchemaFingerprinter` declared; zero implementing types. 19.1–19.6 unimplemented.** Upgrade (17.8) and repair both depend on this; implement it first.

---

## Phase 3: Offline Sync Hardening

> **Open question before any further work here (see ADR-0006).** The NLDR counterparty this
> phase targets does not exist anywhere in L2-R2 — "NLDR" appears in no file outside this
> repository. Confirm the programme exists and its timeline before spending further effort;
> otherwise flag this phase off and keep `harness/` as a protocol rig only.

- [~] 20. Implement Outbox Relay (MySQL → Kafka)
  - [x] 20.1 `IOutboxRelay` interface
  - [ ] 20.2–20.5 Poller, producer, Kafka-down handling, checkpoint — **`OutboxRelay.cs:36` is `// TODO: Actual MySQL query + Kafka publish implementation`. The class is a shell.**
  - [ ] 20.6 Unit tests

- [~] 21. Implement Sync Agent
  - [x] 21.1 Connectivity state machine · [x] 21.2 HTTPS probe · [x] 21.3 Circuit breaker (threshold, half-open, cooldown)
  - [ ] 21.4 Chunked upload with per-chunk ACK — **`ISyncTransport` has zero implementations.**
  - [ ] 21.5 Bandwidth detection / adaptive chunk sizing — **config values exist; no logic.**
  - [ ] 21.6 Sync priority queue · [ ] 21.7 Dead-letter handling · [ ] 21.8 Durable MySQL checkpoint
  - [ ] 21.9 Unit tests for circuit breaker and retry

- [~] 22. Implement Inbox Processing (NLDR → PACS)
  - [x] 22.1 `IInboxProcessor` + implementation shell
  - [ ] 22.2 Idempotent apply · [ ] 22.3 Conflict resolution · [ ] 22.4 Command handler — **`InboxProcessor.cs:110` is `// TODO: Route to appropriate handler based on EventType`.**
  - [ ] 22.5 Unit tests

- [ ] 23. Implement Reconciliation — **`IReconciliationEngine` declared; zero implementing types. 23.1–23.5 unimplemented.**

---

## Phase 4: Security Hardening

- [ ] 24. Implement Signing and Verification Pipeline — **`ICodeSigner` declared; zero implementing types. 24.1–24.5 unimplemented.** Note this is the *producing* side; the *verifying* side (5.2 detached CMS) is real.

- [ ] 25. Implement Access Control (ACL) Engine — **`IAclEngine` declared; zero implementing types. 25.1–25.5 unimplemented.**

- [ ] 26. Implement Firewall Rules Engine — **`IFirewallManager` declared; zero implementing types. 26.1–26.5 unimplemented.**

- [~] 27. Implement Audit Log Hash Chaining
  - [x] 27.1 `IAuditChain` interface · [x] 27.2 Hash chain over critical events · [x] 27.3 Chain verification
  - [ ] 27.4 Unit tests — **none. This is tamper-evidence code with zero test coverage.**

- [~] 28. Implement Secret Management
  - [x] 28.1 `ISecretStore` interface · [x] 28.2 Credential generation · [x] 28.3 Encryption at rest · [x] 28.4 Rotation support
  - [ ] 28.5 Secret-scan validation — **no scanner.**
  - [ ] 28.6 Unit tests — **none. This is the code that generates and stores the database password.**

---

## Cross-cutting items the original plan did not carry

- [ ] **X1. Create the Windows service accounts.** `ServiceOrchestrator` registers services with
  `obj= ".\ePACSDbSvc"` and friends, but nothing creates those local accounts or sets their
  passwords. Registration will fail on a clean machine.

- [x] **X2. Build on the estate SDK, on the estate's framework.** *Done 2026-08-29.* Both
  solutions now target **`net10.0`** and build clean (0 warnings, 0 errors) on SDK **10.0.302**,
  the same pin as the L2-R2 workspace. What this took, and what it found:
  - `global.json` added, pinning 10.0.302 / `rollForward: latestFeature` — previously the SDK
    was whatever the machine had.
  - `AnalysisLevel` moved to `10.0-recommended` **in the same commit as the TargetFramework**,
    so the analyser set is tied to the code and not to the build agent.
  - **58 `CA1873` sites converted to `[LoggerMessage]` source generation** across 27 files, with
    a `LogEvents` class per project and a documented EventId map (see
    `src/Installer.Actions/Logging/LogEvents.cs`). This is a supportability win as much as a
    performance one: an installer is diagnosed from a bundle by someone who was not there, and
    a stable EventId survives message rewording.
  - Two CA1873 sites were **real defects, not noise**: `Worker` held its monitors as a lazy
    `IEnumerable<IMonitor>` and re-enumerated it every loop iteration for the life of the
    installation (now materialised once); `FileSyncMonitor` summed the size of every pending
    file unconditionally just to format one log line (now hoisted).
  - Per-project `TargetFramework` declarations removed from all 22 csprojs — they were silently
    overriding `Directory.Build.props`, which is why the test projects stayed on net8.0 through
    an earlier retarget attempt.
  - **Central Package Management introduced** for the installer solution
    (`Directory.Packages.props`). Versions had already drifted between the two solutions
    (Test.Sdk 17.6.0 vs 17.10.0, xunit 2.4.2 vs 2.9.0).
  - **NU1510:** `Microsoft.Extensions.Diagnostics.HealthChecks` and `System.Text.Json` removed
    from nine harness projects — both ship in the net10.0 shared framework now.
  - **NU1903 (security):** Testcontainers 3.9.0 pulls SSH.NET transitively, and **every release
    up to and including 2025.1.0 carries GHSA-q939-rpr3-3284 (HIGH)** — ScpClient arbitrary
    file write. Pinned transitively to the first patched release, **2026.0.0**. See the note in
    `harness/Directory.Packages.props`: that version is not yet in the estate's offline mirror
    and must be added, or a disconnected build host cannot restore.

- [x] **X3. CI that builds something installable.** *Done 2026-08-29.* New
  `.github/workflows/ci.yml` restores (which is also the dependency-audit gate, since
  `NuGet.Config` sets `auditLevel=high`), builds both solutions in Release on **Linux and
  Windows**, runs every test that can run without Docker, and then — on a real Windows runner —
  produces the **self-contained single-file win-x64** publish that ADR-0007 §1 describes, and
  prints the artefact sizes so media growth is visible before it is a problem. `build.yml` keeps
  its NuGet-packaging job and now carries a scope note saying it is not the gate.

  Still not covered, deliberately and stated rather than implied: the integration, chaos and
  long-offline suites need Docker and are not wired in (W9), and two of those projects are
  still empty.

- [ ] **X4. A guard against this file drifting again.** A check that fails when an item is
  marked `[x]` and the type or file its evidence note names does not exist.

- [ ] **X5. Register `l3_installer` in the workspace.** It is absent from
  `ops/l2r2 bootstrap clone` and from `Intellect.L2R2.sln`, so it is not part of the estate's
  build or CI.
