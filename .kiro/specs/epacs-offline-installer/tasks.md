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
> **All four structural gaps (F1-F4) are closed as of 2026-08-29.** What remains is scope, not
> shape: the payload bundling, the upgrade/restore/repair engines, and the runtime-target
> decision that F3 forces.
>
> **Framework maturity:** ~12,300 LOC, **138 installer tests + 16 harness contract tests**, 0
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
| ~~**F3**~~ | ~~No database bootstrap.~~ **CLOSED 2026-08-29** for install. `Installer.Actions/Database/MySqlBootstrapper` initialises the data directory, writes `my.ini`, sets the root password from a generated secret, creates the application and health-check accounts, imposes `stable_baseline_ddl.sql`, and **counts before and after**. Guarded by `TableNameCaseGuard`, which refuses outright where the estate's `lower_case_table_names=0` cannot be honoured. *(15.2 — the backup dump — is still a placeholder and is separate.)* | Closed for install. **The guard is where the runtime-target decision bites hardest — see below.** | 8.11, 15.2 |
| ~~**F4**~~ | ~~No config templates.~~ **CLOSED 2026-08-29.** `ServicesOptions.Applications` is a keyed collection, so N services are expressible; `ConfigGenerator` addresses any of them as `${Service:<name>:Port}`; `packaging/config-templates/appsettings.Site.template.json` exists and is contract-tested. Unresolved tokens are now **fatal**. | Closed. **All four structural gaps are now closed** — see "F4 closure" below for the three defects it exposed. | 2.3, 2.8, 8.5 |

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

## F4 closure — and where it bites

F4 removed the last structural ceiling: the configuration model could name one application, and
L2-R2 is twenty-six. Three things came out of building it.

### 1. Every path token produced invalid JSON

`${DataRoot}` expands to `D:\ePACSData`. Substituted raw into a JSON string that becomes
`"D:\ePACSData\\logs"` — and `\e` is not a valid escape, so **the whole file fails to parse**.
Every path token on the target platform hit this, and the first thing that would have noticed is
a service refusing to start on a node in a village.

Found by a test asserting the shipped template's Serilog path — which is exactly the class of
defect that only appears once something actually runs the code. Values are now JSON-escaped
where the output is JSON, the generated file is **parsed before it is written**, and non-JSON
outputs (`my.ini`, `kafka.properties`, `garnet.conf`) are deliberately left unescaped.

### 2. The installer could not tell a service which state it serves

`ASPNETCORE_ENVIRONMENT=<STATE>` selects `appsettings.<STATE>.json` inside every L2-R2 service.
It is the entire mechanism by which one codebase serves every state, and the estate's own
systemd template says what happens when it is wrong: *"Getting it wrong does not fail — it runs
the wrong state's configuration, silently, which is far worse."*

**This installer had no way to express it.** `ServiceMapEntry` had no environment field, and
`sc.exe` has no verb for environment variables — the Service Control Manager reads them from a
`REG_MULTI_SZ` value under the service's own registry key. Now implemented, and a failure to
apply them aborts registration rather than leaving a service that starts and serves the wrong
state.

### 3. The log path override is mandatory, not advisory — and this is the bite

Roughly fifteen L2-R2 source files hardcode a Linux path as a **fallback**, e.g.
`l3_PACDetailsAPI/Common/Constants/AppConstants.cs:8` and `l3_FAS/FAS/Program.cs:283`:

```csharp
var filePath = serilogFile.GetValue<string>("Path") ?? "/data/L3-logs/r2-dev/l3_FAS/fas.log";
```

Being a fallback is what makes it dangerous. On Windows with no override the service **does not
fail** — it resolves a path that cannot exist and logging silently stops. The first anyone knows
is a support bundle with no application logs in it, taken because something else went wrong.

So `appsettings.Site.template.json` must set `Serilog:WriteTo` for every service on every node,
and a test asserts the shipped template does. But note what that means: **the installer is
compensating in configuration for a hardcoded assumption in twenty-six modules.** That works,
and it is the right thing to do now, but it is a workaround. The modules should take their log
root from configuration with no platform-specific literal behind it — which is module work in
the L2-R2 estate, not installer work.

The same applies to the two report controllers that shell out to `/bin/bash` under an nginx
`/app` layout (`l3_FAS/.../FASReports.cs:922`). No configuration can compensate for that one.

---

## F3 — where the runtime-target decision bites

F3 was built with **Redis as the cache default and Kafka conditional**, as instructed. Three
things surfaced that the Gate-0 decision has to answer. None of them is something the installer
can engineer around.

### 1. MySQL cannot honour the estate's table-name case setting on Windows

> **CORRECTED 2026-08-29, the same day it was written.** The first version of this section said a
> case-folding server would collapse case-differing baseline tables. That was wrong, and it was
> wrong in the direction that makes a decision look forced. Measured:
> `db/stable_baseline_ddl.sql` declares **1,189 table names and zero of them collide when folded
> to lower case** — the baseline applies identically either way.
>
> The estate's "18 of 20 captures" figure, which the original text leaned on, is
> [README.md:1170](../../../README.md) and describes something else: **one** table, `DB_Names`
> vs `db_names`, where an existing **state capture** disagreed with the baseline and produced
> `ERROR 1146` when migrated on a Linux server. It was fixed. A fresh PACS node imposes the
> baseline; it does not migrate a capture, so that failure mode does not reach it.

**What is actually true.** MySQL refuses to initialise with `lower_case_table_names=0` on a
case-insensitive file system, and the value is fixed at initialisation — it cannot be changed
afterwards. NTFS is case-insensitive by default. That part is a property of MySQL and cannot be
worked around by the installer or by packaging.

**What that costs.** On `lower_case_table_names=1` MySQL *stores* identifiers folded, so the
node's `information_schema` reports `db_names` where every Linux node reports `DB_Names`. Nothing
fails on day one. What you inherit is a permanent divergence:

  * **Schema fingerprinting** (§19) compares a node against the baseline, so every mixed-case
    identifier reads as drift. Either the fingerprint normalises case — and stops detecting real
    case drift — or it reports noise on every node.
  * **Cross-platform dump and restore is lossy in both directions**, and neither direction fails
    loudly.
  * **SQL case errors are masked.** A query referencing `voucherdetails` works here and fails on
    every Linux server.

**What the installer does.** Refuses by default, and names `AcceptCaseFolding` as the way past
it. Not because a folded node is broken, but because the choice is irreversible and its cost is
invisible — an irreversible divergence should be chosen, not defaulted into.

**Severity, honestly:** this is a *governed divergence*, not a blocker. It should not by itself
decide the runtime target. `TableNameCaseGuard` probes the actual data directory by experiment
rather than inferring from the OS, because Windows supports per-directory case sensitivity and a
network share can be either.

### 2. Redis has no official Windows build — and Garnet is not a free substitution

`Components:Cache:Provider` defaults to `redis`, matching what L2-R2 actually runs
(`StackExchange.Redis 2.10.1`). The cache is not optional: the ansible role puts it plainly —
it holds the idempotency keys (`fas:idem:`) that stop a retried request posting twice, *"which
is a correctness concern, not a performance one"*.

There is no official Redis build for Windows. ADR-0002 chose Garnet for exactly that reason,
and that reasoning is sound. But `l3_ERPClient/Security/RateLimiting/RedisRateLimitStore.cs`
uses `LoadedLuaScript` (`SCRIPT LOAD` / `EVALSHA`) for sliding-window and concurrency limiting.
Server-side Lua is Garnet's weakest compatibility area, and that consumer is a **security
control**, not a cache.

`samples/service-map.yaml` still names `GarnetServer.exe` and now carries a prominent
`⚠ UNRESOLVED` note saying so. **What has to happen before that line is correct:** run the
estate's own `utils-caching` and `RedisCaching.Tests` suites, plus ERPClient's rate-limiter
tests, against a Garnet instance, and record the result in ADR-0002.

### 3. Kafka is a 290 MB payload for a feature nothing provisions — now conditional

`Components:Eventing:Enabled` defaults to **false**. Kafka (110 MB) plus the JRE it needs
(180 MB) is ~290 MB on every USB stick, and measured against the estate: there is no Kafka role
in `ops/ansible`, no Kafka service in `ops/compose`, and no mention of Kafka anywhere in `ops/`.
The client library is referenced by FAS and Loans, but every publisher sits behind the
orchestration kill-switch (`FAS/ServiceRegistration.cs:286`).

A component that is off is **not registered** rather than registered and left stopped — a
stopped Windows service looks like a failed install to every operator and monitoring tool that
sees it. Turning eventing on is one config flag, and it enlarges the medium deliberately.

---

### What the pipeline will not do

Worth stating positively, because refusing is most of what an installer should be good at:

| Situation | Outcome | Reason |
|---|---|---|
| Another installer holds the lock | 5, names the holder's PID | Two processes registering services is unrecoverable |
| Payload fails signature or hash | 2, before anything is touched | The only tamper-evidence in force (ADR-0001) |
| Any blocking precheck | 1, before anything is touched | A half-installed node is worse than none |
| Malformed service map | 2, before anything is touched | Loaded ahead of the first mutation on purpose |
| Mode with no engine | 4 | Never 0 — see defect 3 above |
| Data volume cannot host `lower_case_table_names=0` | 1, before anything is touched | The setting is fixed at initialisation; a node built wrongly has to be flattened |
| MySQL data directory already populated | 1, before anything is touched | Re-initialising would destroy a society's books |
| Baseline applied but the table count did not move | 2 | rc=0 is not evidence — the estate has been bitten twice |
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
  - [x] 2.3 ServicesOptions — infrastructure stays named (a buffer pool is not a heap size is not a chunk size), and `Applications` is a case-insensitive keyed collection of `ApplicationServiceOptions` (port, start order, account, optional health path). The old fixed shape could describe the stand-in payload in `harness/`; it could not describe 26 services, which made it a hard ceiling on the whole bundling intent.
  - [x] 2.4 MonitoringOptions
  - [x] 2.5 BackupOptions
  - [x] 2.6 LogRotationOptions
  - [x] 2.7 appsettings.json + appsettings.Production.json
  - [x] 2.8 `packaging/config-templates/appsettings.Site.template.json` — the site overlay an ERP service reads. Contract-tested: a test resolves the shipped template end to end and fails if any token in it is one the generator cannot supply. The old `appsettings.Production.json` used `${epcfg:...}` tokens but was never named `*.template.*`, so it had been inert since it was written; it is now retired in place with a note explaining why.

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
  - [x] 8.5 Config generation from templates — reworked. Four token namespaces (`${DataRoot}`, `${epcfg:field}`, `${Services:MySql:Port}`, `${Service:l3_FAS:Port}`), values JSON-escaped where the output is JSON, the result parsed before it is written, and **an unresolved token aborts generation** listing every one at once. Previously it logged a warning and left `${...}` in the file.
  - [x] 8.6 Service registration from a service map, with recovery actions — **two implementations since 2026-08-29** (ADR-0010): `ServiceOrchestrator` (Windows, `sc.exe` + a registry write for the environment) and `SystemdServiceOrchestrator` (Debian, unit files + `systemctl`). Platform chosen at composition time; a third platform gets `UnsupportedPlatformServiceOrchestrator`, which throws *when used* rather than when resolved so the graph still validates and a dry run still works on a developer's machine.
    - The systemd units are contract-tested against the properties `ops/ansible/roles/deployapp/templates/l2r2-service.service.j2` carries — tier restart policy, `LimitNOFILE=65535`, the modest `ProtectSystem=full` hardening, journal output. Two unit shapes for the same 26 services is how an offline node comes to behave differently from the estate under load.
    - **Defect found and fixed on the way.** The shipped service map puts `${Services:Web:HttpsPort}` in ePACSWeb's arguments and `${Services:MySql:Port}` in MySQL's health check, and the Windows orchestrator substituted only `${BinaryRoot}` and `${DataRoot}`. Kestrel would have been handed a literal `${...}` as its URL. Token vocabulary is now shared (`InstallerTokenMap`) between both orchestrators and `ConfigGenerator`, and an unknown token aborts registration. It had never failed because it had never run.
  - [x] 8.7 Service start in dependency order
  - [ ] 8.8 Firewall rules — **`IFirewallManager` has no implementation.**
  - [ ] 8.9 Windows Update reboot suppression — **no code.**
  - [x] **8.12 (NEW) Per-service environment variables.** `ServiceMapEntry.Environment`, parsed by the topology loader and applied by `ServiceOrchestrator.ApplyEnvironmentAsync`. **This is the state-selection mechanism and the installer had no way to express it at all**: `ASPNETCORE_ENVIRONMENT=<STATE>` selects `appsettings.<STATE>.json` inside every L2-R2 service. Note `sc.exe` has no verb for environment variables — the Service Control Manager reads them from a `REG_MULTI_SZ` value under the service's own registry key, so that is what is written. A failure to set them **aborts registration**: a service that starts without its environment serves the wrong state's configuration and never fails.
  - [ ] 8.10 Integration tests for fresh install — **`Installer.IntegrationTests/UnitTest1.cs` is one placeholder fact.**
  - [x] **8.11 Database bootstrap.** `Installer.Actions/Database/`. Ordered by what is irreversible: refuse if the volume cannot host `lower_case_table_names=0` → write `my.ini` → initialise (**irreversible**) → start → set root password → create accounts → impose `db/stable_baseline_ddl.sql` → count. 20 tests.
    - **Counted, never assumed.** The bootstrap fails if the table count did not move after applying the baseline. `ops/README.md` states the rule and why: *"this estate has twice had a seed return rc=0 while landing zero rows."* A MySQL client exiting 0 having applied nothing is exactly that.
    - **Least privilege.** The application account is scoped to one database and denied DDL — schema changes arrive through the installer, not from a running service. The health-check account gets `USAGE` and nothing else, because its credentials sit in `service-map.yaml`, which is readable on the box. Anonymous accounts are deleted.
    - **No password on a command line.** Passwords reach MySQL through `MYSQL_PWD` and SQL through stdin; any user on the box can read the process table. `ProcessRunner` also redacts known secrets from captured output, because that output is what an operator pastes into an email when an install fails.
    - **Durability is not tunable in practice.** `innodb_flush_log_at_trx_commit=1`, `sync_binlog=1`, `innodb_doublewrite=ON`. A PACS node loses power — that is the premise of the product, not an edge case.
    - **Refuses to re-initialise a populated data directory.** That would destroy a society's books.
    - Found and fixed on the way: the `${DataRoot}\mysql\data` defaults use Windows separators, so on Linux and macOS the whole path collapsed into one literal directory — CI was exercising a different path shape from the target. Now normalised on expansion.

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

- [~] 24. Implement Signing and Verification Pipeline
  - [x] 24.1–24.3 `CmsCodeSigner` — detached CMS over the release manifest, SHA-256 pinned explicitly (the default has moved between .NET versions and a signature whose digest depends on the SDK is not reproducible), whole chain embedded (an offline node cannot fetch an intermediate from an AIA URL).
  - [x] 24.4 Chain validation — with revocation deliberately **off**: a CRL or OCSP fetch cannot succeed on an air-gapped node, so revocation must be enforced where the medium is *built*, not where it is installed.
  - [x] 24.5 Unit tests — 17, including every tamper case.
  - [ ] **Authenticode over the outer package** — still unimplemented, still deferred to a WiX bootstrapper that does not exist (ADR-0001). Detached CMS over the manifest remains the only tamper-evidence in force.
  - [ ] **The signing ceremony** — Gate G3. Who holds the EV certificate, where the key lives, who may invoke it. `CmsCodeSigner` is deliberately agnostic (a `Func<X509Certificate2?>`, so a PFX, an `X509Store` or an HSM/Key Vault CNG provider all work) precisely so that answer can change without touching code.

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

- [x] **X6 (NEW). The media pipeline — W6.** `src/Installer.MediaBuilder` (`epacs-media`): stage payloads, **measure** every SHA-256, write the manifest, sign it, and then verify the assembled medium with `ManifestVerificationService` — **the installer's own verifier, not a reimplementation**, so "the build passed" and "the node will accept this" are the same statement. CI assembles and verifies a medium on every run. 17 tests. Replaces `samples/release-manifest.yaml`, whose hashes were hand-typed for payloads no build produced.
  - Composition comes from `samples/media-spec.yaml`, a reviewable file rather than command-line flags, filtered by the same component groups as the service map — so a disabled component is not carried at all.
  - The output directory is **cleaned**, because a medium assembled over a previous build can carry a payload the manifest no longer lists: that verifies fine (verification checks listed payloads are present, not that present payloads are listed) and installs something nobody intended.
  - The manifest is **byte-reproducible** — no build timestamp — so two runs of one release produce the same manifest and the same signature, and "did this medium change?" is answerable by comparing hashes.
  - An unsigned build exits **2**, so a pipeline cannot mistake it for releasable.

- [ ] **X4. A guard against this file drifting again.** A check that fails when an item is
  marked `[x]` and the type or file its evidence note names does not exist.

- [ ] **X5. Register `l3_installer` in the workspace.** It is absent from
  `ops/l2r2 bootstrap clone` and from `Intellect.L2R2.sln`, so it is not part of the estate's
  build or CI.
