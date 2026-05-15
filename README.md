# ePACS Offline Installer + Sync Test Harness

> Production-grade, signed Windows bootstrapper for the ePACS ERP stack, plus a three-backend simulation harness that proves the offline-first sync architecture end-to-end.

---

## What's in This Repository

| Component | Solution | Purpose |
|-----------|----------|---------|
| **Offline Installer** | `ePACS.Installer.sln` | Installs, upgrades, repairs, backs up, restores, and uninstalls the full ePACS stack on offline PACS nodes |
| **Sync Test Harness** | `harness/ePACS.SyncHarness.sln` | Simulates PACS ↔ NLDR sync with fault injection, exercises 100+ test cases, serves as post-install smoke target |

Both target **.NET 8 LTS** on **Windows 10/11 x64** (offline, rural India).

---

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
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
│  Installer Package (Authenticode-signed EXE)                     │
│  WiX v4 Burn + C# Managed BootstrapperApplication               │
│  Payloads: MySQL 8.4, Garnet, Kafka 3.7, JRE 17, Harness EXEs  │
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
| `Installer.Core` | State machine with checkpoint persistence (power-cut resilient) |
| `Installer.Actions` | Prechecks, payload extraction, service orchestration, harness integration |
| `Installer.Agent` | Always-on worker (health polling, disk monitoring, drift detection) |
| `Installer.CLI` | Silent/unattended CLI (`/quiet /config /mode /demo`) |
| `ManifestVerifier` | Authenticode signature + SHA-256 payload verification |
| `SupportBundle` | Diagnostics collector with PII redaction |
| `BackupRestore` | MySQL backup/restore workflows |
| `Sync.Agent` | Outbox relay, connectivity detection, circuit breaker |

### Harness (`harness/src/`)

| Project | Port | Purpose |
|---------|------|---------|
| `Harness.Common` | — | Shared contracts: envelope, hash, clock, fault hooks, options |
| `Pacs.Fas.Api` | 5101 | FAS voucher REST API (INSERT/UPDATE/DELETE with outbox) |
| `Pacs.Loans.Api` | 5102 | Loans REST API with maker-checker and amendments |
| `Pacs.SyncWorker` | 5103 | Outbox drain → Kafka, ACK consumer, heartbeat, file uploader |
| `Pacs.OperatorUi` | 5301 | Razor MVC field-operator UI (FAS + Loans areas) |
| `Nldr.Api` | 5201 | Strict central receiver (12-step ingest pipeline) |
| `Nldr.SyncWorker` | 5203 | ACK publisher, command publisher, heartbeat consumer |
| `Nldr.DashboardUi` | 5401 | Razor MVC central observability dashboard |
| `Harness.ScenarioPlayer` | — | Demo-mode orchestrator (one-button scenarios) |

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

| Test Suite | Docker | Duration | Command |
|-----------|--------|----------|---------|
| Installer unit tests | No | < 1s | `dotnet test ePACS.Installer.sln` |
| Harness contract tests | No | < 1s | `dotnet test harness/tests/Harness.ContractTests/` |
| Harness integration tests | Yes | ~30s | `dotnet test harness/tests/Harness.IntegrationTests/` |
| Harness chaos tests | Yes | ~5min | `dotnet test harness/tests/Harness.ChaosTests/` |
| Long offline soak | Yes | ~30min | `dotnet test harness/tests/Harness.LongOfflineTests/` |

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
