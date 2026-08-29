# ePACS Offline Installer + Sync Test Harness

> An installer **framework** for the ePACS ERP stack on offline Windows PACS nodes — a chassis
> that verifies a signed payload, prechecks the machine, extracts and deploys it, registers and
> orders Windows services, and monitors them afterwards. The stack it installs (application,
> MySQL, cache, eventing) is bundled with it, so an offline node runs no database that was not
> delivered and verified as part of the installation.

> ### Status — read this first
>
> **The framework runs end to end** as of 2026-08-29 — verification, prechecks, topology load,
> install and uninstall execute through a composed pipeline that checkpoints every phase. It
> builds clean on **.NET 10** (SDK 10.0.302, pinned in `global.json` to match the L2-R2
> workspace); CI builds and tests it on Linux and Windows.
>
> **Dry run is the default.** Nothing changes until you pass `--apply`.
>
> Since then: the **database bootstrap** (F3) initialises the bundled MySQL, imposes
> `stable_baseline_ddl.sql` and counts before and after; and **configuration** (F4) can describe
> N application services rather than one. All four structural gaps are closed.
>
> What is still missing is substantial: there is no payload bundling, no bootstrapper, and no
> upgrade, restore or repair engine. Those modes exit **4** with a message naming the missing
> engine, rather than returning 0 as they used to.
>
> **This README is hand-written.** `build/generate-module-readmes.py` in the L2-R2 workspace
> excludes this repo (`NOT_MODULES`) because its generated prose — state branches, `r2-dev-stable`,
> the module deployment story — is not true of a separate product on `master`. A generator run
> overwrote this file once, on 2026-08-29; the exclusion is what stops it happening again.
> **[`.kiro/specs/epacs-offline-installer/tasks.md`](.kiro/specs/epacs-offline-installer/tasks.md)
> carries a per-item audit** — what is built, what is partial, and what is not started — and is
> the only place to trust for status. In particular: there is no WiX bootstrapper, no payload
> build, no database bootstrap, and no upgrade, restore or repair engine.
>
> `harness/` is a **deliberate stand-in payload**, not the product. It exists so the chassis can
> be exercised before the real L2-R2 stack is pointed at it. `Pacs.Fas.Api` is a 435-line
> simulation of `l3_FAS`; it must never be installed onto a node that runs the real thing.

---

## What's in This Repository

| Component | Solution | Purpose | Status |
|-----------|----------|---------|--------|
| **Offline Installer** | `ePACS.Installer.sln` | The framework: verification, prechecks, topology, configuration, database bootstrap, install, uninstall, service orchestration, monitoring. Upgrade / repair / restore are designed but unimplemented. | ~12,300 LOC · 138 unit tests · no integration coverage |
| **Sync Test Harness** | `harness/ePACS.SyncHarness.sln` | Stand-in payload and protocol rig for PACS ↔ NLDR sync. **A simulation, not the product.** | ~3,836 LOC · 15 tests · 5 projects are empty shells |

Both target **.NET 10** on **Windows 10/11 x64** (offline, rural India) — matched to the
`r2-dev-stable` baseline of the L2-R2 platform, so an offline node carries one runtime and not
two.

---

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — the exact version is pinned
  in `global.json` (10.0.302). Do not override it; the analyser set is tied to the target
  framework, and an SDK mismatch turns into build errors rather than warnings.
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for harness local dev and integration tests)

### Build Everything

```bash
# Installer
dotnet build ePACS.Installer.sln
dotnet test ePACS.Installer.sln

# Harness
cd harness
dotnet build ePACS.SyncHarness.sln
dotnet test tests/Harness.ContractTests/Harness.ContractTests.csproj
```

### Run the Harness (Development)

```bash
cd harness

# Start infrastructure (Kafka + MySQL × 2 + Redis × 2)
docker compose -f docker/docker-compose.minimal.yml up -d

# Start services (each in a separate terminal)
dotnet run --project src/Pacs.Fas.Api          # http://localhost:5101
dotnet run --project src/Nldr.Api              # http://localhost:5201
dotnet run --project src/Pacs.SyncWorker       # outbox relay
dotnet run --project src/Nldr.SyncWorker       # ACK publisher

# Verify
curl http://localhost:5101/health/ready        # → 200
curl http://localhost:5201/health/ready        # → 200
```

### Create a Voucher (End-to-End Smoke)

```bash
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{
    "voucherNo": "VCH-2026-00001",
    "voucherDate": "2026-05-15",
    "voucherType": "CR",
    "narration": "Test voucher",
    "createdBy": "admin",
    "lines": [{"accountCode":"1001","debitAmount":0,"creditAmount":5000}]
  }' | jq .
```

This creates a voucher → writes to `sync_outbox` atomically → `Pacs.SyncWorker` relays to Kafka → `Nldr.Api` ingests → `Nldr.SyncWorker` publishes ACK → `Pacs.SyncWorker` marks ACKED.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Installer Package (Authenticode-signed EXE)          NOT BUILT  │
│  WiX v4 Burn + C# Managed BootstrapperApplication                │
│  Payloads: MySQL 8.4, Garnet, Kafka 3.7, JRE 17, app services    │
│  → see ADR-0001 (status: Proposed). No .wxs exists; today the    │
│    entry point is Installer.CLI, which has no composition root.  │
└─────────────────────────────────────────────────────────────────┘
         │ installs
         ▼
┌─────────────────────────────────────────────────────────────────┐
│  Runtime (per PACS node)                                         │
│  C:\Program Files\ePACS\current\                                 │
│  D:\ePACSData\ (mysql, cache, eventing, logs, config, files)    │
│                                                                  │
│  Windows Services:                                               │
│    MySQL → Garnet → Kafka → Pacs.Fas.Api → Pacs.Loans.Api →    │
│    Pacs.SyncWorker → Pacs.OperatorUi → InstallerAgent           │
│                                                                  │
│  (Demo mode adds: Nldr.Api → Nldr.SyncWorker → Nldr.Dashboard) │
└─────────────────────────────────────────────────────────────────┘
```

---

## Project Structure

### Installer (`src/`)

| Project | Purpose |
|---------|---------|
| `SharedKernel` | Configuration models, contracts, error handling abstractions |
| `Installer.Core` | State machine with checkpoint persistence (power-cut resilient), the **composition root** (`AddInstaller()`), the **pipeline** that drives a run, the `.epcfg` loader, and the cross-platform concurrency lock |
| `Installer.Actions` | Prechecks, payload extraction, service orchestration, harness integration |
| `Installer.Agent` | Always-on worker (health polling, disk monitoring, drift detection) |
| `Installer.CLI` | The entry point. Builds the composition root, runs the pipeline, maps outcomes to exit codes. Dry run by default. |
| `ManifestVerifier` | Authenticode signature + SHA-256 payload verification |
| `SupportBundle` | Diagnostics collector with PII redaction |
| `BackupRestore` | Backup skeleton. **The MySQL dump itself is a placeholder** (`BackupEngine.cs:253`); restore is unimplemented. |
| `Sync.Agent` | Connectivity detection and circuit breaker are real; the outbox relay and inbox routing are TODO shells |
| `Installer.Actions/Topology` | `ServiceMapLoader` — the framework's single topology input |
| `Installer.Actions/Database` | The database bootstrap: case-sensitivity guard, `my.ini` generation, initialise, accounts, baseline imposition, census |

### Harness (`harness/src/`)

| Project | Port | Purpose |
|---------|------|---------|
| `Harness.Common` | — | Shared contracts: envelope, hash, clock, fault hooks, options |
| `Pacs.Fas.Api` | 5101 | FAS voucher REST API (INSERT/UPDATE/DELETE with outbox) |
| `Pacs.Loans.Api` | 5102 | *(empty shell — 9 lines)* |
| `Pacs.SyncWorker` | 5103 | Outbox drain → Kafka, ACK consumer, heartbeat, file uploader |
| `Pacs.OperatorUi` | 5301 | *(empty shell — 7 lines)* |
| `Nldr.Api` | 5201 | Strict central receiver (12-step ingest pipeline) |
| `Nldr.SyncWorker` | 5203 | ACK publisher, command publisher, heartbeat consumer |
| `Nldr.DashboardUi` | 5401 | *(empty shell — 7 lines)* |
| `Harness.ScenarioPlayer` | — | *(empty shell — no source files)* |

---

## Running It

```bash
# What would happen — changes nothing. This is the default.
Installer.CLI --config=D:\site.epcfg --media=E:\media

# Do it
Installer.CLI --config=D:\site.epcfg --media=E:\media --apply

# Unattended rollout (no console output)
Installer.CLI --quiet --config=D:\site.epcfg --apply

# Remove, keeping business data
Installer.CLI --mode=uninstall --apply
```

Windows-style `/flag:value` works everywhere `--flag=value` does. Run `--help` for the full list.

**Exit codes** — these are an interface; the rollout tooling branches on them:

| | |
|---|---|
| 0 | Success |
| 1 | Precheck failure — a prerequisite was not met; **nothing was changed** |
| 2 | Operation failure |
| 3 | Health check failure after install |
| 4 | **Mode not implemented in this build — nothing was done.** Do not treat the node as upgraded, restored or backed up |
| 5 | Refused — another installer is running, or a required input was missing |
| 64 | Usage error |
| 99 | Unexpected error |
| 130 | Cancelled (Ctrl+C); the run is checkpointed and resumable |

### Components

Which infrastructure the medium carries and the node registers, from the `Components` section:

| | Default | Why |
|---|---|---|
| `Cache:Enabled` | **on** | Holds the idempotency keys (`fas:idem:`) that stop a retried request posting twice — a correctness dependency, not a performance one |
| `Cache:Provider` | `redis` | What L2-R2 actually runs. **Unresolved on Windows** — there is no official Redis build for it, and Garnet's Lua support is what ERPClient's rate limiter needs. See ADR-0002. |
| `Eventing:Enabled` | **off** | Kafka + JRE is ~290 MB, and no deployment in the L2-R2 estate provisions Kafka: no ansible role, no compose service, and every publisher behind the orchestration kill-switch |

A component that is off is **not registered**, rather than registered and left stopped — a
stopped Windows service looks like a failed install to every operator and monitoring tool that
sees it.

### One thing that will stop an install on Windows

The installer refuses to initialise MySQL where `lower_case_table_names=0` cannot be honoured,
which is every case-insensitive volume — NTFS by default. L2-R2 pins that setting deliberately
because 18 of 20 state captures spell at least one table in a different case from the baseline,
and MySQL fixes the value at initialisation with no way to change it afterwards. This is not
something the installer can work around; see `.kiro/specs/epacs-offline-installer/tasks.md`,
"F3 — where the runtime-target decision bites".

---

## Deployment Modes

| Mode | How | TestMode | NLDR |
|------|-----|----------|------|
| **Development** | Docker infra + `dotnet run` | true | localhost |
| **Full Docker** | `docker-compose.yml` | true | containerised |
| **Native Install** | `Installer.CLI /mode:install` | false | remote central |
| **Demo Install** | `Installer.CLI /mode:install /demo` | true | localhost |

### Publishing for Native Windows

```powershell
cd harness
.\scripts\publish-win-x64.ps1 -CreateZip

# Output:
#   publish/pacs/Pacs.Fas.Api.exe        (~80 MB each, self-contained)
#   publish/harness-pacs-win-x64.zip     (installer payload)
#   publish/harness-nldr-win-x64.zip     (demo-only payload)
```

---

## Testing

| Test Suite | Docker | Tests | Command |
|-----------|--------|-------|---------|
| Installer unit tests | No | **138** | `dotnet test ePACS.Installer.sln` |
| Installer integration tests | No | **1 placeholder** | (in the same solution) |
| Harness contract tests | No | **15** | `dotnet test harness/tests/Harness.ContractTests/` |
| Harness integration tests | Yes | 0 `[Fact]`s; infrastructure only | `dotnet test harness/tests/Harness.IntegrationTests/` |
| Harness chaos tests | Yes | **empty project** | — |
| Long offline soak | Yes | **empty project** | — |

There are **no integration tests that install anything**. Every claim in the sections above is
verified by inspection, not by execution.

---

## Key Principles

1. **Zero hardcoding** — every value from `appsettings.json` / `.epcfg` / environment variables
2. **Power-cut resilient** — every operation resumable from checkpoint (fsync'd state file)
3. **Offline-first** — no internet dependency after USB media delivery
4. **Structured logging** — Serilog via `Intellect.Erp.Observability` (IAppLogger, no PII)
5. **Typed errors** — YAML error catalog via `Intellect.Erp.ErrorHandling`
6. **Data preservation** — uninstall never deletes business data without governance token
7. **Tamper-evident** — SHA-256 payload hashing, Authenticode signing

---

## Documentation

| Document | Purpose |
|----------|---------|
| [AGENTS.md](AGENTS.md) | AI assistant guidance (full project context) |
| [harness/README.md](harness/README.md) | Harness developer guide (setup, run, test, contribute) |
| [docs/test-harness/TESTERS-README.md](docs/test-harness/TESTERS-README.md) | QA tester's guide (setup, execution, evidence, gotchas) |
| [docs/test-harness/00-design-overview.md](docs/test-harness/00-design-overview.md) | Authoritative harness design (~2000 lines) |
| [docs/adr/](docs/adr/) | Architecture Decision Records (ADR-0001 through ADR-0008) |
| [samples/](samples/) | Sample manifests, service maps, .epcfg files |

---

## License

Proprietary — Intellect Design Arena Ltd.
