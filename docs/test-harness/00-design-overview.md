# ePACS Sync Test Harness — Design Overview (v2.0)

> Single-source design document for a three-backend simulation harness that proves the offline-first ePACS sync architecture end-to-end, exercises the **100+ test cases** in `ePACS_Sync_Test_Cases_and_Simulation_Plan_v1.0` and the concepts in `ePACS_Test_Engineer_Guide_Offline_Sync_v1.0`, and integrates with the offline installer for pilot-site demos.

---

## 0. How to Read This Document

| If you are… | Read first | Build outcome |
|---|---|---|
| Architect / reviewer | §1, §2, §3, §6.3, §8, §13, §22, §31 | Approve / reject the design before code is written |
| Backend developer | §3, §4, §5, §6, §7, §8, §12, §14, §15, §16 | Build the three .NET 8 backends + worker |
| UI developer | §12.4, §12.7, §19, §20 | Build the Razor operator UI and NLDR dashboard |
| QA / automation engineer | §8, §13, §18, §19, §20, §21, §22, §24 | Wire test cases to fault hooks + evidence |
| DevOps | §14, §17, §23, §29 | Provision Docker / Hyper-V / two-laptop / pilot lab |
| Installer team | §14.3, §23.5, §29 | Package harness payload + smoke contract |

Sections that were entire **companion documents** in v1 (`01-pacs-fas-design.md` … `07-installer-integration.md`) are now **embedded** here as numbered sections. There is exactly one design document. Code-level details live next to the code in `README.md` of each project once scaffolded.

---

## 1. Purpose, Scope, and Mental Model

### 1.1 What this harness is

A deliberately small but **architecturally faithful** simulation of an ePACS deployment that:

1. **Proves offline-first behaviour live** — UI, DB, Kafka, Redis, and NLDR state visible at every moment.
2. **Injects failures at exact checkpoints** — power-cut, NLDR-down, Kafka-down, Redis-flush, network partition, clock drift, ACK loss, payload tamper.
3. **Validates invariants programmatically** — outbox/inbox correctness, sequence continuity, idempotency, hash integrity, three-witness audit, PII redaction.
4. **Demos the installer + sync stack** to CxO, security, pilot-site reviewers.
5. **Runs the 100+ test cases** from the Test Plan v1.0 and supports the 10 Practice Labs in the Engineer Guide v1.0.
6. **Acts as the smoke target** for the installer's post-install verification.

### 1.2 What this harness is NOT

- It is **not** a replacement for unit tests. It assumes unit tests exist for Dapper repositories, serializers, validators, and service methods.
- It is **not** the production ePACS ERP codebase. The three backends are deliberately minimal slices that prove architecture-level invariants without dragging in real business logic.
- It is **not** authorised to perform destructive physical power-off on production hardware. Power tests run on a dedicated test laptop / VM snapshot only.

### 1.3 Mental model (one paragraph)

> A PACS node is a **small bank branch**. Its **local MySQL is authoritative truth**. Kafka is transport. Redis is disposable cache. The NLDR is downstream convergence — important but not load-bearing for day-to-day operations. Every test passes only when **DB state + outbox/inbox state + Kafka state + UI state + logs + audit evidence all agree**.

### 1.4 The five non-negotiable invariants (every test must respect)

| # | Invariant | Test that proves it |
|---|---|---|
| I-1 | Local MySQL is source of truth | OFF-001..005, FAIL-006..007, SEC-006 |
| I-2 | Transactional outbox: business row and `sync_outbox` row commit or roll back together | PWR-001..003, SYNC-POS-001..004 |
| I-3 | Idempotent receiver: same `event_id`/`idempotency_key` produces one business effect | SEQ-004..006, FAIL-003, FAIL-009, CRIT-003, CRIT-009 |
| I-4 | Monotonic sequence per `pacs_id` + stream | SEQ-001..012, CRIT-008, NEG-010, NEG-018 |
| I-5 | Tamper-evident envelope: payload hash mismatch rejected | SEC-001, SEC-005, CRIT-010, NEG-020 |

These are repeated in §22 (coverage matrix) so every component design is traceable back.

---

## 2. Critical Evaluation of v1 Design — Gap Log

This subsection lists every concrete gap the v1 design left, and what §-of-this-document closes it. **Reviewers should challenge each item before code starts.**

| # | Gap in v1 | Severity | Closed in §  |
|---|---|---|---|
| G-01 | Test-plan tables (`sync_outbox`, `sync_inbox`, …) vs orchestration NuGet tables (`OutboxMessages`, `InboxMessages`) — schema reconciliation undefined | P0 | §6.3 |
| G-02 | Fault-injection hook catalog (7 checkpoints from Test Plan §18.1) not enumerated, no precedence rules, no scoping | P0 | §13.3 |
| G-03 | NLDR mock modes enumerated as 5 in v1 vs 8 in Test Plan §22 | P0 | §13.2 |
| G-04 | Sequence allocation atomicity (must be inside same MySQL tx as business write + outbox write) unspecified | P0 | §8.3 |
| G-05 | `payloadHash` canonicalization rule undefined → makes SEC-001 / CRIT-010 non-deterministic | P0 | §7.3 |
| G-06 | Three-witness audit wiring (`sync_outbox` + audit row + Traceability row) not shown | P0 | §15.3 |
| G-07 | Time-control / clock-drift injection (SEQ-011, CRIT-018) absent | P0 | §13.4 |
| G-08 | File-sync chunking, resume, dedup (SYNC-POS-010, OFF-006, PWR-008, NEG-020) not designed | P0 | §11 |
| G-09 | Conflict detection algorithm & UI flow (CRIT-019) absent | P0 | §19 |
| G-10 | Reconciliation report generator (Exit Criteria §19) not defined | P0 | §18 |
| G-11 | Multi-PACS profile support (SEQ-009/010) — v1 models one PACS only | P1 | §25 |
| G-12 | Heartbeat producer/consumer + operator-visible Online/Offline banner semantics (OFF-004, UI-001) underspecified | P1 | §17.3 |
| G-13 | Demo Mode (Scenario Player) — v1 demos are 6 manual click sequences, not reproducible | P1 | §20 |
| G-14 | Installer integration: `.epcfg` overrides, packaged manifest entry, automated smoke contract — only Demo 6 hand-waves it | P1 | §14.3, §23.5 |
| G-15 | `Harness.Common` API surface (envelope, canonical JSON, hash, idempotency-key formatter, redaction attrs, retry-policy DSL) not specified | P1 | §12.8 |
| G-16 | Health-check set per service (Kafka, MySQL, Redis, downstream NLDR) not defined | P1 | §17.1 |
| G-17 | Zero-hardcoding compliance: no `*Options.cs` classes named | P1 | §14.1 |
| G-18 | "3 apps" requested by user but v1 has 2 (PACS + NLDR), with PACS-FAS and PACS-Loans co-resident | P1 | §3.1 |
| G-19 | Reconnect drain prioritization (financial-first per OFF-003) algorithm absent | P1 | §8.6 |
| G-20 | DELETE before-state capture mechanism (CRIT-011, NEG-007) — how is it produced? Trigger? App code? Dapper interceptor? | P1 | §12.1.4 |
| G-21 | AMENDMENT reason/approver enforcement at API boundary (NEG-009) — not designed | P1 | §12.2.5 |
| G-22 | Power-cut "test hook" referenced in CRIT-006 (pause-after-DB-commit) but never specified as an API contract | P1 | §13.1 |
| G-23 | Support bundle structure, redaction scanner, correlation index (UI-005, CRIT-020) absent | P2 | §15.5 |
| G-24 | Error catalog file (`packaging/error-catalog/harness.yaml`) not listed in repo layout | P2 | §16 |
| G-25 | "Pacs.OperatorUi" was a single project in v1 but user calls out "one simple UI module like `l3_ERPClient`" — make it explicitly Razor MVC and shared across FAS + Loans | P2 | §12.4 |
| G-26 | No "reset-lab" semantics — labs must reset DB + Kafka + Redis + outbox between runs with one command | P2 | §24.4 |
| G-27 | Build/run phases / acceptance milestones for PM tracking absent | P2 | §29 |
| G-28 | Performance budgets per scenario (PERF-001..006) not stated as numeric SLOs | P2 | §28 |

Resolving G-01..G-04 first is mandatory before any code; the rest can be parallelised.

---

## 3. Architecture: Three Backends + One Razor UI + One NLDR Dashboard

### 3.1 Why **three** backends (not two)

The user request is explicit: "build these 3 …  one similar to FAS … one bespoke like l3_loans or l3_merchandise with UI … and the NLDR mock". This is also the right architectural shape:

- **Pacs.Fas** demonstrates a **voucher-heavy, hard-delete-with-audit-table** workflow that mirrors the real FAS module (`fa_voucherdeletionmain` pattern from `docs/deletionsenerio.md`).
- **Pacs.Loans** demonstrates a **customer-centric, amendment-heavy, maker-checker** workflow with reason/approver capture (the high-risk class in §7 of the Engineer Guide).
- **Nldr.Mock** demonstrates a **strict central receiver** that refuses tampered, out-of-order, or unsignalled events.

Splitting PACS into two backends (rather than co-residing them in a single PACS process) buys us:

1. Two real Kafka producers running side by side, so cross-stream sequence isolation is testable (SEQ-009).
2. Two real `sync_sequence` rows per PACS (one per `stream_name`).
3. A natural way to demonstrate **bidirectional commands**: NLDR can target FAS or Loans independently.
4. Closer fidelity to the real ePACS deployment, where each l3_* module owns its DB schema and outbox.

Both PACS backends write to the **same `epacs_pacs` MySQL database** (different tables; same `sync_outbox` table). The single shared outbox is critical because the **sync worker drains one queue in sequence order** — this is what the test plan §11 sequence guarantees assume.

### 3.2 Component topology

```
┌────────────────────────────────────────────────────────────┐
│                      Browser                                │
│  ┌──────────────────────┐    ┌──────────────────────┐      │
│  │  PACS Operator UI    │    │   NLDR Dashboard UI  │      │
│  │  (Razor MVC)         │    │   (Razor MVC)        │      │
│  │  http://:5301        │    │   http://:5401       │      │
│  └─────────┬────────────┘    └──────────┬───────────┘      │
└────────────┼─────────────────────────────┼─────────────────┘
             │ HTTP                        │ HTTP
             ▼                             ▼
   ┌─────────────────────┐        ┌─────────────────────┐
   │  Pacs.Fas.Api       │        │   Nldr.Api          │
   │  http://:5101       │        │   http://:5201      │
   │  Pacs.Loans.Api     │        │   Nldr.SyncWorker   │
   │  http://:5102       │        │   Nldr.TestControl  │
   │  Pacs.SyncWorker    │        └──────────┬──────────┘
   │  Pacs.TestControl   │                   │
   └────────┬──┬─────────┘                   │
            │  │                              │
   ┌────────▼──┴─────┐  ┌─────────────┐  ┌───▼────────────┐
   │  MySQL_PACS     │  │  Kafka      │  │  MySQL_NLDR    │
   │  epacs_pacs     │  │  KRaft mode │  │  epacs_nldr    │
   │  :3307          │  │  :9092      │  │  :3308         │
   └─────────────────┘  └─────────────┘  └────────────────┘
   ┌─────────────────┐                    ┌────────────────┐
   │  Redis_PACS     │                    │  Redis_NLDR    │
   │  :6380          │                    │  :6381         │
   └─────────────────┘                    └────────────────┘
```

### 3.3 Why **one** UI (not two operator UIs)

The user request is explicit: "one simple UI module with Razor + HTML like `l3_ERPClient`". The same Razor MVC project serves both the FAS voucher screens and the Loans application screens (separate Razor areas). The NLDR side has its own dashboard because its audience is **central-side** (not the PACS operator).

### 3.4 Roles played by each process

| Process | Plays the role of | Backed by | Hosts | Internal NuGets |
|---|---|---|---|---|
| `Pacs.Fas.Api` | The FAS module on a PACS node | MySQL_PACS (vouchers, sync_outbox shared) + Kafka + Redis_PACS | REST API + (optionally) collocated outbox relay if `Pacs.SyncWorker` is absent | Observability.AspNetCore, ErrorHandling, Orchestration, Messaging.Kafka.Outbox, RedisCaching, Traceability |
| `Pacs.Loans.Api` | The Loans module on a PACS node | MySQL_PACS (loans tables, sync_outbox shared) + Kafka + Redis_PACS | REST API | same as above |
| `Pacs.SyncWorker` | The single per-PACS sync agent that drains outbox → Kafka/HTTP → ACK + consumes NLDR commands | MySQL_PACS (sync_outbox, sync_inbox, sync_checkpoints, file_sync_registry) | BackgroundService(s) using `TraceableBackgroundService` | Orchestration (Hosting), Messaging.Kafka, Observability.Propagation |
| `Pacs.OperatorUi` | The thin field-operator UI | Calls Pacs.Fas.Api + Pacs.Loans.Api + Pacs.TestControl over HTTP | Razor MVC + minimal JS for polling/SignalR | Observability.AspNetCore for correlation, RedisCaching for session |
| `Nldr.Api` | The central NLDR ingress + commands | MySQL_NLDR + Kafka + Redis_NLDR | REST API | Same set but with NLDR-side configuration |
| `Nldr.SyncWorker` | The central ACK/NACK + command publisher | MySQL_NLDR + Kafka | BackgroundService(s) | same |
| `Nldr.DashboardUi` | The central observability dashboard | Calls Nldr.Api over HTTP | Razor MVC + SignalR/Server-Sent Events for live updates | Observability.AspNetCore |
| `Pacs.TestControl` + `Nldr.TestControl` | Fault-injection sidecar endpoints | In-process state (no DB) | Hosted in `Pacs.Fas.Api` and `Nldr.Api` respectively as `/api/test/*` routes (saves us two extra processes) | none beyond Observability |

> **Decision:** `*.TestControl` is **not a separate process**. It is a route group inside each API project, gated by `Harness:TestMode = true`. This avoids unsolved cross-process state-sharing for fault toggles. In a real installer-packaged deployment, `Harness:TestMode = false` and these routes return 404.

### 3.5 Why not Blazor or SPA?

The reference is `l3_ERPClient` (Razor MVC + Bootstrap + minimal JS). Staying with Razor MVC keeps the harness lightweight, matches the platform convention, and avoids a Node.js toolchain in the installer payload. SignalR (or simple polling) covers the live-update needs.

---

## 4. Repository Layout & Solution Topology

### 4.1 Single-solution layout

```
epacs-sync-harness/
├── ePACS.SyncHarness.sln
│
├── src/
│   ├── Harness.Common/                    # Shared contracts, envelope, hash, correlation, test helpers
│   │
│   ├── Pacs.Fas.Api/                      # FAS-style API (vouchers): Dapper + MySQL_PACS
│   ├── Pacs.Loans.Api/                    # Loans-style API (loan apps with amendments)
│   ├── Pacs.SyncWorker/                   # Outbox drain + Kafka/HTTP publish + inbox consume
│   ├── Pacs.OperatorUi/                   # Razor MVC thin UI (FAS area + Loans area)
│   │
│   ├── Nldr.Api/                          # NLDR ingest + commands + ACK API
│   ├── Nldr.SyncWorker/                   # ACK/NACK publisher + central command publisher
│   ├── Nldr.DashboardUi/                  # Razor MVC central dashboard
│   │
│   └── Harness.ScenarioPlayer/            # Demo Mode orchestrator (one-button demos) [optional library, hosted inside both UIs]
│
├── db/
│   ├── mysql/
│   │   ├── pacs/
│   │   │   ├── V001__core_business.sql           # voucher, voucher_line, loan_app, loan_amend_history, file_sync_registry
│   │   │   ├── V002__sync_tables.sql             # sync_outbox, sync_inbox, sync_sequence, sync_checkpoints, conflict_log
│   │   │   ├── V003__orchestration_compat.sql    # views mapping sync_outbox→OutboxMessages for utils-orchestration relay
│   │   │   └── V004__seed.sql                    # PACS-AP-0001 + PACS-AP-0002 sequence base, lookup data
│   │   └── nldr/
│   │       ├── V001__received_event.sql
│   │       ├── V002__central_policy.sql
│   │       ├── V003__ack_log.sql
│   │       ├── V004__conflict_log.sql
│   │       ├── V005__file_received.sql
│   │       ├── V006__amendment_history.sql
│   │       └── V007__heartbeat.sql
│   └── README.md                          # Migration runner notes (DbUp or Flyway-style)
│
├── docker/
│   ├── docker-compose.yml                 # Kafka + 2× MySQL + 2× Redis + all 7 .NET projects
│   ├── docker-compose.minimal.yml         # Infra only — for `dotnet run` local dev
│   ├── docker-compose.lab.yml             # VM lab profile (network slows, latency injection)
│   └── env/
│       ├── pacs.env
│       └── nldr.env
│
├── packaging/
│   ├── error-catalog/
│   │   └── harness.yaml                   # ERP-PACS-* and ERP-NLDR-* error codes
│   ├── service-map.yaml                   # For installer integration
│   └── installer-manifest-stub.yaml       # Hook for the offline installer
│
├── tests/
│   ├── Harness.ContractTests/             # Envelope schema, hash canonicalization, sequence rules
│   ├── Harness.IntegrationTests/          # Testcontainers-based: API + Worker + DB + Kafka
│   ├── Harness.ChaosTests/                # Pester-driven: power-cut, network-partition, service-kill
│   └── Harness.LongOfflineTests/          # 7/30/60-day simulation runners
│
├── scripts/
│   ├── reset-lab.ps1                      # Drop+recreate DBs, flush Redis, delete Kafka topics, restart
│   ├── reset-lab.sh                       # Linux/macOS equivalent
│   ├── go-online.ps1 / go-offline.ps1     # Firewall toggles
│   ├── kill-service.ps1 / restart-service.ps1
│   ├── hard-powercut-vm.ps1               # Stop-VM -TurnOff + restart
│   ├── collect-evidence.ps1               # Builds the Evidence/RUN-* folder
│   ├── seed-multi-pacs.ps1                # Spin up second PACS profile for SEQ-009
│   └── time-jump.ps1                      # Advance VM clock for LONG/clock-drift tests
│
├── samples/
│   ├── envelope.sample.json
│   ├── appsettings.Development.json
│   └── appsettings.Docker.json
│
├── docs/
│   └── 00-design-overview.md              # this file (single design doc)
│
└── .editorconfig / Directory.Build.props / Directory.Packages.props / NuGet.Config
```

### 4.2 Project references

```
Harness.Common  ◄──── Pacs.Fas.Api
                ◄──── Pacs.Loans.Api
                ◄──── Pacs.SyncWorker
                ◄──── Pacs.OperatorUi
                ◄──── Nldr.Api
                ◄──── Nldr.SyncWorker
                ◄──── Nldr.DashboardUi

Pacs.SyncWorker  ──► (reads MySQL_PACS directly; does NOT depend on Pacs.Fas.Api or Pacs.Loans.Api)
Pacs.OperatorUi  ──► HTTP only to Pacs.Fas.Api + Pacs.Loans.Api + Pacs.TestControl routes
Nldr.DashboardUi ──► HTTP only to Nldr.Api
```

No project depends on another business project; everything either depends on `Harness.Common` or talks over HTTP/Kafka. This mirrors the real l3_* module isolation.

### 4.3 Target framework & language

`net8.0`, `<LangVersion>12</LangVersion>`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (matching AGENTS.md installer standard).

---

## 5. Internal NuGet Integration Map

This is the **critical contract** with the platform. Each consumed NuGet must be wired in the way the platform expects, not improvised.

### 5.1 Per-project consumption table

| Project | `utils-messaging` | `utils-orchestration` | `utils-traceability` | `utils-LAndE` | `utils-caching` |
|---|---|---|---|---|---|
| `Harness.Common` | `Intellect.Erp.Messaging.Contracts` (EventEnvelope) | — | `Intellect.Erp.Traceability.Contracts` only | `Intellect.Erp.ErrorHandling`, `Intellect.Erp.Observability.Abstractions` | — |
| `Pacs.Fas.Api` | `.Kafka.Outbox` (`IOutbox`) — for outbox writes | `Intellect.Erp.Orchestration` (`IOrchestrationOutbox` + `IModuleDbConnectionFactory`) | `Intellect.Erp.Traceability` (`[TraceableAction]`, `.WithMySqlStore()`, `.WithMessagingOutbox()`, `.WithAspNetCoreCapture()`) | `Observability.Core` + `Observability.AspNetCore` (correlation middleware, IAppLogger<T>); `Observability.AuditHooks` for audit bridge; `ErrorHandling` (`IErrorFactory` + `harness.yaml`) | `RedisCaching` (`AddRedisCaching()`, `ICacheProvider`) |
| `Pacs.Loans.Api` | same | same | same + heavier amendment auditing (`Severity = Critical`, `Retention = REGULATORY_10Y`) | same | same |
| `Pacs.SyncWorker` | `.Kafka` (`IKafkaProducer`, `IKafkaConsumer`), `.Kafka.Resilience` (circuit breaker), `.Kafka.HealthChecks` | `Intellect.Erp.Orchestration` (`OrchestrationOutboxRelayHostedService`, `OrchestrationSubscriberHostedService`) | `.Outbox` + `.Consumer` if we audit inbox apply | `Observability.Core`, `Observability.Propagation` (`TraceableBackgroundService` base) | `RedisCaching` for distributed lock `sync:lock:{pacs_id}` |
| `Pacs.OperatorUi` | none directly (calls APIs over HTTP) | none | none | `Observability.AspNetCore` for correlation forwarding; `ErrorHandling` for operator-friendly error rendering | `RedisCaching` for session |
| `Nldr.Api` | `.Kafka.Outbox` (ACK/command outbox) | `Intellect.Erp.Orchestration` (inbox dedupe via `IInboxStore`) | `Intellect.Erp.Traceability` (ingest events) | same as Pacs.Fas.Api | `RedisCaching` |
| `Nldr.SyncWorker` | same as Pacs.SyncWorker | same | optional | same | optional |
| `Nldr.DashboardUi` | none | none | none | `Observability.AspNetCore` | `RedisCaching` for session |

### 5.2 What each NuGet contributes (concretely)

**`utils-messaging` (Intellect.Erp.Messaging v2.2):**
- `IOutbox.EnqueueAsync(...)` invoked inside the same Dapper `DbTransaction` as the business write — closes G-04.
- `IKafkaProducer.PublishAsync(...)` with `PublishMetadata { CorrelationId, IdempotencyKey, PartitionKey }`.
- `IKafkaConsumer` + `[MessageHandler]` for typed inbox handlers in `Pacs.SyncWorker` and `Nldr.SyncWorker`.
- `OutboxPublisherHostedService` drains `OutboxMessages`; we adapt it to drain `sync_outbox` via view (see §6.3).
- `.Kafka.Resilience.CircuitBreaker` for OFF-003 / FAIL-001..005 retry semantics.

**`utils-orchestration` (Intellect.Erp.Orchestration):**
- `IModuleDbConnectionFactory` — each PACS API and NLDR API implements this to return an open `MySqlConnection`.
- `IOrchestrationOutbox.EnqueueAsync(...)` — preferred over `IOutbox` because it writes into the standardized envelope shape and integrates with `OrchestrationOutboxRelayHostedService`.
- `[SubscribeToOutbox(Topic=..., ConsumerGroup=..., EventType=..., SchemaVersion=...)]` on each inbox handler. Dispatcher flow (per the README): consume → start tx → insert inbox row → invoke handler → mark inbox processed → commit tx → commit Kafka offset. **This already gives us I-3 (idempotent receiver) for free.**
- `OrchestrationContext.Connection` + `.Transaction` are used by handlers (no service locator).
- `SagaInstances` table — we use it for the **multi-step amendment workflow** in Loans (`maker → checker → applied`), although it is not strictly required for the harness's first milestone.

**`utils-traceability` (Intellect.Erp.Traceability):**
- `[TraceableAction(action: "Loans.LoanApp.Amend", entityType: "LoanApp", EntityIdArgument = nameof(...), Severity = TraceSeverity.Critical, Sensitivity = Sensitivity.CONFIDENTIAL, Retention = RetentionClass.REGULATORY_10Y, CaptureBeforeAfter = true, PublishToKafka = true)]` on AMEND/DELETE handlers — closes G-06 (third witness).
- `.WithMySqlStore() + .WithMessagingOutbox()` chain in `AddTraceability(...)` so audit events are published over the same Kafka transport.
- `.WithAspNetCoreCapture()` for HTTP capture; `.WithJobsCapture()` for the worker.

**`utils-LAndE` (Intellect.Erp.Observability + ErrorHandling):**
- `IAppLogger<T>` everywhere (not raw `ILogger<T>`), with `BeginOperation("Pacs", "Fas", "CreateVoucher")` and `Checkpoint("OutboxEnqueued", new {...})`.
- `Observability.AspNetCore.UseCorrelationId()` + `UseGlobalExceptionHandler()` + `UseRequestLogging()`.
- `Observability.Propagation.TraceableBackgroundService` as the base for `Pacs.SyncWorker` and `Nldr.SyncWorker` — so each Kafka consume runs inside a correlation scope tied to the inbound event.
- `Observability.Propagation.KafkaHeaders` for correlation/causation header reading/writing.
- `Observability.AuditHooks` bridges audit events from the API into `Intellect.Erp.Traceability` without the controller knowing.
- `Intellect.Erp.ErrorHandling.IErrorFactory.FromCatalog("ERP-PACS-INS-0010", ...)` — every thrown exception goes through the YAML catalog (`packaging/error-catalog/harness.yaml`).

**`utils-caching` (RedisCaching):**
- `services.AddRedisCaching(configuration)` — same registration as `l3_FAS` Program.cs.
- `ICacheProvider` (preferred) for `lookup:txn-type:*`, `lookup:loan-status:*`, etc.
- `IDistributedCache` (StackExchange.Redis) for `session:*`, `sync:lock:{pacs_id}` with `SET NX PX 60000`.
- Cache provider must be **fail-open** (`AbortOnConnectFail = false`) so FAIL-007 / CRIT-005 pass.

### 5.3 What the harness must NOT do

- Re-implement an outbox writer (use `IOrchestrationOutbox`).
- Re-implement audit (use `[TraceableAction]`).
- Re-implement correlation propagation (use `Observability.AspNetCore` middleware).
- Re-implement Kafka serializer / consumer loop (use `Intellect.Erp.Messaging.Kafka`).
- Re-implement circuit breaker (use `.Kafka.Resilience`).
- Add raw `Microsoft.Extensions.Caching.StackExchangeRedis` directly (use `RedisCaching`).

---

## 6. Database Design

### 6.1 PACS MySQL schema (`epacs_pacs`)

```sql
-- =========================================================================
-- V001__core_business.sql
-- =========================================================================

CREATE TABLE voucher (
    voucher_id        BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    voucher_no        VARCHAR(50)  NOT NULL,             -- monotonic per pacs_id, NOT same as sequence_no
    voucher_date      DATE         NOT NULL,
    voucher_type      VARCHAR(50)  NOT NULL,             -- 'CR', 'DB', 'JV', 'PV', 'RV'
    narration         VARCHAR(500) NULL,
    total_amount      DECIMAL(18,2) NOT NULL,
    status            VARCHAR(30)  NOT NULL,             -- 'DRAFT','POSTED','DELETED'
    is_deleted        TINYINT(1)   NOT NULL DEFAULT 0,
    created_by        VARCHAR(100) NOT NULL,
    created_at        DATETIME(6)  NOT NULL,
    updated_at        DATETIME(6)  NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    UNIQUE KEY uq_pacs_voucher_no (pacs_id, voucher_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE voucher_line (
    voucher_line_id   BIGINT       PRIMARY KEY AUTO_INCREMENT,
    voucher_id        BIGINT       NOT NULL,
    account_code      VARCHAR(50)  NOT NULL,
    debit_amount      DECIMAL(18,2) NOT NULL DEFAULT 0,
    credit_amount     DECIMAL(18,2) NOT NULL DEFAULT 0,
    line_narration    VARCHAR(500) NULL,
    FOREIGN KEY (voucher_id) REFERENCES voucher(voucher_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Matches docs/deletionsenerio.md "fa_voucherdeletionmain" pattern.
CREATE TABLE voucher_deletion_audit (
    deletion_id       BIGINT       PRIMARY KEY AUTO_INCREMENT,
    voucher_id        BIGINT       NOT NULL,
    pacs_id           VARCHAR(50)  NOT NULL,
    voucher_no        VARCHAR(50)  NOT NULL,
    deleted_by        VARCHAR(100) NOT NULL,
    deleted_at        DATETIME(6)  NOT NULL,
    reason            VARCHAR(500) NULL,
    before_state_json LONGTEXT     NOT NULL,             -- mandatory; NEG-007 requires it
    correlation_id    VARCHAR(64)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE loan_application (
    loan_app_id       BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    loan_app_no       VARCHAR(50)  NOT NULL,
    member_no         VARCHAR(50)  NOT NULL,
    member_name       VARCHAR(200) NOT NULL,             -- subject to PII redaction in logs
    requested_amount  DECIMAL(18,2) NOT NULL,
    approved_amount   DECIMAL(18,2) NULL,
    purpose           VARCHAR(500) NULL,
    status            VARCHAR(30)  NOT NULL,             -- 'DRAFT','SUBMITTED','APPROVED','REJECTED','DISBURSED','AMENDED','CANCELLED'
    is_deleted        TINYINT(1)   NOT NULL DEFAULT 0,
    maker             VARCHAR(100) NOT NULL,
    checker           VARCHAR(100) NULL,
    created_at        DATETIME(6)  NOT NULL,
    updated_at        DATETIME(6)  NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    UNIQUE KEY uq_pacs_loan_app_no (pacs_id, loan_app_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE loan_amendment_history (
    amendment_id      BIGINT       PRIMARY KEY AUTO_INCREMENT,
    loan_app_id       BIGINT       NOT NULL,
    amended_at        DATETIME(6)  NOT NULL,
    amended_by        VARCHAR(100) NOT NULL,
    approver          VARCHAR(100) NOT NULL,             -- NEG-009: amendment without approver must be rejected
    reason            VARCHAR(1000) NOT NULL,            -- NEG-009: reason mandatory
    before_state_json LONGTEXT     NOT NULL,
    after_state_json  LONGTEXT     NOT NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    FOREIGN KEY (loan_app_id) REFERENCES loan_application(loan_app_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE file_sync_registry (
    file_id           BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    entity_type       VARCHAR(100) NOT NULL,             -- 'voucher_attachment', 'loan_doc'
    entity_id         VARCHAR(100) NOT NULL,
    file_name         VARCHAR(500) NOT NULL,
    file_size_bytes   BIGINT       NOT NULL,
    file_sha256       CHAR(64)     NOT NULL,
    total_chunks      INT          NOT NULL,
    chunks_acked      INT          NOT NULL DEFAULT 0,
    priority          TINYINT      NOT NULL DEFAULT 100, -- lower = higher priority (photos before reports)
    status            VARCHAR(30)  NOT NULL,             -- 'PENDING','UPLOADING','ACKED','FAILED'
    created_at        DATETIME(6)  NOT NULL,
    completed_at      DATETIME(6)  NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    UNIQUE KEY uq_pacs_file_hash (pacs_id, file_sha256)  -- NEG-020 / OFF-006 dedup after rename
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

```sql
-- =========================================================================
-- V002__sync_tables.sql
-- =========================================================================

CREATE TABLE sync_sequence (
    pacs_id           VARCHAR(50) NOT NULL,
    stream_name       VARCHAR(50) NOT NULL,              -- 'pacs.outbound', 'pacs.heartbeat'
    next_sequence     BIGINT      NOT NULL,
    updated_at        DATETIME(6) NOT NULL,
    PRIMARY KEY (pacs_id, stream_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE sync_outbox (
    outbox_id         BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    sequence_no       BIGINT       NOT NULL,
    event_id          CHAR(36)     NOT NULL,
    idempotency_key   VARCHAR(200) NOT NULL,
    change_type       ENUM('INSERT','UPDATE','DELETE','AMENDMENT') NOT NULL,
    entity_type       VARCHAR(100) NOT NULL,
    entity_id         VARCHAR(100) NOT NULL,
    topic             VARCHAR(255) NOT NULL,             -- 'epacs.pacs.outbound'
    schema_version    VARCHAR(20)  NOT NULL DEFAULT 'v1',
    payload_json      LONGTEXT     NULL,                 -- canonicalized; see §7
    before_state_json LONGTEXT     NULL,                 -- mandatory for DELETE/AMENDMENT
    payload_hash      CHAR(64)     NOT NULL,             -- sha256(canonical(payload + beforeState))
    priority          TINYINT      NOT NULL DEFAULT 100, -- §8.6 reconnect drain order
    status            ENUM('PENDING','IN_FLIGHT','ACKED','FAILED','DEADLETTER') NOT NULL DEFAULT 'PENDING',
    retry_count       INT          NOT NULL DEFAULT 0,
    last_error        VARCHAR(2000) NULL,
    created_at        DATETIME(6)  NOT NULL,
    sent_at           DATETIME(6)  NULL,
    ack_at            DATETIME(6)  NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    causation_id      VARCHAR(64)  NULL,
    UNIQUE KEY uq_event (event_id),
    UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no),
    KEY ix_status_priority_created (status, priority, created_at),
    KEY ix_correlation (correlation_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE sync_inbox (
    inbox_id          BIGINT       PRIMARY KEY AUTO_INCREMENT,
    source_system     VARCHAR(50)  NOT NULL,             -- 'NLDR'
    event_id          CHAR(36)     NOT NULL,
    sequence_no       BIGINT       NULL,                 -- nullable: command streams may be sequenceless
    payload_hash      CHAR(64)     NOT NULL,
    idempotency_key   VARCHAR(200) NOT NULL,
    status            ENUM('RECEIVED','APPLIED','DUPLICATE','REJECTED') NOT NULL,
    reject_reason     VARCHAR(500) NULL,                 -- error code from harness.yaml
    received_at       DATETIME(6)  NOT NULL,
    applied_at        DATETIME(6)  NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    UNIQUE KEY uq_inbox_event (event_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE sync_checkpoints (
    checkpoint_id     BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    stream_name       VARCHAR(50)  NOT NULL,             -- 'pacs.outbound', 'nldr.commands'
    last_acked_sequence    BIGINT  NOT NULL,             -- outbound: last seq ACKed by NLDR
    last_received_sequence BIGINT  NOT NULL,             -- inbound: last seq applied from NLDR
    updated_at        DATETIME(6)  NOT NULL,
    UNIQUE KEY uq_checkpoint (pacs_id, stream_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE conflict_log (
    conflict_id       BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    entity_type       VARCHAR(100) NOT NULL,
    entity_id         VARCHAR(100) NOT NULL,
    local_state_json  LONGTEXT     NOT NULL,
    remote_state_json LONGTEXT     NOT NULL,
    detected_at       DATETIME(6)  NOT NULL,
    resolution        VARCHAR(30)  NULL,                 -- 'LOCAL','REMOTE','MANUAL','PENDING'
    resolved_at       DATETIME(6)  NULL,
    resolved_by       VARCHAR(100) NULL,
    correlation_id    VARCHAR(64)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 6.2 NLDR MySQL schema (`epacs_nldr`)

```sql
CREATE TABLE received_event (
    received_id       BIGINT       PRIMARY KEY AUTO_INCREMENT,
    event_id          CHAR(36)     NOT NULL,
    pacs_id           VARCHAR(50)  NOT NULL,
    sequence_no       BIGINT       NOT NULL,
    change_type       ENUM('INSERT','UPDATE','DELETE','AMENDMENT') NOT NULL,
    entity_type       VARCHAR(100) NOT NULL,
    entity_id         VARCHAR(100) NOT NULL,
    payload_json      LONGTEXT     NULL,
    before_state_json LONGTEXT     NULL,
    payload_hash      CHAR(64)     NOT NULL,
    received_at       DATETIME(6)  NOT NULL,
    apply_status      ENUM('APPLIED','DUPLICATE','REJECTED','GAP_WAITING') NOT NULL,
    reject_reason     VARCHAR(500) NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    UNIQUE KEY uq_event (event_id),
    UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no)        -- catches duplicate sequence (NEG-010)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE nldr_business_voucher (
    voucher_id        BIGINT       PRIMARY KEY,
    pacs_id           VARCHAR(50)  NOT NULL,
    voucher_no        VARCHAR(50)  NOT NULL,
    voucher_date      DATE         NOT NULL,
    voucher_type      VARCHAR(50)  NOT NULL,
    total_amount      DECIMAL(18,2) NOT NULL,
    is_deleted        TINYINT(1)   NOT NULL DEFAULT 0,   -- NLDR soft-deletes only
    deleted_at        DATETIME(6)  NULL,
    deletion_reason   VARCHAR(500) NULL,
    deletion_correlation_id VARCHAR(64) NULL,
    UNIQUE KEY uq_pacs_voucher_no (pacs_id, voucher_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE nldr_business_loan (
    loan_app_id       BIGINT       PRIMARY KEY,
    pacs_id           VARCHAR(50)  NOT NULL,
    loan_app_no       VARCHAR(50)  NOT NULL,
    member_no         VARCHAR(50)  NOT NULL,
    member_name       VARCHAR(200) NOT NULL,
    requested_amount  DECIMAL(18,2) NOT NULL,
    approved_amount   DECIMAL(18,2) NULL,
    status            VARCHAR(30)  NOT NULL,
    is_deleted        TINYINT(1)   NOT NULL DEFAULT 0,
    UNIQUE KEY uq_pacs_loan_app_no (pacs_id, loan_app_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE nldr_amendment_history (
    amendment_id      BIGINT       PRIMARY KEY AUTO_INCREMENT,
    loan_app_id       BIGINT       NOT NULL,
    amended_at        DATETIME(6)  NOT NULL,
    amended_by        VARCHAR(100) NOT NULL,
    approver          VARCHAR(100) NOT NULL,
    reason            VARCHAR(1000) NOT NULL,
    before_state_json LONGTEXT     NOT NULL,
    after_state_json  LONGTEXT     NOT NULL,
    source_event_id   CHAR(36)     NOT NULL,
    correlation_id    VARCHAR(64)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE central_policy (
    policy_id         BIGINT       PRIMARY KEY AUTO_INCREMENT,
    policy_code       VARCHAR(100) NOT NULL UNIQUE,
    policy_name       VARCHAR(200) NOT NULL,
    payload_json      LONGTEXT     NOT NULL,
    effective_from    DATETIME(6)  NOT NULL,
    created_at        DATETIME(6)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE ack_log (
    ack_id            BIGINT       PRIMARY KEY AUTO_INCREMENT,
    event_id          CHAR(36)     NOT NULL,
    pacs_id           VARCHAR(50)  NOT NULL,
    sequence_no       BIGINT       NOT NULL,
    ack_status        ENUM('ACK','NACK') NOT NULL,
    nack_reason       VARCHAR(500) NULL,
    acked_at          DATETIME(6)  NOT NULL,
    correlation_id    VARCHAR(64)  NOT NULL,
    KEY ix_pacs_seq (pacs_id, sequence_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE sequence_gap (
    gap_id            BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    missing_sequence  BIGINT       NOT NULL,
    detected_at       DATETIME(6)  NOT NULL,
    resolved_at       DATETIME(6)  NULL,
    UNIQUE KEY uq_pacs_seq (pacs_id, missing_sequence)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE conflict_log (
    conflict_id       BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    entity_type       VARCHAR(100) NOT NULL,
    entity_id         VARCHAR(100) NOT NULL,
    local_state_json  LONGTEXT     NOT NULL,
    remote_state_json LONGTEXT     NOT NULL,
    detected_at       DATETIME(6)  NOT NULL,
    resolution        VARCHAR(30)  NULL,
    resolved_at       DATETIME(6)  NULL,
    correlation_id    VARCHAR(64)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE file_received (
    file_id           BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    entity_type       VARCHAR(100) NOT NULL,
    entity_id         VARCHAR(100) NOT NULL,
    file_name         VARCHAR(500) NOT NULL,
    file_sha256       CHAR(64)     NOT NULL,
    chunks_received   INT          NOT NULL DEFAULT 0,
    total_chunks      INT          NOT NULL,
    status            VARCHAR(30)  NOT NULL,             -- 'ASSEMBLING','COMPLETED','REJECTED'
    received_at       DATETIME(6)  NOT NULL,
    UNIQUE KEY uq_pacs_file_hash (pacs_id, file_sha256)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE heartbeat (
    heartbeat_id      BIGINT       PRIMARY KEY AUTO_INCREMENT,
    pacs_id           VARCHAR(50)  NOT NULL,
    received_at       DATETIME(6)  NOT NULL,
    payload_json      LONGTEXT     NOT NULL,             -- outbox depth, last seq, build version
    KEY ix_pacs_received (pacs_id, received_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 6.3 Reconciling Test Plan schema vs `utils-orchestration` tables (G-01)

The `utils-orchestration` NuGet ships:
- `OutboxMessages(message_id, event_id, event_type, topic, schema_version, payload_json, message_key, correlation_id, causation_id, saga_id, idempotency_key, status, attempt_count, last_error, created_at_utc, published_at_utc, updated_at_utc)`
- `InboxMessages(event_id, event_type, schema_version, topic, partition_id, offset_value, consumer_group, correlation_id, saga_id, idempotency_key, status, received_at_utc, processed_at_utc, error, attempt_count)`

The Test Plan demands extra columns: `pacs_id`, `sequence_no`, `change_type`, `entity_type`, `entity_id`, `before_state_json`, `payload_hash`, `priority`. These are **not** in the orchestration tables.

**Decision (G-01):** We use a single authoritative table `sync_outbox` with all extra columns (§6.1) — and we **bypass** `OrchestrationOutboxRelayHostedService` in favour of a **harness-owned `SyncOutboxRelay` BackgroundService** (in `Pacs.SyncWorker`) that reads `sync_outbox` directly using `SELECT … FOR UPDATE SKIP LOCKED` and publishes via `IKafkaProducer`. We still use `IOrchestrationOutbox` shape (envelope, contract) for consistency and `IOrchestrationTransactionFactory` to obtain transactions, but the **physical table** is `sync_outbox`.

Rationale:
- Sequence allocation must happen inside the same tx as the outbox write (I-2 + G-04). Doing this through `OrchestrationOutboxRelayHostedService` would force us to add `sequence_no` to `OutboxMessages` for everyone else, which the platform doesn't want.
- `priority` and `change_type` are sync-test-plan-specific.
- `before_state_json` semantics (mandatory for DELETE/AMENDMENT, NEG-007/008) belong on the sync row, not the platform outbox.

For the **inbox**, we **do** use `Intellect.Erp.Orchestration`'s `IInboxStore` (`InboxMessages` table) for idempotency dedupe, and write a corresponding `sync_inbox` row for the audit/observability view exposed in the dashboard. The two tables stay in lock-step inside the handler transaction.

This is documented in `db/mysql/pacs/V003__orchestration_compat.sql` which creates the orchestration tables and a view `OutboxMessages_view` for any platform tool that expects the canonical name.

### 6.4 Indexes, partition strategy, growth budget (for PERF-002)

- `sync_outbox` is the hottest table. After 30 days at ~1 000 INSERT events/day + 20 UPDATEs/day + 5 DELETEs/day per PACS, expected size ≈ 30 800 rows. With 200 MB upper bound for `payload_json + before_state_json` averages, total ~6 GB per PACS. Acceptable.
- Index `ix_status_priority_created` accelerates the relay drain query.
- Partitioning: not in v1; revisit only if `sync_outbox` > 10 M rows.
- Pruning: ACKed rows older than `Sync:OutboxRetentionDays` (default 90) moved to `sync_outbox_archive` table by a daily job.

---

## 7. Event Envelope & Canonicalization (closes G-05)

### 7.1 Wire envelope (JSON)

```json
{
  "schemaVersion": "1.0",
  "eventId": "7f8a-…-UUIDv4",
  "correlationId": "01J…ULID",
  "causationId": "01J…ULID",
  "pacsId": "PACS-AP-0001",
  "sourceSystem": "PACS",
  "targetSystem": "NLDR",
  "sequenceNo": 1042,
  "streamName": "pacs.outbound",
  "idempotencyKey": "PACS-AP-0001:voucher:21150000000042:INSERT:2026-05-14T10:00:00Z",
  "changeType": "INSERT",
  "entityType": "voucher",
  "entityId": "21150000000042",
  "payload":      { "after": { /* business state */ } },
  "beforeState":  { /* present for UPDATE/DELETE/AMENDMENT */ },
  "amendmentMeta":{ "reason": "…", "approver": "…" },
  "payloadHash":  "sha256-hex-over-canonical-json",
  "createdAtUtc": "2026-05-14T10:00:00.000000Z"
}
```

### 7.2 Canonicalization rule (deterministic, version-stable)

**The canonical form is JSON with:**
1. Lexicographically sorted object keys at every depth (UTF-16 code unit order).
2. No insignificant whitespace.
3. Numbers serialized using `R` round-trip format (`double` to invariant string, no trailing zeros).
4. Booleans lowercase, nulls as `null`.
5. Strings escaped per RFC 8259.
6. UTF-8 byte sequence is hashed.
7. Hash algorithm: **SHA-256**.
8. Hash hex output lowercase, **no `sha256-` prefix in the wire field** (the `sha256-` prefix is only used in human-readable logs).

The implementation lives once in `Harness.Common.Canonicalization.CanonicalJsonWriter` and is the **only** place hashing happens. Any other call site is a bug. Reviewers should grep for `SHA256.HashData` and verify it appears only there.

### 7.3 Hash input scope

`payloadHash = sha256_hex(canonical_json({ "payload": payload, "beforeState": beforeState, "amendmentMeta": amendmentMeta }))`

— i.e. excludes envelope metadata that may legitimately differ across retries (`eventId`, `correlationId`, timestamps). This is what allows SEQ-006 (same `idempotency_key`, new `eventId`) to succeed while CRIT-010 (same `eventId`, altered amount) is rejected.

### 7.4 Idempotency key format

`{pacsId}:{entityType}:{entityId}:{changeType}:{businessTimestampISO8601}`

Example: `PACS-AP-0001:loan_application:LA-2026-00042:AMENDMENT:2026-05-14T10:00:00Z`.

Two replays of the same logical operation produce identical idempotency keys. Two different INSERTs of the same entity (which should never happen) would collide and be rejected. The receiver uses `idempotencyKey` as the dedupe key when `eventId` differs (SEQ-006).

### 7.5 Sample (full INSERT)

See `samples/envelope.sample.json` (referenced from §35).

---

## 8. Sync State Machine

### 8.1 Outbox lifecycle

```
            ┌─────────────────────────┐
            │     PENDING (initial)   │
            └───────────┬─────────────┘
                        │  relay picks up
                        ▼
            ┌─────────────────────────┐
            │       IN_FLIGHT         │
            └─┬─────────┬───────────┬─┘
              │         │           │
              │ACK      │NACK/Err   │retry_count >= max
              ▼         ▼           ▼
        ┌───────┐  ┌────────┐  ┌────────────┐
        │ ACKED │  │ FAILED │  │ DEADLETTER │
        └───────┘  └────┬───┘  └────────────┘
                        │  next attempt resets to PENDING
                        ▼
                     PENDING
```

- `IN_FLIGHT` is set with the row locked (`SELECT … FOR UPDATE SKIP LOCKED`) so a crash leaves the row resumable.
- After `Sync:ProcessingLockTimeoutSeconds` (default 120 s) an `IN_FLIGHT` row is automatically released to `PENDING` by `Pacs.SyncWorker.LockReaper` — this powers FAIL-008 / PWR-002.
- `DEADLETTER` events are visible in PACS UI + NLDR dashboard and require manual operator action.

### 8.2 Inbox lifecycle

```
RECEIVED ──(validate envelope, hash, seq, idempotency)──┐
                                                        │
            ┌─────────────────┬──────────────┬──────────┤
            ▼                 ▼              ▼          ▼
        APPLIED          DUPLICATE       REJECTED    GAP_WAITING (only on NLDR side)
```

- `DUPLICATE` is **not** an error. It is a success. The producer should observe ACK or NACK accordingly.
- `REJECTED` reasons (with error code from harness.yaml):
  - `ERP-NLDR-VAL-0001` schema invalid
  - `ERP-NLDR-VAL-0002` hash mismatch
  - `ERP-NLDR-VAL-0003` missing `eventId`
  - `ERP-NLDR-VAL-0004` negative `sequenceNo`
  - `ERP-NLDR-VAL-0005` future `createdAtUtc` beyond drift threshold
  - `ERP-NLDR-VAL-0006` DELETE without `beforeState`
  - `ERP-NLDR-VAL-0007` AMENDMENT without reason/approver
  - `ERP-NLDR-SEC-0001` certificate / test-token invalid

### 8.3 Sequence allocation (closes G-04)

Inside the **same Dapper transaction** as the business write:

```sql
-- pseudo-code, parameterized in C#
START TRANSACTION;

INSERT INTO voucher (...) VALUES (...);
SET @vid = LAST_INSERT_ID();

INSERT INTO voucher_line (voucher_id, ...) VALUES (@vid, ...);
-- ... more business rows

-- Atomically reserve next sequence_no:
UPDATE sync_sequence
   SET next_sequence = next_sequence + 1, updated_at = NOW(6)
 WHERE pacs_id = @pacs AND stream_name = 'pacs.outbound';

SELECT next_sequence - 1 INTO @seq
  FROM sync_sequence
 WHERE pacs_id = @pacs AND stream_name = 'pacs.outbound';

INSERT INTO sync_outbox (pacs_id, sequence_no, event_id, …) VALUES (@pacs, @seq, UUID(), …);

COMMIT;
```

The `UPDATE … SET = +1` + read pattern is safe under InnoDB row-level locking (the row is locked for the duration of the transaction). Two concurrent inserts will serialise on this row, guaranteeing **monotonic, contiguous sequence numbers**.

`Pacs.Fas.Api.SequenceAllocator.GetNextAsync(conn, tx, pacsId, stream)` implements this once. All callers use it.

### 8.4 Checkpoints

- `sync_checkpoints.last_acked_sequence` advances only when an ACK is received for `last_acked_sequence + 1`. If ACK arrives out of order (ACK for 10 while 9 is pending), the receiver **buffers** it (in memory + persisted state) and advances when 9 ACK arrives. This satisfies NEG-018 ("ACK advances beyond missing sequence" → refused).
- `sync_checkpoints.last_received_sequence` (inbound stream) advances when inbox `APPLIED` rows are contiguous from the last advance point.

### 8.5 Retry, backoff, circuit breaker, DLQ

Per `utils-messaging .Kafka.Resilience`:

| Setting | Default | Source | Tests |
|---|---|---|---|
| `Sync:Retry:MaxAttempts` | 7 | `Pacs.SyncWorker:Sync:Retry:MaxAttempts` | FAIL-001 |
| `Sync:Retry:BaseDelayMs` | 2000 | same | FAIL-001 |
| `Sync:Retry:MaxDelayMs` | 60000 | same | FAIL-001 |
| `Sync:Retry:JitterFactor` | 0.2 | same | FAIL-001 |
| `Sync:Retry:RespectRetryAfter` | true | same | FAIL-002 |
| `Sync:Circuit:FailureThreshold` | 5 (consecutive) | same | FAIL-004 |
| `Sync:Circuit:OpenDurationSeconds` | 60 | same | FAIL-004 |
| `Sync:Circuit:HalfOpenProbeCount` | 1 | same | FAIL-004 |
| `Sync:Outbox:QuarantineAfterAttempts` | 10 | same | FAIL-010 (DLQ) |

Quarantined rows → `DEADLETTER` + publish to `epacs.deadletter` topic.

### 8.6 Reconnect drain prioritization (closes G-19)

Per OFF-003 ("Reconnect drains backlog in priority order — financial events ACK first"):

Drain query:
```sql
SELECT * FROM sync_outbox
WHERE status = 'PENDING'
ORDER BY priority ASC, sequence_no ASC
LIMIT @batch
FOR UPDATE SKIP LOCKED;
```

Default `priority` values (configured in `appsettings.json` under `Sync:Priority:*`):

| Entity / change_type | priority |
|---|---|
| voucher INSERT/UPDATE/DELETE | 10 |
| loan_application AMENDMENT | 20 |
| loan_application INSERT/UPDATE | 30 |
| file (small ≤ 1 MB) | 50 |
| file (large > 1 MB) | 80 |
| heartbeat | 200 |

This ensures financial events drain first, photos before reports (SYNC-POS-010), and heartbeat doesn't starve work.

`sequence_no ASC` as **secondary** sort preserves ordering inside a priority class.

### 8.7 Worker concurrency model

- Single `Pacs.SyncWorker` process; multiple internal hosted services:
  - `OutboundRelayService` — drains `sync_outbox`, publishes to Kafka **and** posts to NLDR HTTP `/api/sync/ingest` (the test plan supports both transports; Kafka is the primary).
  - `InboundConsumerService` — Kafka consumer on `epacs.nldr.commands` + `epacs.nldr.acks`.
  - `HeartbeatService` — publishes heartbeat every `Sync:Heartbeat:IntervalSeconds` (default 30).
  - `LockReaperService` — releases stale `IN_FLIGHT` locks every 30 s.
  - `ReconciliationService` — runs nightly + on demand.
- All inherit `Observability.Propagation.TraceableBackgroundService`.

---

## 9. Kafka Topic Design

| Topic | Producer | Consumer | Key | Partitions | Retention | Purpose |
|---|---|---|---|---|---|---|
| `epacs.pacs.outbound` | `Pacs.SyncWorker.OutboundRelayService` | `Nldr.SyncWorker.IngestConsumer` | `pacs_id` | 3 | 7 days | PACS business events to NLDR |
| `epacs.nldr.acks` | `Nldr.SyncWorker.AckPublisher` | `Pacs.SyncWorker.InboundConsumerService` | `pacs_id` | 3 | 7 days | ACK/NACK for outbound events |
| `epacs.nldr.commands` | `Nldr.SyncWorker.CommandPublisher` | `Pacs.SyncWorker.InboundConsumerService` | `pacs_id` | 3 | 7 days | Central policies, master data, corrections |
| `epacs.pacs.heartbeat` | `Pacs.SyncWorker.HeartbeatService` | `Nldr.SyncWorker.HeartbeatConsumer` + dashboard | `pacs_id` | 1 | 1 day | Online/offline + health payload |
| `epacs.deadletter` | both workers | QA/support tooling | `event_id` | 1 | 30 days | Poison events |
| `epacs.audit.traceability` | `Intellect.Erp.Traceability` outbox | optional external collector | `tenant` | 3 | 7 days | Three-witness audit stream |

### 9.1 Required Kafka headers (all topics)

| Header | Value | Used by |
|---|---|---|
| `correlationId` | from envelope | Observability.Propagation.KafkaHeaders |
| `causationId` | from envelope | same |
| `eventId` | from envelope | dedupe |
| `eventType` | e.g. `epacs.voucher.INSERT` | router |
| `schemaVersion` | from envelope | router |
| `pacsId` | from envelope | observability |
| `sequenceNo` | from envelope | gap detection at consumer |
| `idempotencyKey` | from envelope | dedupe |

Topics are created at startup by `Pacs.SyncWorker.KafkaTopicInitializer` (idempotent `CreateTopicsAsync` via AdminClient).

---

## 10. Redis Usage Contract

| Key pattern | Owner | TTL | Purpose | Test that asserts disposability |
|---|---|---|---|---|
| `pacs:lookup:txn-type:*` | `Pacs.Fas.Api` | 6 h | Voucher type master | SYNC-POS-008, CRIT-005 |
| `pacs:lookup:loan-status:*` | `Pacs.Loans.Api` | 6 h | Loan status master | same |
| `pacs:session:*` | `Pacs.OperatorUi` | 30 min | Operator session | OFF-001 (UI must survive Redis flush mid-session) |
| `pacs:sync:lock:{pacs_id}` | `Pacs.SyncWorker` | 60 s (renewed every 20 s) | Single-leader lock when multiple workers race | n/a |
| `pacs:health:last-success:{stream}` | `Pacs.SyncWorker` | 5 min | Heartbeat hint for UI banner | UI-001, OFF-004 |
| `pacs:offline-flag` | `Pacs.TestControl` | none (manual TestControl write) | Forces UI to render Offline banner | Demo Mode |
| `nldr:lookup:central-policy:*` | `Nldr.Api` | 1 h | Policy cache | same disposability rule |

**Fail-open contract:** Every Redis call is wrapped so a Redis outage degrades to MySQL reads or no-op, never raises an exception out of business code. This is what makes FAIL-007 / SEC-006 pass.

---

## 11. File Sync (closes G-08)

### 11.1 Producer side (PACS)

1. Operator uploads file via `POST /api/files/upload` (multipart). `Pacs.Fas.Api` or `Pacs.Loans.Api`:
   1. Streams file to `{DataRoot}/files/staging/{guid}.tmp`, computing SHA-256 on the fly.
   2. Looks up `file_sync_registry` by `(pacs_id, file_sha256)` for dedup (NEG-020 / SYNC-POS-010).
   3. If new: insert `file_sync_registry` row with `status='PENDING'`, `total_chunks = ceil(size / Sync:File:ChunkSizeBytes)`.
   4. Move file to `{DataRoot}/files/queue/{file_id}.dat`.
   5. Insert one `sync_outbox` row with `change_type='INSERT'`, `entity_type='file'`, `entity_id={file_id}`, `payload_json={metadata only}`.
2. `Pacs.SyncWorker.FileChunkUploader` (separate hosted service):
   1. Reads `file_sync_registry` rows where `status='PENDING' OR 'UPLOADING'` ordered by `priority, file_id`.
   2. For each, streams chunks of `Sync:File:ChunkSizeBytes` (default 256 KB) over HTTP to `Nldr.Api POST /api/files/{file_id}/chunks/{index}`.
   3. NLDR responds 200 with chunk index ACK. PACS updates `chunks_acked`.
   4. After last chunk: NLDR verifies full-file SHA-256 vs registry hash. On success, `file_received.status='COMPLETED'` and NLDR sends ACK on `epacs.nldr.acks` for the file event.
   5. Resume: on restart, `chunks_acked` is authoritative; PACS resumes from `chunks_acked + 1`. This is what makes PWR-008 / OFF-006 pass.

### 11.2 Failure modes covered

| Test | Behaviour |
|---|---|
| OFF-006 | Files queue while offline; reconnect drains by priority |
| PWR-008 | Resume from `chunks_acked` after power-off |
| NEG-020 | Chunk hash mismatch → chunk rejected, retry; full-file hash mismatch → whole file rejected, registry status=`FAILED` |
| SYNC-POS-010 | `priority` column ensures photo (200 KB, priority 50) ACKs before report (10 MB, priority 80) |

### 11.3 Configuration

```json
"Sync": {
  "File": {
    "ChunkSizeBytes": 262144,
    "MaxConcurrentChunks": 4,
    "StagingPath": "${DataRoot}/files/staging",
    "QueuePath": "${DataRoot}/files/queue",
    "MaxFileSizeMb": 50,
    "SmallFileThresholdKb": 1024
  }
}
```

---

## 12. Application Design Cards

Each card states: tables touched, endpoints, internal services, fault hooks, mapped tests, observability points.

### 12.1 `Pacs.Fas.Api` — Voucher backend

#### 12.1.1 Tables

- Reads/writes: `voucher`, `voucher_line`, `voucher_deletion_audit`, `sync_outbox`, `sync_sequence`, `file_sync_registry`.

#### 12.1.2 Endpoints (REST + OpenAPI)

| Method | Path | Body | Returns | Tests |
|---|---|---|---|---|
| `POST` | `/api/vouchers` | `CreateVoucherRequest` | `VoucherDto` + `Location` header + `X-Correlation-Id` | CRIT-001, SYNC-POS-001 |
| `PUT` | `/api/vouchers/{id}` | `UpdateVoucherRequest` | `VoucherDto` | SYNC-POS-002 |
| `DELETE` | `/api/vouchers/{id}` | `DeleteVoucherRequest { reason }` | `204` | SYNC-POS-003, CRIT-011, NEG-007 |
| `POST` | `/api/vouchers/bulk-delete` | `BulkDeleteRequest { voucherIds[], reason, overrideToken }` | `BulkDeleteResult` | CRIT-013, SEC-008 |
| `GET` | `/api/vouchers/{id}` | — | `VoucherDto` | UI |
| `GET` | `/api/vouchers?status=&from=&to=&page=&size=` | — | paged | UI |
| `POST` | `/api/files/upload` | multipart | `FileUploadDto` | SYNC-POS-010 |

#### 12.1.3 Service flow — `CreateVoucherAsync`

```
1. Validate request (FluentValidation) → 400 with error catalog code on fail
2. Open IModuleDbConnectionFactory.CreateOpenConnectionAsync()
3. BEGIN TRANSACTION (IOrchestrationTransactionFactory)
4. INSERT voucher + voucher_line rows
5. Compute payload_json (canonical), payload_hash
6. Allocate sequence_no via SequenceAllocator (UPDATE sync_sequence + read)
7. INSERT sync_outbox(status='PENDING', change_type='INSERT', priority=10, …)
8. [Fault hook: after_db_commit]   ← see §13.3
9. COMMIT
10. [Fault hook: after_commit]
11. Optional: ping IDistributedCache to invalidate lookups touched
12. Return 201 with VoucherDto
```

The fault hooks fire **only** when `Harness:TestMode = true` and the hook is enabled via TestControl. They are no-ops in non-test environments.

#### 12.1.4 DELETE with before-state capture (closes G-20)

```
1. Open conn, begin tx
2. SELECT voucher + voucher_line FOR UPDATE → before_state object
3. Validate before_state is fully populated → else throw ERP-PACS-VAL-0007
4. INSERT voucher_deletion_audit (with before_state_json)
5. DELETE voucher_line WHERE voucher_id = @id
6. DELETE voucher WHERE voucher_id = @id          -- HARD delete, matches l3_FAS behaviour
7. Allocate sequence_no
8. INSERT sync_outbox(change_type='DELETE', payload_json=null, before_state_json=<from step 2>, …)
9. [Fault hook: after_db_commit]
10. COMMIT
```

NEG-007 (`DELETE without before-state`) is structurally impossible because the API constructs `before_state` before committing.

#### 12.1.5 Internal services

- `IVoucherRepository` (Dapper) — single concrete impl, all SQL parameterised.
- `IVoucherService` — orchestrates the flow above.
- `ISequenceAllocator` — atomically allocates `sequence_no` (§8.3).
- `IOutboxWriter` (thin façade over `IOrchestrationOutbox`).
- `IPayloadCanonicalizer` from `Harness.Common`.
- `[TraceableAction]` on `DeleteVoucher` and `AmendVoucher` (if amend is allowed for vouchers — typically no).
- `[RequireAuth]` policy (gated by `Iam:Enabled` like FAS).

#### 12.1.6 Bulk delete guardrail (CRIT-013, SEC-008)

`POST /api/vouchers/bulk-delete`:
1. If `Count > Governance:BulkDeleteThreshold` (default 10) **and** request lacks `governance.overrideToken` → 409 `ERP-PACS-GOV-0001`.
2. Verify `BackupManifest.LastSuccessfulBackup` (read from `Pacs.SyncWorker` or a local file) is within `Governance:RequireBackupAgeHours` (default 24h).
3. For each voucher: run the single-voucher DELETE flow, **producing one DELETE event per row** (not one bulk event).
4. Return per-row outcome map.

### 12.2 `Pacs.Loans.Api` — Loan application backend with maker-checker

#### 12.2.1 Tables

- `loan_application`, `loan_amendment_history`, `sync_outbox`, `sync_sequence`, `file_sync_registry`.

#### 12.2.2 Endpoints

| Method | Path | Tests |
|---|---|---|
| `POST` | `/api/loan-applications` | CRIT-001 generalized |
| `PUT` | `/api/loan-applications/{id}` | SYNC-POS-002 |
| `POST` | `/api/loan-applications/{id}/submit` (maker submits) | UI-004 |
| `POST` | `/api/loan-applications/{id}/approve` (checker approves) | three-witness audit |
| `POST` | `/api/loan-applications/{id}/reject` | — |
| `POST` | `/api/loan-applications/{id}/amend` `{ reason, approver, fields[] }` | SYNC-POS-004, CRIT-012, NEG-008/009 |
| `DELETE` | `/api/loan-applications/{id}` `{ reason }` | CRIT-011 |
| `GET` | `/api/loan-applications` paged | UI |
| `GET` | `/api/loan-applications/{id}/timeline` | UI (drill-down across outbox + audit + traceability) |

#### 12.2.3 Amend flow (closes G-21)

```
1. Validate request: reason.Length > 0 AND approver != null AND approver != current user
   → else throw ERP-PACS-VAL-0008 (NEG-009) — at the API boundary, before any DB write
2. Open conn, begin tx
3. SELECT loan_application FOR UPDATE → before_state
4. UPDATE loan_application SET <amended fields>, updated_at = NOW(6), status='AMENDED'
5. INSERT loan_amendment_history (before, after, reason, approver, correlation_id)
6. Allocate sequence_no
7. INSERT sync_outbox(change_type='AMENDMENT', payload_json=after, before_state_json=before,
                      amendmentMeta={reason, approver}, priority=20, …)
8. COMMIT
9. [TraceableAction] auto-emits the third witness via Traceability.WithMessagingOutbox()
```

#### 12.2.4 Three-witness validation (closes G-06)

After every amend/delete, automated tests assert:
```sql
-- Witness 1: sync_outbox
SELECT 1 FROM sync_outbox WHERE event_id=@eid AND change_type IN ('AMENDMENT','DELETE');

-- Witness 2: domain audit (loan_amendment_history OR voucher_deletion_audit)
SELECT 1 FROM loan_amendment_history WHERE source_event_id=@eid;
SELECT 1 FROM voucher_deletion_audit  WHERE correlation_id=@corr;

-- Witness 3: traceability (managed by utils-traceability)
SELECT 1 FROM erp_traceability.audit_activity WHERE correlation_id=@corr AND action='Loans.LoanApp.Amend';
```

All three must return `1`. Missing any one fails the test.

#### 12.2.5 Maker-checker enforcement (NEG-009)

- `submit` requires role `maker`.
- `approve`/`reject` requires role `checker` and `checker != maker`.
- `amend` requires role `checker` + `approver` field naming a different user than the current.

When `Iam:Enabled = false` (default for local dev), the role check is a stub that reads `X-Test-User` header and `X-Test-Role` header. This is what lets QA scripts impersonate roles without IAM.

### 12.3 `Pacs.SyncWorker`

#### 12.3.1 Hosted services (all `TraceableBackgroundService` derivatives)

| Service | Cadence | Tests |
|---|---|---|
| `OutboundRelayService` | continuous (poll every `Sync:Outbox:PollIntervalMs` = 500 ms; back off when empty) | SYNC-POS-007, OFF-003 |
| `InboundConsumerService` (Kafka subscriber via `[SubscribeToOutbox]`) | continuous | SYNC-POS-005, SEQ-004..006 |
| `HeartbeatService` | every `Sync:Heartbeat:IntervalSeconds` = 30 s | OFF-004, UI-001 |
| `LockReaperService` | every 30 s | FAIL-008, PWR-002 |
| `ReconciliationService` | daily 02:00 + `/api/test/reconciliation/run` | SEQ-011, BAK-006 |
| `FileChunkUploaderService` | continuous, gated by NLDR availability | SYNC-POS-010, OFF-006 |
| `CircuitBreakerStateLogger` | on transition | FAIL-004 |
| `ClockDriftDetector` | every 60 s (compares NLDR `Date` header vs local) | CRIT-018 |

#### 12.3.2 OutboundRelayService loop

```
loop:
  if circuit is OPEN:
    sleep openDuration
    transition to HALF_OPEN
    continue
  rows = SELECT ... FROM sync_outbox WHERE status='PENDING' 
         ORDER BY priority ASC, sequence_no ASC LIMIT N FOR UPDATE SKIP LOCKED;
  if empty: sleep emptyBackoff; continue
  for each row:
    UPDATE sync_outbox SET status='IN_FLIGHT', sent_at=NOW(6) WHERE outbox_id=row.id;
    COMMIT;
    [Fault hook: after_mark_in_flight]
    try:
      [Fault hook: before_kafka_publish]
      kafkaProducer.PublishAsync(topic, envelope, metadata);
      [Fault hook: after_kafka_publish]
      // Two-transport mode: also POST to NLDR HTTP ingest (helps with TLS/cert tests)
      if Sync:UseHttpTransport: httpClient.PostAsync(nldrIngest, envelope);
      // wait for ACK via separate consumer; relay continues
      // ACK arrival is handled in InboundConsumerService
    catch transient: 
      UPDATE sync_outbox SET status='PENDING', retry_count=retry_count+1, last_error=ex.Code
      apply backoff
    catch permanent (hash mismatch, schema violation): 
      UPDATE sync_outbox SET status='DEADLETTER'
      publish to epacs.deadletter
```

#### 12.3.3 InboundConsumerService ACK handler

```
[SubscribeToOutbox(Topic="epacs.nldr.acks", ConsumerGroup="pacs-ack-consumer", EventType="nldr.ack", SchemaVersion="v1")]
public sealed class AckHandler : IOrchestrationMessageHandler<AckPayload>
{
  public async Task HandleAsync(AckPayload ack, OrchestrationContext ctx, CancellationToken ct)
  {
    // inside ctx.Transaction
    [Fault hook: before_ack_update]
    UPDATE sync_outbox SET status='ACKED', ack_at=NOW(6) 
      WHERE event_id=@eid AND status='IN_FLIGHT';
    
    // Advance checkpoint only if contiguous
    advanceCheckpointIfContiguous(ack.PacsId, ack.SequenceNo);
    [Fault hook: after_ack_update]
  }
}
```

NACK handler: similar, sets `status='FAILED'`, logs reason, applies retry policy.

### 12.4 `Pacs.OperatorUi` (Razor MVC, single thin UI)

#### 12.4.1 Layout

- `_Layout.cshtml` with top bar showing: PACS ID, online/offline banner (red/green), sync queue depth, last sync time.
- Areas:
  - `Areas/Fas/` — Voucher screens (List, Create, Edit, Delete, Bulk-Delete confirmation, Voucher detail with drill-down)
  - `Areas/Loans/` — Loan application screens (List, Create, Amend, Approve, Reject, Detail with timeline)
  - `Areas/Shared/SyncDashboard/` — Outbox status counts, conflict list, retry-all button
  - `Areas/Shared/TestControl/` — only rendered when `Harness:TestMode=true`: buttons "Go Offline", "Go Online", "Kafka Down", "Redis Flush", "Pause-after-DB-commit", "Drop-next-ACK", "Tamper Hash", "Time Jump", "Run Demo …"

#### 12.4.2 Online/offline banner logic (UI-001, OFF-004)

- The UI polls `GET /api/sync/status` every `Ui:Polling:StatusIntervalMs` = 2000 ms.
- Status payload: `{ online: bool, lastAckAt: …, pendingCount: …, inFlightCount: …, failedCount: …, deadletterCount: …, circuitState: …, heartbeatLastOkAt: … }`.
- Banner colour: green when `online=true AND circuitState!=OPEN AND (now - heartbeatLastOkAt) < 2×interval`. Red otherwise.
- The UI also subscribes to SignalR hub `/hubs/sync` for push updates (when implemented; falls back to polling).

#### 12.4.3 Drill-down view (Demo 1 step 5)

`GET /vouchers/{id}/timeline` renders a vertical timeline with one row per artefact, all linked by correlation ID:
1. Business row created (voucher table) — timestamp, correlation
2. Outbox PENDING (sync_outbox) — sequence_no, event_id
3. Kafka publish (topic, partition, offset) — fetched from `/api/sync/kafka-offsets/{eventId}`
4. NLDR received (received_event) — fetched from NLDR via Nldr.Api
5. ACK received (ack_log) — fetched from NLDR
6. Checkpoint advance (sync_checkpoints)

This single screen is the most powerful demo asset — it makes the architecture **visible**.

#### 12.4.4 Conflict UI (CRIT-019)

`/conflicts` page lists open `conflict_log` rows; clicking a row shows side-by-side local vs remote state and three buttons: "Keep Local" / "Take Remote" / "Manual Merge". The manual merge form allows hand-editing the merged state with a reason and approver.

### 12.5 `Nldr.Api` — NLDR ingress + commands

#### 12.5.1 Endpoints

| Method | Path | Tests |
|---|---|---|
| `POST` | `/api/sync/ingest` (envelope as body) | SYNC-POS-001, SEC-001..007 |
| `POST` | `/api/files/{file_id}/chunks/{index}` | SYNC-POS-010 |
| `POST` | `/api/commands` (NLDR creates command for PACS) | SYNC-POS-005 |
| `GET` | `/api/received-events?pacsId=&from=&to=&page=&size=` | dashboard |
| `GET` | `/api/sequence-gaps?pacsId=` | dashboard |
| `GET` | `/api/conflicts` | dashboard |
| `GET` | `/api/heartbeat/{pacsId}` | dashboard |
| `GET` | `/api/reconciliation/{pacsId}` | dashboard |
| `POST` | `/api/test/failure-mode` (TestControl) — §13.2 | every failure test |
| `GET` | `/health/ready` and `/health/live` | infra |

#### 12.5.2 Ingest pipeline (strict)

```
1. Parse envelope. On JSON error → 400 NACK ERP-NLDR-VAL-0001.
2. Validate mTLS / test token. On fail → 401 ERP-NLDR-SEC-0001.
3. Validate envelope schema (required fields). On fail → 400 with specific code.
4. [Fault hook: check NLDR mode] — if mode=http500 → return 500 (FAIL-001); 
   if mode=429 → return 429 with Retry-After (FAIL-002); 
   if mode=timeout → sleep delayMs then return 200 or close conn (FAIL-003); 
   if mode=hashStrict → continue to step 5 strict mode; 
   if mode=dropAck → apply but suppress ACK send.
5. Recompute payload_hash from payload+beforeState; compare to envelope.payloadHash.
   On mismatch → 422 ERP-NLDR-VAL-0002 (SEC-001, CRIT-010). Insert received_event REJECTED.
6. Validate sequence:
   - If sequence_no <= last_acked_for(pacsId): 
       if event_id matches existing received_event row → DUPLICATE (SEQ-004, CRIT-009)
       else → REJECTED ERP-NLDR-SEC-0002 (CRIT-010 variant)
   - If sequence_no == last_acked + 1: APPLIED
   - If sequence_no > last_acked + 1: GAP_WAITING; insert sequence_gap rows for missing seq numbers
7. Validate change-type-specific constraints:
   - DELETE requires beforeState (NEG-007 → ERP-NLDR-VAL-0006)
   - AMENDMENT requires amendmentMeta.reason and .approver (NEG-008/9 → ERP-NLDR-VAL-0007)
8. Apply business state to nldr_business_voucher / nldr_business_loan tables:
   - INSERT: upsert
   - UPDATE: update by entity_id
   - DELETE: set is_deleted=1, deleted_at, deletion_reason — never physical delete (CRIT-011)
   - AMENDMENT: update + insert nldr_amendment_history row
9. INSERT received_event with apply_status
10. INSERT inbox row (utils-orchestration InboxMessages for dedupe)
11. Enqueue ACK in nldr's sync_outbox (separate table; same shape) — published by Nldr.SyncWorker
12. Return 200 with { eventId, status, ackedAt }
```

The entire flow runs **inside one MySQL transaction** so an exception at step 8 rolls back step 9.

### 12.6 `Nldr.SyncWorker`

- `AckPublisherService` — drains NLDR outbox, publishes to `epacs.nldr.acks`.
- `CommandPublisherService` — when an operator creates a command in the dashboard, publishes to `epacs.nldr.commands`.
- `IngestKafkaConsumer` — alternate ingest path via Kafka (`epacs.pacs.outbound`); same pipeline as HTTP `/api/sync/ingest`.
- `HeartbeatConsumer` — consumes `epacs.pacs.heartbeat`, updates `heartbeat` table.
- `FileChunkAssembler` — assembles chunks into a final file, verifies SHA-256, marks `file_received.status='COMPLETED'`.
- `ReconciliationService` — same as PACS side but central perspective.

### 12.7 `Nldr.DashboardUi`

Razor pages:
- **Overview** — live counts: events received today, ACKed, NACKed, gaps, conflicts, files received, heartbeat per PACS.
- **Received Events** — paged list with filters (pacs_id, change_type, status). Click → drill-down.
- **Sequence Gaps** — per pacs_id, with a "Reconcile" button that triggers `Nldr.Api POST /api/reconciliation/{pacsId}`.
- **Conflicts** — list of `conflict_log`.
- **Heartbeats** — last 50 heartbeats per PACS with timeline.
- **Files** — list of files received with size, chunks, hash.
- **Commands** — form to create a new command (policy update, master data, correction) targeted at a specific PACS.
- **Reconciliation** — last 10 reports with PASS/FAIL.
- **Test Control** (only when `Harness:TestMode=true`) — buttons matching §13.2.

### 12.8 `Harness.Common` — shared library (closes G-15)

Public surface (the **only** types other projects depend on):

| Namespace | Type | Purpose |
|---|---|---|
| `Harness.Common.Envelope` | `EventEnvelope`, `EventEnvelopeBuilder` | Build/parse the wire shape (§7.1) |
| `Harness.Common.Canonicalization` | `CanonicalJsonWriter`, `PayloadHasher` | Deterministic JSON + SHA-256 (§7.2) |
| `Harness.Common.Identifiers` | `IdempotencyKey`, `EventIdProvider` | Idempotency key formatter, UUIDv7 |
| `Harness.Common.Sequencing` | `SequenceAllocator` (Dapper-based) | Atomic sequence allocation (§8.3) |
| `Harness.Common.Outbox` | `SyncOutboxWriter` | Thin Dapper insert helper |
| `Harness.Common.Inbox` | `SyncInboxStore` | Insert + dedupe lookup |
| `Harness.Common.Retry` | `RetryPolicyBuilder` | Builds Polly policies from `RetryOptions` |
| `Harness.Common.Health` | `MySqlHealthCheck`, `KafkaHealthCheck`, `RedisHealthCheck`, `DownstreamHealthCheck` | For `/health/ready` |
| `Harness.Common.TestHooks` | `IFaultInjector`, `FaultHook` enum | §13.3 catalog and dispatch |
| `Harness.Common.Redaction` | `[SensitiveAttribute]`, `[DoNotLogAttribute]`, `[MaskAttribute]` | Wired into Observability redaction engine |
| `Harness.Common.Errors` | (re-export of `Intellect.Erp.ErrorHandling.IErrorFactory`) | Convenience |
| `Harness.Common.Time` | `IClock`, `SystemClock`, `OffsetClock` | Closes G-07 |
| `Harness.Common.Reconciliation` | `ReconciliationReport`, `ReconciliationRunner` | §18 |

---

## 13. Fault Injection Contract (closes G-02)

### 13.1 PACS TestControl routes (`Harness:TestMode=true`)

Hosted in `Pacs.Fas.Api` and shared across siblings via Redis-backed flags (`pacs:fault:*`) so worker processes see them.

| Route | Body | Effect | Scope |
|---|---|---|---|
| `POST /api/test/offline` | `{ enabled: bool }` | Sets `pacs:offline-flag` → outbound worker stops attempting publish/HTTP | persistent |
| `POST /api/test/network/block` | `{ target: "nldr-api"\|"kafka"\|"redis", durationSeconds: int? }` | Adds OS firewall rule on the host or sets in-process toggle | time-bounded |
| `POST /api/test/kafka/stop` / `start` | — | Calls docker API (when running in compose) or sets in-process bypass | persistent |
| `POST /api/test/redis/flush` | — | `FLUSHALL` on `pacs-redis` | one-shot |
| `POST /api/test/hooks/{hookId}` | `{ mode: "pause"\|"crash"\|"throw"\|"noop", count: int?, durationMs: int? }` | Arms a fault at a named checkpoint | count-bounded |
| `POST /api/test/clock/jump` | `{ offsetSeconds: int }` | Sets `OffsetClock` offset (drift only; system clock untouched) | persistent |
| `POST /api/test/clock/reset` | — | offset = 0 | one-shot |
| `POST /api/test/tamper/last-outbox` | `{ field: "payload"\|"hash"\|"sequence", newValue: any }` | Direct mutation of a `sync_outbox` row for SEC tests | one-shot |
| `POST /api/test/sequence/skip` | `{ count: int }` | Advances `sync_sequence.next_sequence` by `count` to inject a gap | one-shot (SEQ-002, CRIT-008) |
| `POST /api/test/scenarios/{name}/run` | — | Runs a packaged Demo scenario; returns runId | — (§20) |
| `GET /api/test/state` | — | Returns currently armed hooks/flags | — |

### 13.2 NLDR TestControl routes (closes G-03 — full 8 modes)

Hosted in `Nldr.Api`.

| Route | Body | Mode | Tests |
|---|---|---|---|
| `POST /api/test/failure-mode` | `{ mode: "healthy" }` | reset | — |
| | `{ mode: "http500", count: 3 }` | next 3 ingests return 500 | FAIL-001, CRIT-006 prep |
| | `{ mode: "timeout", delayMs: 5000 }` | accept then delay response | FAIL-003, CRIT-003 |
| | `{ mode: "dropAck", count: 1 }` | apply event but suppress ACK | CRIT-003 |
| | `{ mode: "rateLimit", retryAfterSec: 20 }` | return 429 + Retry-After | FAIL-002 |
| | `{ mode: "badAck", swapEventId: true }` | ACK references wrong event | NEG-017 |
| | `{ mode: "hashStrict" }` | recompute canonical hash strict, reject any mismatch | SEC-001, CRIT-010 |
| | `{ mode: "sequenceStrict" }` | reject gap events instead of GAP_WAITING | SEQ-002 |
| `POST /api/test/cert/reject-next` | `{ count: 1 }` | next handshake returns 401 | FAIL-005, SEC-003 |
| `POST /api/test/db/restart` | — | restart MySQL_NLDR (in compose) | FAIL-008 |
| `POST /api/test/tamper/{eventId}` | `{ field, newValue }` | mutate received_event row directly | SEC-005 |
| `POST /api/test/commands/duplicate/{commandId}` | — | re-publishes the same command | NEG-019 |
| `GET /api/test/state` | — | currently armed modes | — |

### 13.3 Fault hook catalog (closes G-02)

`Harness.Common.TestHooks.FaultHook` enum:

| Hook ID | Fired by | Test that uses it |
|---|---|---|
| `BeforeDbCommit` | Any service inside business tx, just before `COMMIT` | PWR-001 |
| `AfterDbCommit` | Right after `COMMIT` returns | PWR-002, CRIT-006 |
| `BeforeKafkaPublish` | `OutboundRelayService` before `PublishAsync` | PWR-003 |
| `AfterKafkaPublish` | After `PublishAsync` returns | FAIL-009 |
| `BeforeAckUpdate` | `AckHandler` before `UPDATE … status='ACKED'` | PWR-003 (variant) |
| `AfterAckUpdate` | After ACK persisted | — |
| `BeforeInboxApply` | NLDR ingest pipeline before business write | NEG-015 |
| `AfterInboxApply` | After business write commits at NLDR | — |
| `BeforeOutboxFetch` | `OutboundRelayService` SELECT FOR UPDATE | FAIL-008 |
| `AfterMarkInFlight` | After row flipped to IN_FLIGHT | PWR-006 |
| `BeforeFileChunkUpload` | Per chunk | PWR-008 |
| `AfterFileChunkAck` | Per chunk | OFF-006 |
| `BeforeHeartbeatPublish` | Heartbeat producer | OFF-004 |

**Hook modes:** `pause` (block until released or `durationMs`), `crash` (`Environment.Exit(1)`), `throw` (raise specified exception type, exercising retry path), `noop` (record visit, do nothing). Default mode is `noop`.

**Hook precedence:** Multiple hooks can be armed simultaneously. They fire **in declared order** at the hook site. Each hook decrements its `count` on each visit; reaches 0 → auto-disarms.

**Hook visibility:** `GET /api/test/state` lists armed hooks with counter values. `/api/test/hooks/clear` disarms all.

### 13.4 Time-control / clock-drift (closes G-07)

- `Harness.Common.Time.IClock` injected everywhere instead of `DateTime.UtcNow`.
- `OffsetClock` adds a configurable offset.
- `POST /api/test/clock/jump` updates the offset in Redis (`pacs:clock-offset-seconds`) so all processes see it.
- `Sync:ClockDrift:MaxAllowedSeconds` (default 30 s). If detected drift exceeds, `ClockDriftDetector` emits `ERP-PACS-HLT-0010` warning; if exceeds `Sync:ClockDrift:BlockingSeconds` (300 s), sync is paused (CRIT-018).
- For SEQ-011 (30-day offline simulation), we **don't** actually wait 30 days. We:
  1. Generate events with `createdAtUtc` spaced out as if 30 days elapsed.
  2. Use `OffsetClock` to set the local "now" to T0 + 30d before reconnect.
  3. Run drain — all events go through normally.
  
  This converts a 30-day soak into a few minutes while preserving the data shape.

---

## 14. Configuration Philosophy (closes G-17)

### 14.1 Options classes (zero-hardcoding)

```csharp
// Harness.Common/Options/

public sealed class PacsOptions {
    public const string SectionName = "Pacs";
    public string PacsId { get; set; } = string.Empty;            // PACS-AP-0001
    public string Tenant { get; set; } = "ePACS";
    public string DataRoot { get; set; } = string.Empty;          // ${DataRoot} variable
    public IamOptions Iam { get; set; } = new();
    public GovernanceOptions Governance { get; set; } = new();
    public HarnessOptions Harness { get; set; } = new();
}

public sealed class SyncOptions {
    public const string SectionName = "Sync";
    public OutboxOptions Outbox { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public CircuitOptions Circuit { get; set; } = new();
    public HeartbeatOptions Heartbeat { get; set; } = new();
    public FileOptions File { get; set; } = new();
    public PriorityOptions Priority { get; set; } = new();
    public ClockDriftOptions ClockDrift { get; set; } = new();
    public string OutboundTopic { get; set; } = "epacs.pacs.outbound";
    public string AcksTopic { get; set; } = "epacs.nldr.acks";
    public string CommandsTopic { get; set; } = "epacs.nldr.commands";
    public string HeartbeatTopic { get; set; } = "epacs.pacs.heartbeat";
    public string DeadletterTopic { get; set; } = "epacs.deadletter";
    public bool UseHttpTransport { get; set; } = false;           // dual-transport switch
    public string? NldrIngestUrl { get; set; }                    // when UseHttpTransport=true
}

public sealed class HarnessOptions {
    public const string SectionName = "Harness";
    public bool TestMode { get; set; } = false;                   // gates TestControl routes
    public string Profile { get; set; } = "Default";              // "Default","Multi-Pacs","Two-Laptop","Vm-Lab","Installer"
    public bool ScenarioPlayerEnabled { get; set; } = false;      // §20
}

public sealed class GovernanceOptions {
    public int BulkDeleteThreshold { get; set; } = 10;
    public int RequireBackupAgeHours { get; set; } = 24;
    public string? OverrideTokenHashSha256 { get; set; }
}
```

…and likewise for `NldrOptions`, `UiOptions`, `MessagingOptions` (re-uses `Intellect.Erp.Messaging` shape), `CachingOptions` (re-uses `RedisCaching`), `TraceabilityOptions` (re-uses `Intellect.Erp.Traceability`).

### 14.2 Configuration layering

1. `appsettings.json` — compiled defaults shipped with each project.
2. `appsettings.{Environment}.json` — `Development`, `Docker`, `Production`.
3. `appsettings.Profile.{Harness:Profile}.json` — profile overlays for `Multi-Pacs`, `Two-Laptop`, `Vm-Lab`.
4. `.epcfg` (Site Config Pack) when running under the installer — see §14.3.
5. Environment variables — final override (`ASPNETCORE_` and `EPACS_` prefixes).

`AddJsonFile`+`AddEnvironmentVariables` registration is identical across projects via `Harness.Common.HostBuilderExtensions.AddHarnessConfiguration(builder)`.

### 14.3 `.epcfg` integration with installer (closes G-14)

When the harness runs as a payload of the installer, the installer:

1. Generates `appsettings.Production.json` from the `.epcfg` site config pack.
2. Sets `Pacs:PacsId`, `Pacs:Tenant`, `Pacs:DataRoot`, `Messaging:Kafka:BootstrapServers`, MySQL connection strings, Redis connection strings.
3. Sets `Harness:TestMode = false` and `Harness:Profile = "Installer"`.
4. Drops the override at `${DataRoot}/config/appsettings.Production.json`.
5. The installer service map (`packaging/service-map.yaml`) registers all 7 harness processes as native Windows services with start order: `Kafka` → `MySQL` → `Redis` → `Pacs.Fas.Api` → `Pacs.Loans.Api` → `Pacs.SyncWorker` → `Pacs.OperatorUi`.
6. NLDR side is **not** installed at the pilot PACS site — only the dashboard URL is configured. NLDR is hosted centrally.

For pilot **demo** deployments (where one machine plays both PACS and NLDR), the installer's `--demo` flag installs the NLDR-side services too.

---

## 15. Observability, Audit, Traceability

### 15.1 Correlation

- `X-Correlation-Id` HTTP header propagates through:
  - `Observability.AspNetCore.UseCorrelationId` middleware
  - `Observability.Propagation.CorrelationDelegatingHandler` for outgoing HTTP
  - `Observability.Propagation.KafkaHeaders` for outgoing Kafka
  - Each background service inherits `TraceableBackgroundService` which scopes the correlation per consumed message
- If a request arrives without a correlation ID, the middleware generates a ULID. **No business handler ever calls `Guid.NewGuid()` for correlation**.

### 15.2 Structured logging

- `IAppLogger<T>` everywhere.
- Mandatory `BeginOperation(module, feature, operation)` at API entry and worker work-item start.
- Mandatory `Checkpoint(name, data)` at each fault hook + each persistence boundary.
- Forbidden: raw `_logger.Information("Voucher created with amount " + amt)`. Use templated arguments.

### 15.3 Three-witness audit wiring (closes G-06)

```csharp
[TraceableAction(
    action: "Loans.LoanApp.Amend",
    entityType: "LoanApp",
    EntityIdArgument = nameof(AmendLoanCommand.LoanAppId),
    Severity = TraceSeverity.Critical,
    Sensitivity = Sensitivity.CONFIDENTIAL,
    Retention = RetentionClass.REGULATORY_10Y,
    CaptureBeforeAfter = true,
    PublishToKafka = true)]
public async Task<AmendLoanResult> AmendAsync(AmendLoanCommand cmd, CancellationToken ct) { ... }
```

The three witnesses populated automatically:
1. `sync_outbox` row (we write this in the same tx).
2. Domain audit row (`loan_amendment_history`) — we write this in the same tx.
3. `erp_traceability.audit_activity` — written by the `[TraceableAction]` pipeline (asynchronously via outbox; same correlation_id).

`Harness.IntegrationTests.ThreeWitnessTests` automates the assertion suite for every mutation type.

### 15.4 PII redaction (closes G-23 partial)

DTO fields annotated:
```csharp
public sealed class LoanApplicationDto {
    public long LoanAppId { get; init; }
    public string LoanAppNo { get; init; } = string.Empty;
    [Sensitive] public string MemberName { get; init; } = string.Empty;
    [Mask("****")] public string MemberMobile { get; init; } = string.Empty;
    [DoNotLog] public string AadhaarLast4 { get; init; } = string.Empty;
    public decimal RequestedAmount { get; init; }
}
```

Redaction engine in `Observability.Core` masks these in logs and in support bundle output. The `DS-PII` dataset (Test Plan §6.1) populates these fields and SEC-004 / CRIT-020 scan support bundles for un-redacted values.

### 15.5 Support bundle (closes G-23)

`POST /api/support/bundle` (PACS-side):
1. Collects last N days of logs from `{DataRoot}/logs/` with redaction applied at write-time.
2. Dumps `SELECT * FROM sync_outbox ORDER BY outbox_id DESC LIMIT 1000` and other key tables.
3. Captures `docker ps`, container versions, schema versions.
4. Builds `support-bundle-{runId}-{timestampUtc}.zip` with the structure in Test Plan §24.
5. Runs `Harness.Common.Redaction.SupportBundleScanner` over the zip and refuses to emit if any field flagged.

---

## 16. Error Catalog (closes G-24)

`packaging/error-catalog/harness.yaml` (YAML, parsed by `Intellect.Erp.ErrorHandling`):

```yaml
errors:
  ERP-PACS-VAL-0001:  { http: 400, category: VAL, operator: "Required field missing", severity: Warning }
  ERP-PACS-VAL-0007:  { http: 422, category: VAL, operator: "Before-state could not be captured for delete", severity: Error }
  ERP-PACS-VAL-0008:  { http: 422, category: VAL, operator: "Amendment requires reason and approver, and approver must differ from current user", severity: Error }
  ERP-PACS-GOV-0001:  { http: 409, category: GOV, operator: "Bulk delete requires backup and override token", severity: Error }
  ERP-PACS-INS-0001:  { http: 500, category: INS, operator: "Database write failed", severity: Error }
  ERP-PACS-SYN-0001:  { http: 503, category: SYN, operator: "Sync paused — clock drift exceeded blocking threshold", severity: Error }
  ERP-PACS-HLT-0010:  { http: 200, category: HLT, operator: "Clock drift warning", severity: Warning }
  ERP-NLDR-VAL-0001:  { http: 400, category: VAL, operator: "Invalid envelope JSON", severity: Error }
  ERP-NLDR-VAL-0002:  { http: 422, category: VAL, operator: "Payload hash mismatch", severity: Error }
  ERP-NLDR-VAL-0003:  { http: 400, category: VAL, operator: "Missing event id", severity: Error }
  ERP-NLDR-VAL-0004:  { http: 400, category: VAL, operator: "Negative or zero sequence", severity: Error }
  ERP-NLDR-VAL-0005:  { http: 400, category: VAL, operator: "Event timestamp exceeds drift threshold", severity: Error }
  ERP-NLDR-VAL-0006:  { http: 422, category: VAL, operator: "DELETE event missing beforeState", severity: Error }
  ERP-NLDR-VAL-0007:  { http: 422, category: VAL, operator: "AMENDMENT missing reason or approver", severity: Error }
  ERP-NLDR-SEC-0001:  { http: 401, category: SEC, operator: "Authentication failed", severity: Error }
  ERP-NLDR-SEC-0002:  { http: 422, category: SEC, operator: "Replayed event with altered payload", severity: Error }
```

Every thrown exception goes through `IErrorFactory.FromCatalog(code, contextMessage)`.

---

## 17. Health Checks & Heartbeat

### 17.1 Health check set per service (closes G-16)

| Service | `/health/ready` includes | `/health/live` |
|---|---|---|
| `Pacs.Fas.Api` | MySQL_PACS · Redis_PACS · Kafka (down=degraded, not unhealthy — see Engineer Guide §6) | always-200 if process up |
| `Pacs.Loans.Api` | same | same |
| `Pacs.SyncWorker` | MySQL_PACS · Kafka · Redis · NLDR HTTP probe (degraded if down) | same |
| `Nldr.Api` | MySQL_NLDR · Kafka · Redis_NLDR | same |
| `Nldr.SyncWorker` | same | same |
| `Pacs.OperatorUi` | Pacs.Fas.Api · Pacs.Loans.Api (degraded if down) · Redis_PACS | same |
| `Nldr.DashboardUi` | Nldr.Api | same |

Pluggable via `Harness.Common.Health` builders.

### 17.2 Health rendering in UI

Status badge per dependency in the top bar: green/yellow/red dot. Click → modal showing each check + last result.

### 17.3 Heartbeat (closes G-12)

- Producer: `Pacs.SyncWorker.HeartbeatService` every 30 s publishes to `epacs.pacs.heartbeat`:
  ```json
  { "pacsId":"PACS-AP-0001","sentAtUtc":"…","outboxDepth":42,"lastAckedSequence":1042,
    "buildVersion":"…","gitSha":"…","dataRootFreeGb":120, "uptimeSeconds":3600 }
  ```
- Consumer: `Nldr.SyncWorker.HeartbeatConsumer` inserts a row in `heartbeat` table.
- Operator UI banner: shows "Online" if last heartbeat **acknowledged** within `Sync:Heartbeat:OnlineWindowSeconds` (default 90). Heartbeat acknowledgement is via reverse heartbeat from NLDR on `epacs.nldr.acks` topic with `eventType=nldr.heartbeat.ack`.
- OFF-004 test: block network, observe banner flip within 2× interval (≤120 s); unblock, observe flip back.

---

## 18. Reconciliation (closes G-10)

### 18.1 Algorithm

`ReconciliationRunner.RunAsync(pacsId, windowFrom, windowTo)`:
1. **Gap check (local)**: `SELECT sequence_no FROM sync_outbox WHERE pacs_id=? ORDER BY sequence_no`. Compute gaps via `LAG()`. Expect zero.
2. **Local-vs-central completeness**: For every ACKed `sync_outbox` row, verify `received_event` exists at NLDR. Expect 1:1.
3. **Hash integrity**: For every common `event_id`, compare local `payload_hash` to NLDR `payload_hash`. Expect equal.
4. **Duplicate central**: `SELECT entity_id, COUNT(*) FROM nldr_business_voucher GROUP BY entity_id HAVING COUNT(*) > 1` — expect zero.
5. **Orphan central**: Rows at NLDR with no matching outbox row at PACS (would indicate manual tampering).
6. **Checkpoint correctness**: `sync_checkpoints.last_acked_sequence` ≥ max ACKed sequence_no, ≤ max sequence_no.
7. **Manual-tampering detection**: For sampled rows, recompute hash from current NLDR business state; compare to received_event hash. Flag drift (SEC-005).

### 18.2 Output

JSON report saved to `{DataRoot}/reconciliation/RUN-{utcDate}.json`:
```json
{ "pacsId":"…","windowFrom":"…","windowTo":"…","status":"PASS"|"FAIL",
  "checks":[ { "name":"gap-check","status":"PASS","detail":{...} }, … ],
  "summary":{ "expected":1000,"localAck":1000,"centralReceived":1000,"hashMismatches":0,"gaps":[],"orphans":[],"duplicates":[] }}
```

### 18.3 Triggers

- Nightly 02:00.
- On-demand via `POST /api/reconciliation/run`.
- After every CRIT-* automated test (assertion harness validates `status=PASS`).
- After backup/restore (BAK-006).

---

## 19. Conflict Detection & Resolution (closes G-09)

### 19.1 Detection algorithm

A **conflict** is detected when NLDR receives an `UPDATE`/`AMENDMENT` event for an entity whose central state has been **modified locally at NLDR** (or by a different PACS) since the last received state from this PACS.

Implementation:
1. `received_event` ingest: before applying, look up `nldr_business_voucher` (or _loan).
2. Compare `entity_state_version` (a monotonic counter on the central row) to the version implied by `beforeState`.
3. If mismatch → instead of applying, insert `conflict_log` row + ACK with `outcome=CONFLICT` so PACS UI surfaces it.

For CRIT-019 we artificially modify the NLDR central row via `POST /api/test/tamper/{eventId}` between PACS edit and PACS reconnect.

### 19.2 Resolution UI

`/conflicts/{conflict_id}`:
- Side-by-side: local state (from PACS payload), remote state (from NLDR business table).
- Three actions:
  - **Keep Local** → NLDR overwrites central with payload, marks `resolution='LOCAL'`, publishes correction command `epacs.nldr.commands` to PACS (no-op for PACS; just records resolution).
  - **Take Remote** → PACS receives a correction command, applies remote state locally, marks `resolution='REMOTE'`.
  - **Manual Merge** → operator hand-edits a merged state; resolution requires a `reason` and `approver` (full audit trail).

### 19.3 Conflict closure

Once `resolution != NULL`, both PACS and NLDR `conflict_log` rows close. Reconciliation passes when there are zero open conflicts.

---

## 20. Demo Mode — Scenario Player (closes G-13)

A single endpoint and a single UI button per scenario. Each scenario orchestrates fault toggles + business actions + waits.

### 20.1 Endpoint

`POST /api/test/scenarios/{name}/run` → returns `{ runId, scenarioName, startedAt }`. Progress streamed over SignalR hub `/hubs/scenario/{runId}` or polled at `/api/test/scenarios/runs/{runId}`.

### 20.2 Built-in scenarios

| Name | Script (high level) | Mapped tests |
|---|---|---|
| `demo-happy-path` | Create 1 voucher → wait for ACK → render timeline | CRIT-001, SYNC-POS-001 |
| `demo-offline-reconnect` | Go offline → create 10 vouchers → wait 5 s → go online → wait for drain → assert NLDR has 10 + 0 gaps | CRIT-002, OFF-001..003 |
| `demo-ack-lost` | Set NLDR dropAck count=1 → create voucher → wait for retry → set NLDR healthy → wait for ACK → assert 1 NLDR row | CRIT-003 |
| `demo-kafka-down` | Stop Kafka → create 20 vouchers → wait → start Kafka → wait → assert 20 ACKed | CRIT-004 |
| `demo-redis-flush` | Warm cache → FLUSHALL → create voucher → assert correctness | CRIT-005 |
| `demo-power-cut-after-commit` | Arm hook `AfterDbCommit` mode=`crash` count=1 → create voucher → API crashes → restart manually → relay resumes → assert ACK | CRIT-006 |
| `demo-sequence-gap` | Inject seq skip count=1 → create voucher → assert NLDR `sequence_gap` row | CRIT-008 |
| `demo-duplicate-replay` | Capture last event → re-publish twice → assert NLDR has 1 business row + 2 DUPLICATE inbox rows | CRIT-009 |
| `demo-tamper` | Capture last event → mutate amount, keep eventId → re-publish → assert REJECTED | CRIT-010 |
| `demo-delete-with-before-state` | Create+sync voucher → delete → assert NLDR `is_deleted=1` and deletion_audit row | CRIT-011 |
| `demo-amend-with-reason` | Create+sync loan → amend with reason+approver → assert three witnesses | CRIT-012 |
| `demo-bulk-delete-guardrail` | Try bulk delete 11 → expect 409 → take backup → retry with token → expect per-row events | CRIT-013 |
| `demo-conflict-edit` | Edit at NLDR → offline PACS edit → reconnect → assert conflict_log row | CRIT-019 |
| `demo-30-day-offline-compressed` | Apply `OffsetClock` +30d, generate 30 000 events with spread timestamps, reconnect, drain | CRIT-017, SEQ-011 |
| `demo-clock-drift` | Jump clock +10 min, +600 s → assert sync paused; reset → assert resumes | CRIT-018 |
| `demo-support-bundle-pii` | Create PII rows → trigger fault → generate bundle → run scanner → assert clean | CRIT-020, UI-005 |

### 20.3 Why this matters

Stakeholder demos are reliable. QA can re-run any scenario verbatim. The `runId` is captured in the evidence folder. Failures are reproducible by re-running the same scenario with the same seed.

---

## 21. End-to-End Demo Flows (mapped to CRIT-001..020)

Existing v1 sections "Demo 1..6" are kept, but each is now executed by `POST /api/test/scenarios/{name}/run` (§20.2). The narrative below is what the audience sees on screen.

### 21.1 Demo A: Happy path (3 min)

1. Open `PACS Operator UI` (`/`) and `NLDR Dashboard UI` (`/`) side by side.
2. Click "Run Demo: Happy path".
3. PACS UI: row appears, status `Pending Sync` (yellow), then `Synced` (green) within 2 s.
4. NLDR Dashboard: matching `Received Event` row + ACK row + checkpoint advance.
5. Click "Timeline" on the PACS row → see all 6 artefacts linked by correlation ID.

### 21.2 Demo B: Offline + reconnect (4 min)

1. Click "Run Demo: Offline reconnect".
2. PACS UI banner turns red ("Offline — sync queued").
3. 10 vouchers appear with `Pending` status — all created locally, sync queue depth shows 10.
4. NLDR Dashboard shows zero new events (heartbeat stale).
5. After 5 s, banner turns green; queue drains; NLDR Dashboard shows all 10 events in correct sequence.
6. Reconciliation panel auto-runs, shows PASS (0 gaps, 0 duplicates).

### 21.3 Demo C: Power-cut recovery (5 min)

1. Click "Run Demo: Power cut after commit".
2. Hook `AfterDbCommit` armed with `crash` count=1.
3. Create voucher #6 → API process exits.
4. Run `scripts/restart-service.ps1 -Service Pacs.Fas.Api` (or `docker start epacs-pacs-fas`).
5. Operator UI banner shows API back online. Outbox UI shows the orphaned row resume `IN_FLIGHT`.
6. NLDR receives — assert exactly-once.

### 21.4 Demo D: Hard delete with sync (3 min)

(unchanged from v1 Demo 4)

### 21.5 Demo E: Amendment with maker-checker (4 min)

(unchanged from v1 Demo 5, but with explicit three-witness drill-down at end)

### 21.6 Demo F: Installer integration (10 min)

1. Run `installer.exe /quiet /config:demo-site.epcfg /demo` in a fresh VM.
2. Installer logs render in the harness UI (via `Pacs.OperatorUi` reading `${DataRoot}/logs/installer/state-*.json` files).
3. Smoke test runs automatically: scenario `demo-happy-path` from §20.2.
4. Final result screen with green ticks + support bundle path + dashboard URLs.

---

## 22. Test Coverage Map

Every test ID in Test Plan v1.0 maps to (at least) one fault hook, one DB invariant, and one evidence artefact.

| Test class | IDs | Primary tools |
|---|---|---|
| Positive E2E (§8 Test Plan) | SYNC-POS-001..010 | Scenarios A, file upload demo |
| Offline/Reconnect (§9) | OFF-001..008 | `pacs:offline-flag`, firewall rules, scenario B |
| Failure/Retry (§10) | FAIL-001..010 | NLDR TestControl modes, `BeforeKafkaPublish` hook |
| Sequence/Idempotency (§11) | SEQ-001..012, CRIT-008, CRIT-009 | `POST /api/test/sequence/skip`, `POST /api/test/tamper/last-outbox` |
| Power-cut/Backup (§12) | PWR-001..008, BAK-001..006 | hook crash mode + `Stop-VM -TurnOff` |
| Security (§13) | SEC-001..008, CRIT-010, CRIT-020 | NLDR hashStrict, tamper, support bundle scanner |
| UI (§14) | UI-001..006 | Razor screens + Playwright |
| Performance (§15) | PERF-001..006 | Scenario `demo-30-day-offline-compressed`, drain timer |
| Negative matrix (§16) | NEG-001..020 | Contract tests with crafted envelopes |
| Critical cards (§20) | CRIT-001..020 | Scenarios in §20.2 |

The detailed traceability matrix from Test Plan §17 is **already exhaustive** and is treated as the authoritative coverage source. The harness must make every row of that matrix executable; this section names the tooling, not the duplicate matrix.

---

## 23. Build & Run

### 23.1 Local dev (Docker Compose minimal)

`docker compose -f docker/docker-compose.minimal.yml up -d` starts Kafka + 2 MySQL + 2 Redis only. Then each .NET project runs via `dotnet run` from its folder. Useful for fast iteration.

### 23.2 Full harness (Docker Compose)

`docker compose -f docker/docker-compose.yml up -d --build` brings up infra + all 7 .NET projects. Ports:
- 5101 Pacs.Fas.Api
- 5102 Pacs.Loans.Api
- 5201 Nldr.Api
- 5301 Pacs.OperatorUi
- 5401 Nldr.DashboardUi
- 9092 Kafka
- 3307 / 3308 MySQL
- 6380 / 6381 Redis

### 23.3 Hyper-V VM lab

Pre-built VHD with the harness installed via the installer. Used for **hard power-off** tests. `scripts/hard-powercut-vm.ps1`.

### 23.4 Two-laptop lab

`Harness:Profile = "Two-Laptop"` overlay sets the NLDR services to a different host and points everything via a LAN address. Useful for **real network unplug** tests.

### 23.5 Pilot site via installer

See §14.3. The installer **never** packages the NLDR-side projects on the PACS node. The pilot site only runs PACS-side processes; the central NLDR mock lives at the demo-central VM.

---

## 24. Evidence Collection

### 24.1 Folder structure

(Per Test Plan §24, unchanged.) `scripts/collect-evidence.ps1` produces:
```
Evidence/RUN-{utc}-{scenario}/
├── run-sheet.md
├── environment/   (docker ps, versions, health snapshots)
├── screenshots/
├── sql/           (pacs outbox/inbox/checkpoints, nldr received_event/ack_log)
├── kafka/         (topics, offsets, captured envelopes)
├── redis/         (KEYS + selected TTLs)
├── logs/          (correlation-id-grouped log files)
├── reconciliation/ reconciliation-report.json
├── support-bundle/ support-bundle.zip
└── faults/        (fault injection command timestamps)
```

### 24.2 What auto-runs after each scenario

`POST /api/test/scenarios/{name}/run` ends with a call to the evidence collector so a one-button demo produces a complete forensic folder.

### 24.3 Reset lab (closes G-26)

`scripts/reset-lab.ps1` (and `.sh`):
1. `docker compose down -v` (volumes dropped).
2. Wait health, then re-apply migrations.
3. Re-seed `PACS-AP-0001` + (if multi-PACS profile) `PACS-AP-0002`.
4. Re-create Kafka topics with planned partition counts.
5. Confirm `health:ready` is green on all services.

Total time: ≤ 60 s on a developer laptop.

---

## 25. Multi-PACS Profiles (closes G-11)

`Harness:Profile = "Multi-Pacs"`:
- Spins up a second copy of `Pacs.Fas.Api`, `Pacs.Loans.Api`, `Pacs.SyncWorker`, `Pacs.OperatorUi` configured for `PACS-AP-0002`.
- Same MySQL_PACS instance, but separate **schema** `epacs_pacs_0002`.
- Same Kafka cluster; same topics; events distinguished by `pacs_id` key.
- NLDR sees both pacs ids and stores both with proper isolation (UNIQUE keys include `pacs_id`).

This unlocks SEQ-009 (BIGINT range isolation) and SEQ-010 (wrong pacs_id config blocked at startup).

### 25.1 Wrong-PACS startup check (SEQ-010)

At process start, each PACS process validates:
- `Pacs:PacsId` is non-empty and matches `^PACS-[A-Z]{2}-\d{4}$`.
- The DB connection's `epacs_pacs.sync_sequence` has a row for this `pacs_id`. If a foreign `pacs_id` row exists with mismatching entity_id ranges, **fail startup** with `ERP-PACS-INS-0005`.

---

## 26. Security & Tamper Posture

- mTLS between `Pacs.SyncWorker` and `Nldr.Api` HTTP transport (when `Sync:UseHttpTransport=true`). Self-signed certs in dev; site-issued in pilot.
- Kafka SASL/SSL when `Messaging:Kafka:SecurityProtocol = "SaslSsl"` — same registration as `l3_FAS`.
- Outbox & inbox payloads stored as-is (encryption at rest is the DB's job).
- Manifest signing for `.epcfg` and release payloads — out of scope for the harness; defer to installer's existing mechanism.
- `Authorization` policy: `[RequireAuth]` honoured only when `Iam:Enabled=true`. In test mode, `X-Test-User` and `X-Test-Role` headers carry identity.

---

## 27. PII Redaction

Covered in §15.4. DS-PII dataset and the support bundle scanner provide the closing assertion.

---

## 28. Performance Targets & Budgets (closes G-28)

| Test | Target | Hard fail |
|---|---|---|
| PERF-001 (10K drain) | ≥ 500 events/min sustained on dev laptop; ≥ 1500/min on Hyper-V test VM (4 vCPU, 8 GB) | < 200/min |
| PERF-002 (30-day outbox) | DB size < 8 GB, p99 INSERT latency < 50 ms | DB > 12 GB or latency > 200 ms |
| PERF-003 (throttled drain) | sync speed scales linearly with bandwidth ≥ 80 % | < 50 % efficiency |
| PERF-004 (warm vs cold cache) | warm lookup p95 < 5 ms, cold p95 < 50 ms | warm > 20 ms |
| PERF-005 (Kafka lag) | lag returns to 0 within 5 min after backlog | > 15 min |
| PERF-006 (8-hour soak with flapping) | no memory leak (heap growth < 50 MB), no stuck IN_FLIGHT | — |

All targets read from `Performance:Targets:*` config and asserted by `Harness.LongOfflineTests`.

---

## 29. Build Phases & Milestones (closes G-27)

| Phase | Deliverable | Acceptance |
|---|---|---|
| **M0** — Skeleton | Solution + 7 projects compile; `Harness.Common` API stubs; appsettings & options classes; harness.yaml; CI build | `dotnet build` clean; `dotnet test` minimal smoke green |
| **M1** — Happy path | `Pacs.Fas.Api.CreateVoucher` + `Pacs.SyncWorker.OutboundRelayService` + `Nldr.Api.Ingest` end-to-end; `IOrchestrationOutbox` + `sync_outbox` write in same tx; canonicalization + hashing; Razor list + create screens | Scenario `demo-happy-path` passes; CRIT-001 / SYNC-POS-001 evidence folder complete |
| **M2** — Sync invariants | InboundConsumer + ACK flow + checkpoint advance + LockReaper; idempotent receiver via `IInboxStore`; sequence allocator | CRIT-003, CRIT-009 pass; SEQ-001..007 pass |
| **M3** — Offline + reconnect | Heartbeat + Online/Offline banner + circuit breaker + reconnect drain prioritization | Scenario `demo-offline-reconnect` passes; OFF-001..005 pass |
| **M4** — Power-cut robustness | All fault hooks; LockReaper resumes IN_FLIGHT; restart resumes outbox | Scenarios `demo-power-cut-*` pass; PWR-001..006 pass |
| **M5** — Delete + Amendment | Pacs.Loans.Api full; voucher_deletion_audit + loan_amendment_history; `[TraceableAction]` wiring; three-witness assertions | CRIT-011 / CRIT-012 / CRIT-013 pass |
| **M6** — Security + tamper | NLDR strict modes; payload hash strict; tamper scenarios; PII redaction; support bundle | SEC-001..008 + CRIT-010 / CRIT-020 pass |
| **M7** — File sync | Chunked uploader, registry, NLDR assembler, priority | SYNC-POS-010, OFF-006, PWR-008, NEG-020 pass |
| **M8** — Long offline + drift | OffsetClock; 30-day compressed scenario; drift detector | CRIT-017, CRIT-018, SEQ-011 pass; PERF-001..002 within budget |
| **M9** — Multi-PACS | Multi-Pacs profile + isolated schemas + wrong-pacs startup check | SEQ-009, SEQ-010 pass |
| **M10** — Conflict UI | conflict_log + Razor screens + resolution flow | CRIT-019 pass |
| **M11** — Reconciliation + Backup hooks | Reconciliation report + BAK-* hooks (installer manages the actual backup tool) | BAK-001..006 pass |
| **M12** — Installer integration | service-map.yaml, `.epcfg` override, demo profile, smoke contract | Scenario `demo-installer` passes end-to-end on a clean VM |
| **M13** — Polish | Playwright UI tests; performance soak; SonarCloud / code-coverage gates; Authenticode-signed binaries | Exit criteria (Test Plan §19) met |

Each milestone is independently demoable.

---

## 30. Open Questions & Decisions Required

Items the architecture team must resolve before code starts:

1. **Q-01** Is `OrchestrationOutboxRelayHostedService` replacement (G-01 decision) approved, or do we extend `OutboxMessages` instead?
2. **Q-02** Are we shipping **two** transports (Kafka + HTTP `POST /api/sync/ingest`) or **only Kafka**? Test Plan §5.5 lists both; current design supports both behind `Sync:UseHttpTransport`. Decision affects mTLS scope.
3. **Q-03** Does NLDR mock need to support **multiple central tenants**, or single tenant?
4. **Q-04** What is the **canonical UUID format** for `eventId` — UUIDv4 or UUIDv7? UUIDv7 buys us natural ordering for debugging.
5. **Q-05** Does the Razor UI use **MVC** (matches `l3_ERPClient`) or **Razor Pages** (simpler for single-purpose screens)? Recommend MVC for parity.
6. **Q-06** **SignalR vs polling** for the live UI updates? SignalR is richer; polling is sufficient and simpler. Recommend polling for v1, SignalR optional for v1.5.
7. **Q-07** Where do **integration tests** run — host or container? Testcontainers in CI is the standard; do we have CI runners with Docker available?
8. **Q-08** Are we using **DbUp** for migrations (matches `Intellect.Erp.Traceability`) or **Flyway-style** SQL only? Recommend DbUp for harness because the installer ships DbUp.
9. **Q-09** Authorization in test mode: are `X-Test-User`/`X-Test-Role` acceptable, or do we plug in a stub Keycloak even for dev?
10. **Q-10** **Support bundle scanner regex catalog** — who owns the regex list? Engineer Guide §11 lists Aadhaar, mobile, account; we'll need a maintained YAML.

---

## 31. Review Gate (replaces v1 §10)

Before code starts on M0:

- [ ] **Architecture review** — §3 topology, §6.3 schema decision, §8 state machine, §13 hook catalog signed off by Architect.
- [ ] **NuGet integration** — §5 mapping reviewed by each NuGet's maintainer (`utils-messaging`, `utils-orchestration`, `utils-traceability`, `utils-LAndE`, `utils-caching`) and any required API additions logged as cross-team tickets.
- [ ] **Test coverage** — §22 confirmed by QA Lead as covering 100 % of Test Plan v1.0 traceability matrix.
- [ ] **Config philosophy** — §14 reviewed against AGENTS.md zero-hardcoding rule.
- [ ] **Installer integration** — §14.3, §23.5 reviewed by installer team for service-map and `.epcfg` compatibility.
- [ ] **Security** — §26 reviewed by Security Lead; mTLS scope confirmed.
- [ ] **Performance budgets** — §28 reviewed by Performance Lead and Architecture; numbers committed.
- [ ] **Open questions** — every Q-* in §30 has a decision recorded here.

Once all checkboxes are green, M0 begins.

---

## 32. Glossary

| Term | Definition |
|---|---|
| **PACS** | Primary Agricultural Credit Society — the field branch |
| **NLDR** | National-Level Data Repository (the central receiver in production) |
| **Outbox** | Local table holding events not yet sent to NLDR |
| **Inbox** | Local table holding events received and (to-be) applied from NLDR |
| **Sequence number** | Monotonic counter per (pacs_id, stream) — proves no events lost |
| **Event ID** | Globally unique ID for an event instance — duplicate replay safety |
| **Idempotency key** | Logical operation identifier — retry safety even with new event_id |
| **Payload hash** | SHA-256 over canonical JSON of payload + beforeState — tamper detection |
| **Checkpoint** | Last-acked sequence per stream — resumption point |
| **Three witnesses** | sync_outbox + domain audit row + traceability row for mutations |
| **Hook** | A named instrumentation point that can pause/crash/throw on demand |
| **Scenario Player** | One-button demo orchestrator (§20) |
| **`.epcfg`** | Signed Site Config Pack produced out-of-band, consumed by installer |
| **Three-laptop test** | Multi-PACS profile (PACS-AP-0001 + PACS-AP-0002 + NLDR) on three machines |

---

## 33. Appendix A — Concrete SQL DDL

See §6.1 and §6.2 — these are the single source of truth for `db/mysql/pacs/V001..V004` and `db/mysql/nldr/V001..V007`.

## 34. Appendix B — Sample envelope JSON

`samples/envelope.sample.json` — provided as physical asset on M0; canonical example referenced by Contract Tests.

## 35. Appendix C — Sample `appsettings.json` (Pacs.Fas.Api Development)

```json
{
  "ConnectionStrings": {
    "PacsDb": "Server=localhost;Port=3307;Database=epacs_pacs;User=root;Password=root;SslMode=None;AllowUserVariables=true"
  },
  "Pacs": {
    "PacsId": "PACS-AP-0001",
    "Tenant": "ePACS",
    "DataRoot": "${LOCALAPPDATA}/ePACS-Harness",
    "Iam": { "Enabled": false },
    "Governance": { "BulkDeleteThreshold": 10, "RequireBackupAgeHours": 24 }
  },
  "Harness": {
    "TestMode": true,
    "Profile": "Default",
    "ScenarioPlayerEnabled": true
  },
  "Sync": {
    "Outbox": { "PollIntervalMs": 500, "BatchSize": 50, "ProcessingLockTimeoutSeconds": 120, "OutboxRetentionDays": 90 },
    "Retry":  { "MaxAttempts": 7, "BaseDelayMs": 2000, "MaxDelayMs": 60000, "JitterFactor": 0.2, "RespectRetryAfter": true },
    "Circuit":{ "FailureThreshold": 5, "OpenDurationSeconds": 60, "HalfOpenProbeCount": 1 },
    "Heartbeat":{ "IntervalSeconds": 30, "OnlineWindowSeconds": 90 },
    "File":   { "ChunkSizeBytes": 262144, "MaxConcurrentChunks": 4, "MaxFileSizeMb": 50, "SmallFileThresholdKb": 1024 },
    "Priority":{ "VoucherDefault": 10, "LoanAmendment": 20, "LoanDefault": 30, "FileSmall": 50, "FileLarge": 80, "Heartbeat": 200 },
    "ClockDrift":{ "MaxAllowedSeconds": 30, "BlockingSeconds": 300 },
    "OutboundTopic": "epacs.pacs.outbound",
    "AcksTopic": "epacs.nldr.acks",
    "CommandsTopic": "epacs.nldr.commands",
    "HeartbeatTopic": "epacs.pacs.heartbeat",
    "DeadletterTopic": "epacs.deadletter",
    "UseHttpTransport": false,
    "NldrIngestUrl": "http://localhost:5201/api/sync/ingest"
  },
  "Messaging": {
    "Enabled": true,
    "Transport": "Kafka",
    "EnvironmentName": "dev",
    "DefaultStateCode": "AP",
    "ModuleName": "pacs",
    "Kafka": {
      "BootstrapServers": "localhost:9092",
      "SecurityProtocol": "Plaintext"
    }
  },
  "Orchestration": {
    "Enabled": true,
    "ModuleName": "Pacs",
    "Kafka": { "BootstrapServers": "localhost:9092", "SecurityProtocol": "Plaintext", "AutoOffsetReset": "Earliest" },
    "Tables": { "OutboxTable": "OutboxMessages", "InboxTable": "InboxMessages", "SagaInstancesTable": "SagaInstances" },
    "Dispatcher": { "StartHostedConsumers": true },
    "OutboxRelay": { "Enabled": false, "BatchSize": 100, "IntervalSeconds": 5, "QuarantineAfterAttempts": 10 }
  },
  "Caching": {
    "Enabled": true,
    "StateCode": "AP",
    "Namespace": { "ModuleName": "pacs" },
    "Redis": { "ConnectionString": "localhost:6380,abortConnect=false", "ConnectTimeoutMs": 5000, "OperationTimeoutMs": 1000 },
    "Serializer": "SystemTextJson"
  },
  "Traceability": {
    "Enabled": true,
    "DbConnectionString": "Server=localhost;Port=3307;Database=erp_traceability;User=root;Password=root;",
    "Publishing": { "Mode": "MessagingOutbox" }
  },
  "Observability": {
    "Service": { "Name": "Pacs.Fas.Api", "Version": "1.0.0" },
    "Redaction": { "EnableAttributeBased": true },
    "CorrelationHeaderName": "X-Correlation-Id"
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" }
  }
}
```

## 36. Appendix D — Sample `docker-compose.yml` skeleton

```yaml
version: "3.9"
services:
  kafka:
    image: bitnami/kafka:3.7
    ports: ["9092:9092"]
    environment:
      KAFKA_CFG_PROCESS_ROLES: "broker,controller"
      KAFKA_CFG_NODE_ID: 1
      KAFKA_CFG_CONTROLLER_QUORUM_VOTERS: "1@kafka:9093"
      KAFKA_CFG_LISTENERS: "PLAINTEXT://:9092,CONTROLLER://:9093"
      KAFKA_CFG_ADVERTISED_LISTENERS: "PLAINTEXT://localhost:9092"
      KAFKA_CFG_CONTROLLER_LISTENER_NAMES: "CONTROLLER"
      KAFKA_CFG_INTER_BROKER_LISTENER_NAME: "PLAINTEXT"
      ALLOW_PLAINTEXT_LISTENER: "yes"

  pacs-mysql:
    image: mysql:8.4
    ports: ["3307:3306"]
    environment: { MYSQL_ROOT_PASSWORD: root, MYSQL_DATABASE: epacs_pacs }
    volumes: [ "pacs-mysql:/var/lib/mysql" ]

  nldr-mysql:
    image: mysql:8.4
    ports: ["3308:3306"]
    environment: { MYSQL_ROOT_PASSWORD: root, MYSQL_DATABASE: epacs_nldr }
    volumes: [ "nldr-mysql:/var/lib/mysql" ]

  pacs-redis: { image: "redis:7.2", ports: ["6380:6379"] }
  nldr-redis: { image: "redis:7.2", ports: ["6381:6379"] }

  pacs-fas-api:
    build: { context: .., dockerfile: src/Pacs.Fas.Api/Dockerfile }
    ports: ["5101:8080"]
    environment:
      ASPNETCORE_ENVIRONMENT: Docker
      Harness__TestMode: "true"
    depends_on: [ pacs-mysql, pacs-redis, kafka ]

  pacs-loans-api:
    build: { context: .., dockerfile: src/Pacs.Loans.Api/Dockerfile }
    ports: ["5102:8080"]
    depends_on: [ pacs-mysql, pacs-redis, kafka ]

  pacs-sync:
    build: { context: .., dockerfile: src/Pacs.SyncWorker/Dockerfile }
    depends_on: [ pacs-mysql, pacs-redis, kafka, nldr-api ]

  pacs-operator-ui:
    build: { context: .., dockerfile: src/Pacs.OperatorUi/Dockerfile }
    ports: ["5301:8080"]
    depends_on: [ pacs-fas-api, pacs-loans-api ]

  nldr-api:
    build: { context: .., dockerfile: src/Nldr.Api/Dockerfile }
    ports: ["5201:8080"]
    depends_on: [ nldr-mysql, nldr-redis, kafka ]

  nldr-sync:
    build: { context: .., dockerfile: src/Nldr.SyncWorker/Dockerfile }
    depends_on: [ nldr-mysql, nldr-redis, kafka ]

  nldr-dashboard-ui:
    build: { context: .., dockerfile: src/Nldr.DashboardUi/Dockerfile }
    ports: ["5401:8080"]
    depends_on: [ nldr-api ]

volumes:
  pacs-mysql: {}
  nldr-mysql: {}
```

---

## 37. Final Reviewer Checklist

Before approving this design for build:

- [ ] The three backends + one Razor UI + one NLDR dashboard arrangement matches the user's intent (§3).
- [ ] All 5 internal NuGets are consumed in the way each NuGet expects (§5).
- [ ] Schema reconciliation between Test Plan and `utils-orchestration` is documented and not magic (§6.3).
- [ ] Sequence allocation is atomic with the business write (§8.3).
- [ ] Hash canonicalization is deterministic and version-stable (§7.2).
- [ ] Every test class in Test Plan v1.0 has at least one tool that exercises it (§22).
- [ ] Zero-hardcoding rule is structurally enforced via `*Options.cs` classes (§14.1).
- [ ] Installer integration path is concrete, not aspirational (§14.3, §23.5).
- [ ] Open questions in §30 have decisions or owners.
- [ ] Phases in §29 are sized so each can be demo-ed in a fortnight.

---

*End of design overview v2.0. This is the single design document for the harness. Code-level details live next to the code in each project's `README.md` once scaffolded under §29 M0.*
