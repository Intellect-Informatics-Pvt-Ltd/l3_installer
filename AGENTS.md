# AGENTS.md — ePACS Offline Installer + Sync Test Harness

> This file provides context for AI coding assistants (Kiro, Cursor, Copilot, Windsurf, Cline, etc.) working on this repository. It is the single source of truth for project structure, conventions, and build/test/run instructions.

---

## 1. Project Overview

This repository contains **two .NET 8 solutions** that together form the ePACS offline deployment and sync verification stack:

| Solution | Path | Purpose |
|----------|------|---------|
| **ePACS.Installer.sln** | `/ePACS.Installer.sln` | Production-grade signed Windows bootstrapper — installs, upgrades, repairs, backs up, restores, and uninstalls the full ePACS ERP stack on offline PACS nodes |
| **ePACS.SyncHarness.sln** | `/harness/ePACS.SyncHarness.sln` | Three-backend simulation harness — proves the offline-first sync architecture end-to-end, exercises 100+ test cases, and serves as the installer's post-install smoke target |

**Target environment**: Windows 10/11 Pro or Server 2019+ (x64), 8 GB+ RAM, SSD, fully offline after installation. Rural Indian conditions: unreliable power, intermittent 4G connectivity, 35–45°C temperatures.

---

## 2. Technology Stack

| Component | Technology | Version |
|-----------|-----------|---------|
| Language | C# 12 | .NET 8 LTS |
| Installer framework | WiX v4 Burn | 4.x |
| Database | MySQL | 8.4 LTS |
| Cache | Microsoft Garnet | Latest stable |
| Eventing | Apache Kafka (KRaft mode) | 3.7.x LTS |
| JRE (for Kafka) | Eclipse Temurin | 17 LTS |
| Backup tool | Percona XtraBackup | 8.4 |
| DDL migration | DbUp | Latest |
| Logging | Serilog (via Intellect.Erp.Observability) | — |
| Testing | xUnit + FluentAssertions + Moq | — |
| Containerisation (dev) | Docker Compose v2 | — |

---

## 3. Repository Layout

```
/
├── ePACS.Installer.sln                    ← Installer solution
├── AGENTS.md                              ← This file
├── README.md                              ← Project overview + quick start
│
├── src/                                   ← Installer source projects
│   ├── SharedKernel/                      # Config models, contracts, error handling
│   ├── Installer.Core/                    # State machine, checkpoint persistence
│   ├── Installer.Actions/                 # Prechecks, install, uninstall, harness integration
│   │   └── Install/
│   │       ├── ServiceOrchestrator.cs     # Registers/starts Windows services from service-map.yaml
│   │       ├── PayloadExtractor.cs        # Extracts ZIP/TGZ payloads (resumable)
│   │       ├── ConfigGenerator.cs         # Token replacement from .epcfg → appsettings
│   │       ├── HarnessConfigGenerator.cs  # Generates harness appsettings.Production.json
│   │       ├── HarnessServiceMapLoader.cs # Loads/filters harness service-map.yaml by group
│   │       ├── HarnessSmokeTest.cs        # Post-install health check for harness services
│   │       └── ...
│   ├── Installer.Agent/                   # Always-on worker (health, disk, drift, logs)
│   ├── Installer.CLI/                     # Silent/unattended CLI entry point
│   ├── ManifestVerifier/                  # Authenticode + SHA-256 verification
│   ├── SupportBundle/                     # Diagnostics collector with PII redaction
│   ├── BackupRestore/                     # Backup/restore workflows
│   ├── Sync.Abstractions/                 # ISyncTransport interface
│   └── Sync.Agent/                        # Outbox relay, connectivity, circuit breaker
│
├── tests/
│   ├── Installer.UnitTests/               # 20 unit tests
│   └── Installer.IntegrationTests/        # 1 integration test
│
├── packaging/
│   ├── config-templates/                  # appsettings.json, appsettings.Production.json
│   ├── error-catalog/                     # core.yaml, installer.yaml
│   ├── payloads/                          # (empty — CI places binaries here)
│   ├── scripts/                           # (PowerShell install helpers)
│   └── wix/                               # (WiX Burn bundle — not yet scaffolded)
│
├── samples/
│   ├── release-manifest.yaml              # Sample signed manifest
│   ├── service-map.yaml                   # Infra service definitions
│   └── site-config-pack.epcfg            # Sample .epcfg
│
├── docs/
│   ├── adr/                               # Architecture Decision Records (ADR-0001..0008)
│   ├── test-harness/
│   │   ├── 00-design-overview.md          # Authoritative harness design (~2000 lines)
│   │   └── TESTERS-README.md             # QA tester's guide (handoff document)
│   ├── ePACS_SAD_v1.2.pdf                 # System Architecture Document
│   └── ...
│
└── harness/                               ← Sync Test Harness (separate solution)
    ├── ePACS.SyncHarness.sln
    ├── Directory.Build.props              # net8.0, win-x64 self-contained publish in Release
    ├── Directory.Packages.props           # Central NuGet version pinning
    ├── NuGet.Config
    ├── README.md                          # Harness developer guide
    │
    ├── src/
    │   ├── Harness.Common/               # Shared: envelope, hash, clock, fault hooks, options
    │   ├── Pacs.Fas.Api/                 # FAS voucher REST API  :5101
    │   ├── Pacs.Loans.Api/              # Loans REST API        :5102
    │   ├── Pacs.SyncWorker/             # Outbox relay + ACK consumer worker
    │   ├── Pacs.OperatorUi/             # Razor MVC field-operator UI  :5301
    │   ├── Nldr.Api/                    # Strict central receiver API  :5201
    │   ├── Nldr.SyncWorker/             # ACK/command publisher worker
    │   ├── Nldr.DashboardUi/           # Razor MVC central dashboard  :5401
    │   └── Harness.ScenarioPlayer/      # Demo-mode orchestrator
    │
    ├── tests/
    │   ├── Harness.ContractTests/        # 16 property/unit tests (no Docker)
    │   ├── Harness.IntegrationTests/     # Testcontainers end-to-end
    │   ├── Harness.ChaosTests/           # Power-cut, partition scenarios
    │   └── Harness.LongOfflineTests/     # 7/30/60-day compressed soak
    │
    ├── db/mysql/{pacs,nldr}/             # SQL migrations V001..V00N
    ├── docker/                           # docker-compose.yml, docker-compose.minimal.yml
    ├── packaging/
    │   ├── error-catalog/harness.yaml    # ERP-PACS-*, ERP-NLDR-* error codes
    │   ├── service-map.yaml             # Harness Windows service definitions
    │   └── installer-manifest-stub.yaml # CI payload manifest entries
    ├── scripts/
    │   ├── reset-lab.sh                 # Drop volumes, remigrate, reseed
    │   └── publish-win-x64.ps1          # Build self-contained EXEs + ZIP payloads
    └── samples/
        ├── envelope.sample.json          # Canonical wire envelope example
        └── appsettings.Installer.json   # Reference config for native deployment
```

---

## 4. Quick Start

### Prerequisites

| Tool | Version | Required for |
|------|---------|-------------|
| .NET SDK | 8.x | Both solutions |
| Docker Desktop | 4.x+ | Harness integration tests + local dev |
| PowerShell 7+ | 7.x | Publish scripts (Windows) |

### Build & Test

```bash
# ─── Installer ───────────────────────────────────────────
dotnet build ePACS.Installer.sln
dotnet test ePACS.Installer.sln

# ─── Harness ─────────────────────────────────────────────
cd harness
dotnet build ePACS.SyncHarness.sln
dotnet test tests/Harness.ContractTests/Harness.ContractTests.csproj   # fast, no Docker

# Integration tests (Docker required)
dotnet test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj
```

### Run the Harness Locally

```bash
cd harness

# 1. Start infra (Kafka + MySQL × 2 + Redis × 2)
docker compose -f docker/docker-compose.minimal.yml up -d

# 2. Run services (each in a separate terminal)
dotnet run --project src/Pacs.Fas.Api          # :5101
dotnet run --project src/Nldr.Api              # :5201
dotnet run --project src/Pacs.SyncWorker       # outbox relay
dotnet run --project src/Nldr.SyncWorker       # ACK publisher

# 3. Smoke test
curl http://localhost:5101/health/ready
curl http://localhost:5201/health/ready
```

### Publish for Windows Deployment

```powershell
# From harness/ directory (PowerShell)
.\scripts\publish-win-x64.ps1 -CreateZip

# Output: publish/harness-pacs-win-x64.zip, publish/harness-nldr-win-x64.zip
```

### Installer CLI

```bash
# Publish installer
dotnet publish src/Installer.CLI/Installer.CLI.csproj -c Release -r win-x64 --self-contained

# Run (on Windows target)
Installer.CLI.exe /quiet /config:D:\site-config.epcfg /mode:install
Installer.CLI.exe /quiet /config:D:\site-config.epcfg /mode:install /demo   # includes NLDR
```

---

## 5. Critical Coding Standards

### 5.1 ZERO HARDCODING

**Every** path, port, threshold, interval, credential, and behavioral parameter MUST come from configuration. No exceptions.

```csharp
// ❌ NEVER
var dataRoot = @"D:\ePACSData";
var topic = "epacs.pacs.outbound";

// ✅ ALWAYS
var dataRoot = _options.Value.DataRoot;
var topic = _syncOptions.Value.OutboundTopic;
```

### 5.2 Structured Logging

Use `IAppLogger<T>` (not raw `ILogger<T>`). Use structured message templates with named parameters. Never log PII.

```csharp
_logger.Information("Service {ServiceName} health check {Outcome} in {DurationMs}ms",
    serviceName, outcome, duration.TotalMilliseconds);

_logger.Checkpoint("OutboxEnqueued", new Dictionary<string, object?> {
    ["VoucherId"] = voucherId,
    ["SequenceNo"] = seqNo
});
```

### 5.3 Error Handling

All exceptions go through `IErrorFactory.FromCatalog(code, message)` with codes from YAML catalogs.

| Solution | Catalog file | Code format |
|----------|-------------|-------------|
| Installer | `packaging/error-catalog/installer.yaml` | `ERP-INST-{CAT}-{NUM}` |
| Harness | `harness/packaging/error-catalog/harness.yaml` | `ERP-{PACS\|NLDR}-{CAT}-{NUM}` |

### 5.4 Configuration Pattern

```csharp
public sealed class SyncOptions
{
    public const string SectionName = "Sync";
    public OutboxOptions Outbox { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
}

// Registration:
services.Configure<SyncOptions>(configuration.GetSection(SyncOptions.SectionName));
```

### 5.5 Power-Cut Resilience

- Every state transition writes a checkpoint (fsync'd via write-then-rename)
- MySQL: `innodb_flush_log_at_trx_commit=1`
- All operations must be resumable from the last checkpoint
- Use `SELECT ... FOR UPDATE SKIP LOCKED` for outbox relay

### 5.6 Testing

- Framework: xUnit + FluentAssertions + Moq
- Naming: `MethodName_Scenario_ExpectedResult`
- All config-driven behavior tested with different config values
- Harness contract tests: property-based (FsCheck-style) for canonicalization/hashing

### 5.7 Harness-Specific Rules

- `SHA256.HashData` may ONLY be called in `Harness.Common/Canonicalization/PayloadHasher.cs`
- Every business write MUST share its `DbTransaction` with the `sync_outbox` write (invariant I-2)
- `TestControl` routes (`/api/test/*`) return 404 when `Harness:TestMode = false`
- No project depends on another business project — cross-service via HTTP or Kafka only

---

## 6. Five Non-Negotiable Invariants (Harness)

Every code change in the harness must preserve these:

| # | Invariant | Enforced by |
|---|---|---|
| I-1 | Local MySQL is source of truth | Dapper-only writes; no ORM |
| I-2 | Business row + `sync_outbox` row commit or roll back together | Shared `DbTransaction` |
| I-3 | Same `event_id`/`idempotency_key` → exactly one business effect | `SyncInboxStore` + UNIQUE KEY |
| I-4 | Sequence numbers monotonic and contiguous per `(pacs_id, stream_name)` | `SequenceAllocator` |
| I-5 | Payload hash mismatch → rejected (tamper-evident) | `PayloadHasher.Verify()` |

---

## 7. Key Source Files

### Installer

| File | Purpose |
|------|---------|
| `src/Installer.Core/StateMachine/InstallerStateMachine.cs` | State machine with checkpoint persistence |
| `src/Installer.Actions/Install/ServiceOrchestrator.cs` | Windows service registration via sc.exe |
| `src/Installer.Actions/Install/PayloadExtractor.cs` | Resumable ZIP/TGZ extraction |
| `src/Installer.Actions/Install/ConfigGenerator.cs` | Token replacement from .epcfg |
| `src/Installer.Actions/Install/HarnessConfigGenerator.cs` | Generates harness config from .epcfg |
| `src/Installer.Actions/Install/HarnessServiceMapLoader.cs` | Loads/filters harness service-map by group |
| `src/Installer.Actions/Install/HarnessSmokeTest.cs` | Post-install health verification |
| `src/Installer.CLI/Program.cs` | CLI entry point (/quiet, /config, /mode, /demo) |
| `src/ManifestVerifier/ManifestVerificationService.cs` | Authenticode + SHA-256 verification |

### Harness

| File | Purpose |
|------|---------|
| `harness/src/Harness.Common/Envelope/EventEnvelope.cs` | Wire envelope contract |
| `harness/src/Harness.Common/Canonicalization/CanonicalJsonWriter.cs` | Deterministic JSON (only source) |
| `harness/src/Harness.Common/Canonicalization/PayloadHasher.cs` | SHA-256 (only call site) |
| `harness/src/Harness.Common/Sequencing/SequenceAllocator.cs` | Atomic sequence allocation |
| `harness/src/Harness.Common/Outbox/SyncOutboxWriter.cs` | Outbox insert in shared tx |
| `harness/src/Harness.Common/TestHooks/FaultHook.cs` | 13 fault injection checkpoints |
| `harness/src/Pacs.Fas.Api/Vouchers/VoucherService.cs` | Canonical I-2 transaction pattern |
| `harness/src/Nldr.Api/Sync/NldrIngestService.cs` | 12-step strict ingest pipeline |
| `harness/src/Pacs.SyncWorker/Workers/OutboundRelayService.cs` | SELECT FOR UPDATE SKIP LOCKED relay |

---

## 8. Configuration Hierarchy

Both solutions follow the same configuration precedence (later wins):

1. `appsettings.json` — compiled defaults
2. `appsettings.{Environment}.json` — environment overlay (Development/Production)
3. `appsettings.Profile.{Harness:Profile}.json` — profile overlay (harness only)
4. `.epcfg` (Site Config Pack) — site-specific values (signed, distributed out-of-band)
5. Environment variables — final override (`Pacs__PacsId=PACS-AP-0002`)

---

## 9. Deployment Modes

### Installer (production PACS node)

```
USB stick → Installer.CLI.exe /quiet /config:site.epcfg /mode:install
  → Extracts payloads (MySQL, Garnet, Kafka, JRE, ePACS services, harness)
  → Generates appsettings.Production.json from .epcfg
  → Registers Windows services from service-map.yaml
  → Starts services in dependency order
  → Runs smoke test (health checks)
```

### Installer with --demo (single-machine demo)

Same as above, but also installs NLDR-side harness services on the same machine.
`Harness:TestMode = true` enables fault injection for live demos.

### Docker Compose (development)

```bash
cd harness
docker compose -f docker/docker-compose.minimal.yml up -d   # infra only
dotnet run --project src/Pacs.Fas.Api                       # services locally
```

### Full Docker (CI)

```bash
cd harness
docker compose -f docker/docker-compose.yml up -d --build   # everything containerised
```

---

## 10. Port Map

| Service | Port | Notes |
|---------|------|-------|
| MySQL (PACS) | 3306 (native) / 3307 (Docker) | `epacs_pacs` database |
| MySQL (NLDR) | 3306 (native) / 3308 (Docker) | `epacs_nldr` database |
| Redis (PACS) | 6379 (native) / 6380 (Docker) | Key prefix `pacs:` |
| Redis (NLDR) | 6379 db=1 (native) / 6381 (Docker) | Key prefix `nldr:` |
| Kafka | 9092 | KRaft single-node |
| Pacs.Fas.Api | 5101 | FAS voucher REST API |
| Pacs.Loans.Api | 5102 | Loans REST API |
| Pacs.SyncWorker | 5103 | Health endpoint only |
| Pacs.OperatorUi | 5301 | Razor MVC |
| Nldr.Api | 5201 | NLDR ingest + commands |
| Nldr.SyncWorker | 5203 | Health endpoint only |
| Nldr.DashboardUi | 5401 | Razor MVC |
| Installer Agent | 5090 | Health endpoint |
| ePACS Web | 443 | Production HTTPS |

---

## 11. Architecture Decision Records

| ADR | Decision |
|-----|----------|
| [ADR-0001](docs/adr/ADR-0001-wix-v4-burn-bootstrapper.md) | WiX v4 Burn as installer framework |
| [ADR-0002](docs/adr/ADR-0002-garnet-over-redis.md) | Microsoft Garnet over Redis |
| [ADR-0003](docs/adr/ADR-0003-kafka-kraft-single-node.md) | Kafka KRaft single-node |
| [ADR-0004](docs/adr/ADR-0004-kestrel-self-hosted.md) | Kestrel self-hosted (no IIS) |
| [ADR-0005](docs/adr/ADR-0005-dbup-schema-migrations.md) | DbUp for schema migrations |
| [ADR-0006](docs/adr/ADR-0006-sync-abstraction-layer.md) | Transactional outbox + Kafka |
| [ADR-0007](docs/adr/ADR-0007-harness-native-deployment.md) | Harness native Windows deployment |
| [ADR-0008](docs/adr/ADR-0008-harness-deployment-profiles.md) | Deployment profiles |

---

## 12. Internal NuGet Packages

These are sibling utilities from the ePACS platform (not on nuget.org):

| Package | Purpose |
|---------|---------|
| `Intellect.Erp.Observability.Core` | Structured logging, enrichers, redaction |
| `Intellect.Erp.Observability.Abstractions` | Contracts (IAppLogger, IErrorFactory) |
| `Intellect.Erp.Observability.Propagation` | Correlation across HTTP/Kafka, TraceableBackgroundService |
| `Intellect.Erp.Observability.AuditHooks` | Audit event bridge |
| `Intellect.Erp.ErrorHandling` | Typed exceptions, YAML error catalog |
| `Intellect.Erp.Traceability` | Compliance-grade audit (11 tables, geo-tag) |
| `Intellect.Erp.Messaging.Kafka` | Kafka producer/consumer with resilience |
| `Intellect.Erp.Orchestration` | Outbox/inbox orchestration, saga support |
| `RedisCaching` | ICacheProvider, fail-open Redis wrapper |

---

## 13. Important Constraints

1. **Offline-only**: No internet access at runtime. All dependencies bundled.
2. **Windows-only**: Target is win-x64. No cross-platform needed for deployment.
3. **Self-contained publish**: No .NET runtime dependency on target machine.
4. **No containers in production**: Native Windows services only. Docker is dev/CI only.
5. **Signed packages**: All binaries Authenticode-signed in CI.
6. **Data preservation**: Uninstall NEVER deletes business data without governance token.
7. **Resumable**: Every long operation must survive power-cut and resume.
8. **Zero hardcoding**: Every value from configuration.

---

## 14. File Naming Conventions

| Pattern | Example | Location |
|---------|---------|----------|
| Configuration models | `*Options.cs` | `SharedKernel/Configuration/`, `Harness.Common/Options/` |
| Service interfaces | `I*.cs` | Same directory as implementation |
| Implementations | `*.cs` matching interface | — |
| Tests | `*Tests.cs` | `tests/` |
| Error catalog | `*.yaml` | `packaging/error-catalog/` |
| SQL migrations | `V{NNN}__{description}.sql` | `harness/db/mysql/{pacs,nldr}/` |
| ADRs | `ADR-{NNNN}-{slug}.md` | `docs/adr/` |

---

## 15. Harness Milestone Roadmap

| Milestone | Description | Status |
|-----------|-------------|--------|
| M0 | Solution skeleton, Harness.Common, DB migrations, Docker Compose | ✅ Done |
| M1 | Happy path: create voucher → relay → ingest → ACK | ✅ Done |
| M2 | Sync invariants: idempotent receiver, checkpoint, lock reaper | Pending |
| M3 | Offline/reconnect: heartbeat, banner, circuit breaker | Pending |
| M4 | Power-cut: fault hooks, crash modes, resume | Pending |
| M5 | Delete + amendment: Loans API, three-witness audit | Pending |
| M6 | Security: hash strict, tamper, PII redaction | Pending |
| M7 | File sync: chunked upload, resume, dedup | Pending |
| M8 | Long offline + drift: OffsetClock, 30-day soak | Pending |
| M9 | Multi-PACS: separate schemas, wrong-PACS check | Pending |
| M10 | Conflict UI: side-by-side resolution | Pending |
| M11 | Reconciliation + backup hooks | Pending |
| M12 | Installer integration: service-map, .epcfg, smoke | 🔧 In Progress |
| M13 | Polish: Playwright, performance, SonarCloud | Pending |
