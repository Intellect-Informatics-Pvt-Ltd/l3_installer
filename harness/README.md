# ePACS Sync Test Harness

> A deliberately minimal but **architecturally faithful** simulation of an ePACS deployment.
> Proves the offline-first sync architecture end-to-end, exercises 100+ test cases, and
> acts as the smoke target for the installer's post-install verification.

---

## Table of Contents

1. [What this harness is (and is not)](#1-what-this-harness-is-and-is-not)
2. [Five non-negotiable invariants](#2-five-non-negotiable-invariants)
3. [Project layout](#3-project-layout)
4. [Where to find what](#4-where-to-find-what)
5. [Prerequisites](#5-prerequisites)
6. [First-time setup](#6-first-time-setup)
7. [Running the harness](#7-running-the-harness)
8. [Running tests](#8-running-tests)
9. [Key concepts for contributors](#9-key-concepts-for-contributors)
10. [Configuration reference](#10-configuration-reference)
11. [Error codes](#11-error-codes)
12. [Milestone roadmap](#12-milestone-roadmap)
13. [Coding standards](#13-coding-standards)

---

## 1. What this harness is (and is not)

**Is:** A three-backend simulation (FAS vouchers, Loans, NLDR central receiver) that proves the
offline-first sync architecture is correct — outbox atomicity, sequence integrity, idempotent
receivers, tamper-evident envelopes, power-cut resilience.

**Is not:** The production ePACS ERP codebase. Business logic is intentionally thin; the harness
proves _architecture-level_ invariants, not business rules.

The harness also serves as the **smoke target** after the offline installer runs on a pilot site.

---

## 2. Five non-negotiable invariants

Every test in the harness must respect these; every code change must preserve them.

| # | Invariant | Enforced by |
|---|---|---|
| **I-1** | Local MySQL is source of truth | Dapper-only writes; no ORM magic |
| **I-2** | Business row and `sync_outbox` row commit or roll back together | `SequenceAllocator` + `SyncOutboxWriter` inside the same `DbTransaction` |
| **I-3** | Same `event_id` / `idempotency_key` → exactly one business effect | `SyncInboxStore` DUPLICATE detection + `UNIQUE KEY uq_event` |
| **I-4** | Sequence numbers are monotonic and contiguous per `(pacs_id, stream_name)` | `SequenceAllocator` UPDATE+read inside same tx; `UNIQUE KEY uq_pacs_seq` |
| **I-5** | Payload hash mismatch is rejected — envelope is tamper-evident | `PayloadHasher.Verify()` in `NldrIngestService` step 5 |

---

## 3. Project layout

```
harness/
├── ePACS.SyncHarness.sln          ← single solution entry point
│
├── src/
│   ├── Harness.Common/            ← shared contracts (envelope, hash, clock, fault hooks, options)
│   ├── Pacs.Fas.Api/              ← FAS voucher REST API  :5101
│   ├── Pacs.Loans.Api/            ← Loans REST API        :5102  [stub — M5]
│   ├── Pacs.SyncWorker/           ← outbox drain + ACK consume worker
│   ├── Pacs.OperatorUi/           ← Razor MVC field-operator UI  :5301  [stub — M3]
│   ├── Nldr.Api/                  ← strict central receiver API  :5201
│   ├── Nldr.SyncWorker/           ← ACK/command publisher worker
│   ├── Nldr.DashboardUi/          ← Razor MVC central dashboard  :5401  [stub — M3]
│   └── Harness.ScenarioPlayer/    ← demo-mode orchestrator        [stub — M8]
│
├── tests/
│   ├── Harness.ContractTests/     ← unit/property tests (no infra needed)
│   ├── Harness.IntegrationTests/  ← Testcontainers end-to-end tests
│   ├── Harness.ChaosTests/        ← power-cut, kill, partition scenarios
│   └── Harness.LongOfflineTests/  ← 7/30/60-day compressed simulations
│
├── db/
│   ├── mysql/pacs/                ← V001__core_business.sql … V004__seed.sql
│   └── mysql/nldr/                ← V001__received_event.sql … V007__heartbeat.sql
│
├── docker/
│   ├── docker-compose.yml         ← full harness (infra + all 7 .NET services)
│   ├── docker-compose.minimal.yml ← infra only (for `dotnet run` local dev)
│   └── env/pacs.env, nldr.env     ← environment variable overrides
│
├── packaging/
│   └── error-catalog/harness.yaml ← all ERP-PACS-* and ERP-NLDR-* error codes
│
├── scripts/
│   └── reset-lab.sh               ← drop volumes → start → apply migrations → confirm healthy
│
├── samples/
│   └── envelope.sample.json       ← canonical wire envelope example
│
├── Directory.Build.props          ← net8.0, LangVersion 12, TreatWarningsAsErrors
├── Directory.Packages.props       ← central NuGet version pinning
└── NuGet.Config                   ← package feed configuration
```

---

## 4. Where to find what

| I need to… | Go to |
|---|---|
| Understand the full design | `../docs/test-harness/00-design-overview.md` |
| Find acceptance criteria | `../.kiro/specs/epacs-sync-test-harness/requirements.md` |
| Change the envelope wire shape | `src/Harness.Common/Envelope/EventEnvelope.cs` |
| Change canonicalization / hashing | `src/Harness.Common/Canonicalization/` — **only place** `SHA256.HashData` is called |
| Add a new fault hook | `src/Harness.Common/TestHooks/FaultHook.cs` (enum) + wire call sites |
| Add / change config defaults | `src/Harness.Common/Options/` — never hardcode a value anywhere else |
| Change database schema | `db/mysql/pacs/` or `db/mysql/nldr/` — add a new `V00N__*.sql` file |
| Add a new error code | `packaging/error-catalog/harness.yaml` + call `IErrorFactory.FromCatalog()` |
| Change the NLDR ingest pipeline | `src/Nldr.Api/Sync/NldrIngestService.cs` |
| Change the outbox relay logic | `src/Pacs.SyncWorker/Workers/OutboundRelayService.cs` |
| Change the ACK consumer logic | `src/Pacs.SyncWorker/Workers/InboundConsumerService.cs` |
| Change NLDR TestControl modes | `src/Nldr.Api/TestControl/NldrFailureMode.cs` + `NldrTestController.cs` |
| Run contract tests | `dotnet test tests/Harness.ContractTests/` |
| Run integration tests | `dotnet test tests/Harness.IntegrationTests/` (Docker required) |
| Reset the lab environment | `bash scripts/reset-lab.sh` |

---

## 5. Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 8.x | `dotnet --version` |
| Docker Desktop | 4.x+ | Required for integration tests and full Docker Compose |
| Docker Compose | v2 (`docker compose`) | Bundled with Docker Desktop |
| MySQL client | any | Optional — for inspecting containers manually |

> **macOS / Linux developers**: the harness is cross-platform. The scripts use `bash`. On Windows,
> use `reset-lab.ps1` equivalents (not yet written — contributions welcome).

---

## 6. First-time setup

### Option A — infra only, `dotnet run` locally (fastest iteration)

```bash
# 1. Start Kafka + 2× MySQL + 2× Redis
docker compose -f docker/docker-compose.minimal.yml up -d

# 2. Verify all infra containers are healthy
docker compose -f docker/docker-compose.minimal.yml ps

# 3. Apply DB migrations (the containers init-db scripts do this automatically
#    on first start, but you can run the migrator manually if needed)
#    Simply start each project and it will pick up the seeded DB.

# 4. Run Pacs.Fas.Api
dotnet run --project src/Pacs.Fas.Api

# 5. Run Nldr.Api (in a second terminal)
dotnet run --project src/Nldr.Api

# 6. Run Pacs.SyncWorker (in a third terminal)
dotnet run --project src/Pacs.SyncWorker

# 7. Run Nldr.SyncWorker (in a fourth terminal)
dotnet run --project src/Nldr.SyncWorker
```

Default ports when running locally:

| Service | URL |
|---|---|
| `Pacs.Fas.Api` | http://localhost:5101 |
| `Nldr.Api` | http://localhost:5201 |
| `Pacs.OperatorUi` | http://localhost:5301 |
| `Nldr.DashboardUi` | http://localhost:5401 |
| MySQL (PACS) | localhost:3307 |
| MySQL (NLDR) | localhost:3308 |
| Redis (PACS) | localhost:6380 |
| Redis (NLDR) | localhost:6381 |
| Kafka | localhost:9092 |

### Option B — full Docker Compose (all 7 services containerised)

```bash
# Build and start everything
docker compose -f docker/docker-compose.yml up -d --build

# Watch logs
docker compose -f docker/docker-compose.yml logs -f pacs-fas-api nldr-api pacs-sync
```

### Resetting the lab

```bash
# Tear down all containers and volumes, reapply migrations, reseed
bash scripts/reset-lab.sh

# Or for the full profile:
bash scripts/reset-lab.sh full
```

---

## 7. Running the harness

### Quick smoke test (happy path)

Once all services are running:

```bash
# 1. Create a voucher
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{
    "voucherNo": "VCH-2026-00001",
    "voucherDate": "2026-05-14",
    "voucherType": "CR",
    "narration": "Test",
    "createdBy": "admin",
    "lines": [{"accountCode":"1001","debitAmount":0,"creditAmount":5000}]
  }' | jq .

# 2. Watch the outbox in PACS MySQL (should show PENDING → IN_FLIGHT → ACKED)
# Connect to: localhost:3307 / database: epacs_pacs / user: root / password: root
SELECT sequence_no, status, event_id FROM sync_outbox ORDER BY outbox_id DESC LIMIT 5;

# 3. Watch NLDR received events
# Connect to: localhost:3308 / database: epacs_nldr
SELECT event_id, apply_status, sequence_no FROM received_event ORDER BY received_id DESC LIMIT 5;
```

### Health endpoints

Every running service exposes:

- `GET /health/live` — 200 if the process is up (no checks)
- `GET /health/ready` — 200 if all dependencies are healthy (MySQL, Redis)

### TestControl — fault injection

When `Harness:TestMode = true` (default in `appsettings.json`):

```bash
# Simulate NLDR returning 500 for the next 3 ingest calls
curl -X POST http://localhost:5201/api/test/failure-mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"http500","count":3}'

# Reset NLDR to healthy
curl -X POST http://localhost:5201/api/test/failure-mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"healthy"}'

# Check current NLDR test state
curl http://localhost:5201/api/test/state
```

Available NLDR modes: `healthy`, `http500`, `timeout`, `dropAck`, `rateLimit`, `badAck`,
`hashStrict`, `sequenceStrict`.

---

## 8. Running tests

### Contract tests (no Docker required — fast, always run first)

```bash
dotnet test tests/Harness.ContractTests/Harness.ContractTests.csproj
```

Tests cover: `CanonicalJsonWriter` determinism (property-based), `PayloadHasher` idempotence,
`IdempotencyKey` format and round-trip, `EventEnvelope` tamper detection.

### Integration tests (Docker required)

```bash
# Run the full happy-path M1 test (spins up MySQL × 2 + Kafka + Redis × 2 via Testcontainers)
dotnet test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj
```

The integration test applies migrations automatically before running; it tears down all containers
after the test. Expect 30–60 seconds for first run (Docker image pull) and ~10 seconds thereafter.

### Full solution

```bash
dotnet build ePACS.SyncHarness.sln
dotnet test tests/Harness.ContractTests/Harness.ContractTests.csproj
```

> Integration, chaos, and long-offline tests are excluded from the default `dotnet test` run
> because they require Docker. Run them explicitly when needed.

---

## 9. Key concepts for contributors

### The transactional outbox (invariant I-2)

The single most important pattern in the entire harness. Every business write **must** include a
`sync_outbox` write in the **same Dapper transaction**:

```csharp
// ✅ Correct — business + outbox + sequence all in one tx
await db.OpenAsync(ct);
await using var tx = await db.BeginTransactionAsync(ct);

var id    = await repo.InsertVoucherAsync(db, tx, ...);
var seqNo = await SequenceAllocator.GetNextAsync(db, tx, pacsId, "pacs.outbound", ct);
var envelope = new EventEnvelopeBuilder()
    .WithSequenceNo(seqNo)
    ...
    .Build(clock);
await SyncOutboxWriter.WriteAsync(db, tx, envelope, priority, ct);

await tx.CommitAsync(ct);  // ← both rows commit or both roll back
```

If you forget the shared transaction, I-2 is broken and the harness will produce spurious
"event lost" failures.

### Canonical JSON and payload hashing (invariant I-5)

`CanonicalJsonWriter` and `PayloadHasher` are the **only** place in the entire solution where
deterministic JSON and SHA-256 hashing happen. Never call `SHA256.HashData` anywhere else.

```csharp
// ✅ Correct — use the builder; it calls PayloadHasher internally
var envelope = new EventEnvelopeBuilder()
    .WithPayload(afterState)
    .WithBeforeState(beforeState)
    .Build(clock);
// envelope.PayloadHash is set automatically

// ❌ Never do this
var hash = Convert.ToHexString(SHA256.HashData(...));
```

### NLDR ingest pipeline (12 steps)

`NldrIngestService.IngestAsync` executes steps 1–12 as described in `§12.5.2` of the design.
Steps 6–12 run inside **one MySQL transaction** — a failure at step 8 (business write) rolls back
steps 9 (received_event) and 10 (inbox). Never split these into separate transactions.

### Fault hooks

Add a call to `IFaultInjector.FireAsync(FaultHook.X, ct)` at any checkpoint you want to make
testable:

```csharp
await faultInjector.FireAsync(FaultHook.BeforeKafkaPublish, ct);
await kafkaProducer.PublishAsync(topic, envelope, ct);
await faultInjector.FireAsync(FaultHook.AfterKafkaPublish, ct);
```

When `Harness:TestMode = false` (production), `NullFaultInjector` is injected and all calls are
no-ops. The hooks are only armed by TestControl HTTP calls during test execution.

### Adding a new entity type

1. Add SQL tables to `db/mysql/pacs/V001__core_business.sql` (or a new migration).
2. Add NLDR-side tables/columns to `db/mysql/nldr/`.
3. Add a new API endpoint in the appropriate `Pacs.*.Api` or `Pacs.Loans.Api` project.
4. Follow the same transaction pattern: business insert → `SequenceAllocator` → `SyncOutboxWriter`.
5. Handle the new `entity_type` in `NldrIngestRepository.ApplyBusinessStateAsync`.
6. Add contract tests for any new canonical form or invariant.

---

## 10. Configuration reference

All configuration follows the zero-hardcoding rule — every numeric threshold, string, and flag
comes from `appsettings.json` or an environment variable override.

### Key sections

```json
{
  "ConnectionStrings": {
    "PacsDb":    "...",   // MySQL PACS connection string
    "PacsRedis": "..."    // Redis PACS connection string
  },
  "Pacs": {
    "PacsId":   "PACS-AP-0001",   // must match ^PACS-[A-Z]{2}-\d{4}$
    "DataRoot": "/path/to/data"   // base for files, logs, reconciliation
  },
  "Harness": {
    "TestMode": true,             // gates all /api/test/* routes
    "Profile":  "Default"         // Default | Multi-Pacs | Two-Laptop | Installer
  },
  "Sync": {
    "Outbox": { "PollIntervalMs": 500, "BatchSize": 50 },
    "Retry":  { "MaxAttempts": 7, "BaseDelayMs": 2000 },
    "Circuit":{ "FailureThreshold": 5, "OpenDurationSeconds": 60 },
    "Priority": {
      "VoucherDefault": 10,   // lower = higher priority
      "LoanAmendment":  20,
      "Heartbeat":     200
    }
  }
}
```

### Environment variable overrides

Any `appsettings.json` key can be overridden with an environment variable using `__` as the
section separator:

```bash
export Pacs__PacsId=PACS-AP-0002
export Sync__Outbox__BatchSize=100
export ConnectionStrings__PacsDb="Server=..."
```

The Docker env files (`docker/env/pacs.env`, `docker/env/nldr.env`) use this pattern.

---

## 11. Error codes

All thrown exceptions use `IErrorFactory.FromCatalog(code, message)` with codes defined in
`packaging/error-catalog/harness.yaml`.

| Prefix | Category | HTTP | Meaning |
|---|---|---|---|
| `ERP-PACS-VAL-*` | Validation | 400/422 | Invalid request on PACS side |
| `ERP-PACS-GOV-*` | Governance | 409 | Bulk-delete threshold / token |
| `ERP-PACS-INS-*` | Installation | 500 | DB write failure, startup error |
| `ERP-PACS-SYN-*` | Sync | 503 | Outbound sync paused (clock drift) |
| `ERP-PACS-HLT-*` | Health | 200 | Warning-level health signal |
| `ERP-NLDR-VAL-*` | Validation | 400/422 | Invalid envelope on NLDR side |
| `ERP-NLDR-SEC-*` | Security | 401/422 | Auth failure or tampered payload |

To add a new code: add an entry to `harness.yaml`, then call
`errorFactory.FromCatalog("ERP-PACS-VAL-0010", "description")`.

---

## 12. Milestone roadmap

The harness is built milestone-by-milestone. Here is the current status:

| Milestone | Description | Status |
|---|---|---|
| **M0** | Solution skeleton, Harness.Common, DB migrations, Docker Compose, error catalog | ✅ Done |
| **M1** | Happy path: `Pacs.Fas.Api` create voucher → `Pacs.SyncWorker` relay → `Nldr.Api` ingest → `Nldr.SyncWorker` ACK | ✅ Done |
| **M2** | Sync invariants: idempotent receiver, checkpoint advance, lock reaper | Pending |
| **M3** | Offline/reconnect: heartbeat, online/offline UI banner, circuit breaker, drain prioritisation | Pending |
| **M4** | Power-cut: all fault hooks, crash/pause/throw modes, lock reaper resume | Pending |
| **M5** | Delete + amendment: `Pacs.Loans.Api`, three-witness audit, `[TraceableAction]` | Pending |
| **M6** | Security: hash strict mode, tamper scenarios, PII redaction, support bundle | Pending |
| **M7** | File sync: chunked upload, resume, dedup, priority | Pending |
| **M8** | Long offline + drift: `OffsetClock`, 30-day compressed scenario, drift detector | Pending |
| **M9** | Multi-PACS: separate schemas, wrong-PACS startup check | Pending |
| **M10** | Conflict UI: `conflict_log`, Razor side-by-side resolution | Pending |
| **M11** | Reconciliation + backup hooks | Pending |
| **M12** | Installer integration: `service-map.yaml`, `.epcfg` override, smoke contract | Pending |
| **M13** | Polish: Playwright UI tests, performance soak, SonarCloud | Pending |

The full milestone acceptance criteria live in `../docs/test-harness/00-design-overview.md §29`.

---

## 13. Coding standards

These standards apply across the entire harness codebase and are enforced by
`Directory.Build.props` (`TreatWarningsAsErrors = true`).

### Zero hardcoding

No path, port, threshold, or string literal in business code. Every value comes from a typed
`*Options.cs` class injected via `IOptions<T>`:

```csharp
// ❌ Never
var topic = "epacs.pacs.outbound";

// ✅ Always
var topic = syncOptions.Value.OutboundTopic;
```

### Structured logging

Use `IAppLogger<T>`, not `ILogger<T>`. Call `BeginOperation()` at entry and `Checkpoint()` at
each persistence boundary:

```csharp
using var op = logger.BeginOperation("Pacs", "Fas", "CreateVoucher");
// ... work ...
logger.Checkpoint("OutboxEnqueued", new Dictionary<string, object?> {
    ["VoucherId"] = voucherId,
    ["SequenceNo"] = seqNo
});
```

### Error handling

Every exception must go through `IErrorFactory.FromCatalog(code, message)` with a code from
`packaging/error-catalog/harness.yaml`. Never `throw new Exception(...)` directly.

### Test naming

`MethodName_Scenario_ExpectedResult` — e.g.,
`CreateVoucher_WithValidRequest_WritesAtomicOutboxRow`.

### Project dependencies

No project may depend on another business project. All cross-project communication is via HTTP or
Kafka. The only shared dependency is `Harness.Common`.

```
Harness.Common ◄── all other projects (one-way only)
Pacs.Fas.Api   ──► (no dependency on Nldr.* or Pacs.SyncWorker)
```

---

## 14. Native Windows Deployment (Installer Integration)

Beyond Docker, the harness can be deployed as **native Windows services** via the ePACS offline
installer. This is the deployment mode for pilot sites and CxO demos.

### How it works

1. CI publishes each harness project as a self-contained single-file EXE (`win-x64`)
2. The EXEs are packaged into two ZIP payloads (`harness-pacs-win-x64.zip`, `harness-nldr-win-x64.zip`)
3. The offline installer extracts them to `C:\Program Files\ePACS\current\harness\`
4. `ServiceOrchestrator` registers them as Windows services using `packaging/service-map.yaml`
5. Services start in dependency order after infra (MySQL → Garnet → Kafka → harness services)

### Publishing locally

```powershell
# From the harness/ directory
.\scripts\publish-win-x64.ps1 -CreateZip

# Output:
#   publish/pacs/Pacs.Fas.Api.exe        (~80 MB, self-contained)
#   publish/pacs/Pacs.Loans.Api.exe
#   publish/pacs/Pacs.SyncWorker.exe
#   publish/pacs/Pacs.OperatorUi.exe
#   publish/nldr/Nldr.Api.exe
#   publish/nldr/Nldr.SyncWorker.exe
#   publish/nldr/Nldr.DashboardUi.exe
#   publish/harness-pacs-win-x64.zip     (installer payload)
#   publish/harness-nldr-win-x64.zip     (installer payload, --demo only)
```

### Service map

The harness service map (`packaging/service-map.yaml`) defines:

| Service | Port | Start Order | Group |
|---------|------|-------------|-------|
| `ePACS.Harness.FasApi` | 5101 | 110 | pacs |
| `ePACS.Harness.LoansApi` | 5102 | 120 | pacs |
| `ePACS.Harness.SyncWorker` | 5103 | 130 | pacs |
| `ePACS.Harness.OperatorUi` | 5301 | 140 | pacs |
| `ePACS.Harness.NldrApi` | 5201 | 150 | nldr |
| `ePACS.Harness.NldrSyncWorker` | 5203 | 160 | nldr |
| `ePACS.Harness.NldrDashboard` | 5401 | 170 | nldr |

PACS-side services (group `pacs`) are always installed. NLDR-side services (group `nldr`) are
installed only when the installer runs with `--demo` flag.

### Installer profile

When deployed by the installer, services use `appsettings.Installer.json` which sets:
- `Harness:TestMode = false` (fault injection routes disabled)
- `Harness:Profile = "Installer"`
- Connection strings pointing to localhost MySQL/Redis/Kafka (single machine)
- Logs to `D:\ePACSData\logs\harness\`

See `samples/appsettings.Installer.json` for the full reference.

### Architecture Decision Records

- [ADR-0007: Harness Native Deployment](../docs/adr/ADR-0007-harness-native-deployment.md) — why self-contained single-file, payload split, service map design
- [ADR-0008: Deployment Profiles](../docs/adr/ADR-0008-harness-deployment-profiles.md) — profile-based configuration overlay system
