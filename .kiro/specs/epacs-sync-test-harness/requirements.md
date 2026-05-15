# Requirements Document — ePACS Sync Test Harness

## Introduction

The ePACS Sync Test Harness is a complete `.NET 8` simulation environment (`ePACS.SyncHarness.sln`) that proves the offline-first ePACS synchronisation architecture end-to-end. It consists of three backend APIs (FAS voucher, Loans, and NLDR central receiver), two sync workers, two Razor MVC UIs, a shared common library, four test projects, full Docker Compose infrastructure, database migration scripts, and operational scripts.

The harness exercises all 100+ test cases from `ePACS_Sync_Test_Cases_and_Simulation_Plan_v1.0` (SYNC-POS-001..010, OFF-001..006, PWR-001..008, SEQ-001..012, FAIL-001..010, SEC-001..008, CRIT-001..020, NEG-001..020, PERF-001..006, UI-001..005), validates the five non-negotiable invariants (I-1 through I-5), and integrates with the offline installer for pilot-site demos.

The harness is **not** the production ePACS ERP codebase. It is a deliberately minimal but architecturally faithful simulation. Unit tests for individual components are assumed to exist separately; this harness proves architecture-level invariants.


## Glossary

- **PACS**: Primary Agricultural Credit Society — the field branch node running the harness locally.
- **NLDR**: National-Level Data Repository — the central receiver that aggregates events from all PACS nodes.
- **Outbox**: The `sync_outbox` table holding events not yet acknowledged by NLDR. Authoritative per G-01.
- **Inbox**: The `sync_inbox` table holding events received from NLDR and applied locally.
- **Sync_Outbox**: The harness-owned authoritative outbox table (not `OutboxMessages`). See G-01.
- **OutboxMessages**: The `utils-orchestration` NuGet table; provided as a compatibility view (`V003__orchestration_compat.sql`).
- **EventEnvelope**: The wire JSON shape carrying a single business event from PACS to NLDR (§7.1 of design).
- **CanonicalJsonWriter**: The single implementation of deterministic JSON serialisation (lexicographic key sort, no whitespace, SHA-256). Lives in `Harness.Common`.
- **PayloadHasher**: Computes `sha256_hex(canonical_json({ payload, beforeState, amendmentMeta }))`. Lives in `Harness.Common`.
- **IdempotencyKey**: Formatted as `{pacsId}:{entityType}:{entityId}:{changeType}:{businessTimestampISO8601}`.
- **SequenceAllocator**: Atomically allocates the next `sequence_no` inside the same Dapper transaction as the business write.
- **SyncOutboxWriter**: Thin Dapper helper that inserts a row into `sync_outbox`.
- **SyncInboxStore**: Inserts and deduplicates rows in `sync_inbox`.
- **RetryPolicyBuilder**: Builds Polly retry + circuit-breaker policies from `RetryOptions` / `CircuitOptions`.
- **TraceableBackgroundService**: Base class from `Intellect.Erp.Observability.Propagation` used by all worker hosted services.
- **TraceableAction**: Attribute from `Intellect.Erp.Traceability` that auto-emits the third audit witness.
- **Three-Witness Audit**: After every amend or delete, three rows must exist: `sync_outbox` row, domain audit row (`loan_amendment_history` or `voucher_deletion_audit`), and `erp_traceability.audit_activity` row.
- **FaultHook**: A named instrumentation point in `Harness.Common.TestHooks` that can pause, crash, throw, or no-op on demand.
- **TestControl**: In-process route group (`/api/test/*`) gated by `Harness:TestMode=true`; returns 404 in production.
- **ScenarioPlayer**: One-button demo orchestrator that runs reproducible named scenarios.
- **OffsetClock**: Implementation of `IClock` that adds a configurable offset to the system clock for time-travel tests.
- **Pacs.Fas.Api**: The FAS voucher backend (port 5101).
- **Pacs.Loans.Api**: The Loans backend with maker-checker workflow (port 5102).
- **Pacs.SyncWorker**: The outbox drain + inbox consume worker for the PACS side.
- **Pacs.OperatorUi**: The Razor MVC field-operator UI (port 5301).
- **Nldr.Api**: The strict central receiver API (port 5201).
- **Nldr.SyncWorker**: The ACK/command publisher worker for the NLDR side.
- **Nldr.DashboardUi**: The Razor MVC central observability dashboard (port 5401).
- **Harness.Common**: Shared contracts library referenced by all other projects.
- **Harness.ScenarioPlayer**: Demo Mode orchestrator library hosted inside both UIs.
- **IAppLogger**: `Intellect.Erp.Observability.Abstractions` structured logger interface (not raw `ILogger<T>`).
- **IErrorFactory**: `Intellect.Erp.ErrorHandling` typed exception factory; all errors go through the YAML catalog.
- **ICacheProvider**: `utils-caching` abstraction over Redis; fail-open (`AbortOnConnectFail=false`).
- **Sensitive / DoNotLog / Mask**: PII redaction attributes from `Harness.Common.Redaction`.
- **mTLS**: Mutual TLS between `Pacs.SyncWorker` and `Nldr.Api` HTTP transport when `Sync:UseHttpTransport=true`.
- **Governance Token**: SHA-256-hashed override token required for bulk-delete operations above the threshold.
- **Reconciliation Report**: JSON report comparing PACS outbox state to NLDR received-event state.
- **Conflict Log**: Table recording divergence between PACS payload `beforeState` and current NLDR central state.
- **Evidence Folder**: `Evidence/RUN-{utc}-{scenario}/` produced by `collect-evidence.ps1` after each scenario run.
- **I-1**: Invariant — Local MySQL is source of truth.
- **I-2**: Invariant — Business row and `sync_outbox` row commit or roll back together.
- **I-3**: Invariant — Same `event_id`/`idempotency_key` produces exactly one business effect.
- **I-4**: Invariant — Sequence numbers are monotonically increasing and contiguous per `(pacs_id, stream_name)`.
- **I-5**: Invariant — Payload hash mismatch is rejected; envelope is tamper-evident.

## Requirements

---

### Requirement 1: Solution Structure and Project Scaffold

**User Story:** As a Release Engineer, I want a single `.NET 8` solution (`ePACS.SyncHarness.sln`) under a `harness/` subdirectory containing all source, test, database, Docker, and script artefacts, so that the harness can be built, run, and packaged as a single coherent unit.

#### Acceptance Criteria

1. THE `ePACS.SyncHarness.sln` SHALL reference exactly nine source projects: `Harness.Common`, `Pacs.Fas.Api`, `Pacs.Loans.Api`, `Pacs.SyncWorker`, `Pacs.OperatorUi`, `Nldr.Api`, `Nldr.SyncWorker`, `Nldr.DashboardUi`, and `Harness.ScenarioPlayer`.
2. THE `ePACS.SyncHarness.sln` SHALL reference exactly four test projects: `Harness.ContractTests`, `Harness.IntegrationTests`, `Harness.ChaosTests`, and `Harness.LongOfflineTests`.
3. THE Solution SHALL target `net8.0` with `<LangVersion>12</LangVersion>`, `<Nullable>enable</Nullable>`, and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props`.
4. WHEN `dotnet build ePACS.SyncHarness.sln` is executed, THE Solution SHALL compile with zero errors and zero warnings.
5. THE Solution SHALL contain a `Directory.Packages.props` file that pins every NuGet package to an exact version.
6. THE Solution SHALL contain a `NuGet.Config` that resolves all packages from the offline feed bundled with the installer payload.
7. THE `harness/` directory SHALL contain `db/`, `docker/`, `packaging/`, `scripts/`, `samples/`, and `docs/` subdirectories as specified in the repository layout (§4.1 of the design).
8. THE Solution SHALL contain a `packaging/error-catalog/harness.yaml` file defining all `ERP-PACS-*` and `ERP-NLDR-*` error codes consumed by `IErrorFactory`.
9. WHEN `dotnet test ePACS.SyncHarness.sln` is executed with no infrastructure available, THE `Harness.ContractTests` project SHALL pass without requiring Docker or network access.

*Traces to: G-17, G-24; Tests: M0 milestone acceptance.*

---

### Requirement 2: Harness.Common — Shared Contracts Library

**User Story:** As a Backend Developer, I want a single shared library (`Harness.Common`) that provides all cross-cutting contracts — envelope, canonicalization, hashing, idempotency key, sequence allocation, outbox/inbox helpers, retry policy builder, health checks, fault hooks, redaction attributes, and clock abstraction — so that no other project re-implements these primitives.

#### Acceptance Criteria

1. THE `Harness.Common` Library SHALL expose `EventEnvelope` and `EventEnvelopeBuilder` in the `Harness.Common.Envelope` namespace matching the wire shape defined in §7.1 of the design.
2. THE `Harness.Common.Canonicalization.CanonicalJsonWriter` SHALL serialise any object to JSON with lexicographically sorted keys at every depth, no insignificant whitespace, numbers in `R` round-trip format, booleans lowercase, and nulls as `null`.
3. FOR ALL valid objects `x`, THE `CanonicalJsonWriter` SHALL produce identical output regardless of the order in which properties were added to the source object (determinism property).
4. THE `Harness.Common.Canonicalization.PayloadHasher` SHALL compute `sha256_hex(canonical_json({ "payload": payload, "beforeState": beforeState, "amendmentMeta": amendmentMeta }))` and SHALL be the only location in the solution where `SHA256.HashData` is called.
5. FOR ALL valid envelopes `e`, THE `PayloadHasher` SHALL produce the same hash when called twice on the same inputs (idempotence property).
6. THE `Harness.Common.Identifiers.IdempotencyKey` formatter SHALL produce keys in the format `{pacsId}:{entityType}:{entityId}:{changeType}:{businessTimestampISO8601}`.
7. FOR ALL valid input tuples `(pacsId, entityType, entityId, changeType, timestamp)`, THE `IdempotencyKey` formatter SHALL produce a key that can be parsed back to the original components (round-trip property).
8. THE `Harness.Common.Sequencing.SequenceAllocator` SHALL allocate the next sequence number by executing `UPDATE sync_sequence SET next_sequence = next_sequence + 1 … WHERE pacs_id = @pacs AND stream_name = @stream` followed by a read of `next_sequence - 1`, both inside the caller-supplied Dapper `DbTransaction`.
9. THE `Harness.Common.Outbox.SyncOutboxWriter` SHALL insert a row into `sync_outbox` using the caller-supplied `DbTransaction` so the outbox write and the business write share the same transaction.
10. THE `Harness.Common.Inbox.SyncInboxStore` SHALL insert a row into `sync_inbox` and return `DUPLICATE` when a row with the same `event_id` already exists.
11. THE `Harness.Common.Retry.RetryPolicyBuilder` SHALL build Polly retry and circuit-breaker policies from `RetryOptions` and `CircuitOptions` configuration objects without any hardcoded numeric values.
12. THE `Harness.Common.Health` namespace SHALL provide `MySqlHealthCheck`, `KafkaHealthCheck`, `RedisHealthCheck`, and `DownstreamHealthCheck` implementations compatible with `Microsoft.Extensions.Diagnostics.HealthChecks`.
13. THE `Harness.Common.TestHooks.IFaultInjector` interface SHALL define `FireAsync(FaultHook hook, CancellationToken ct)` and the `FaultHook` enum SHALL include all thirteen hook IDs listed in §13.3 of the design.
14. THE `Harness.Common.Redaction` namespace SHALL provide `[SensitiveAttribute]`, `[DoNotLogAttribute]`, and `[MaskAttribute]` compatible with the `Intellect.Erp.Observability.Core` redaction engine.
15. THE `Harness.Common.Time.IClock` interface SHALL provide `UtcNow` and THE `OffsetClock` implementation SHALL add a configurable `TimeSpan` offset to the system clock.
16. THE `Harness.Common` Library SHALL NOT depend on any other project in the solution; it SHALL only depend on `Intellect.Erp.*` NuGet packages and standard .NET 8 libraries.

*Traces to: G-05, G-07, G-15; Tests: Harness.ContractTests.*

---

### Requirement 3: Pacs.Fas.Api — FAS Voucher Backend

**User Story:** As a Field Operator, I want to create, update, and delete vouchers through a REST API that atomically writes both the business row and a sync outbox row in the same database transaction, so that no voucher change is ever lost even if the sync worker crashes immediately after the commit.

#### Acceptance Criteria

1. THE `Pacs.Fas.Api` SHALL expose the following endpoints: `POST /api/vouchers`, `PUT /api/vouchers/{id}`, `DELETE /api/vouchers/{id}`, `POST /api/vouchers/bulk-delete`, `GET /api/vouchers/{id}`, `GET /api/vouchers` (paged), and `POST /api/files/upload`.
2. WHEN a `POST /api/vouchers` request is received with a valid body, THE `Pacs.Fas.Api` SHALL insert a `voucher` row, one or more `voucher_line` rows, allocate a `sequence_no` via `SequenceAllocator`, insert a `sync_outbox` row with `change_type='INSERT'` and `priority=10`, and commit all writes in a single MySQL transaction.
3. IF the database transaction in Acceptance Criterion 2 rolls back for any reason, THEN THE `Pacs.Fas.Api` SHALL ensure no `sync_outbox` row exists for that operation (atomicity invariant I-2).
4. WHEN a `DELETE /api/vouchers/{id}` request is received, THE `Pacs.Fas.Api` SHALL SELECT the voucher and all voucher lines `FOR UPDATE` to capture `before_state_json`, insert a `voucher_deletion_audit` row, hard-delete the `voucher_line` rows, hard-delete the `voucher` row, and insert a `sync_outbox` row with `change_type='DELETE'` and `before_state_json` populated — all in a single transaction.
5. IF a `DELETE /api/vouchers/{id}` request is received for a voucher that does not exist, THEN THE `Pacs.Fas.Api` SHALL return `404` with error code `ERP-PACS-VAL-0007`.
6. WHEN a `POST /api/vouchers/bulk-delete` request is received with a count exceeding `Governance:BulkDeleteThreshold`, THE `Pacs.Fas.Api` SHALL verify that `overrideToken` is present and that the SHA-256 hash of the token matches `Governance:OverrideTokenHashSha256`, and SHALL verify that the last successful backup is within `Governance:RequireBackupAgeHours` hours; IF either check fails, THEN THE `Pacs.Fas.Api` SHALL return `409` with error code `ERP-PACS-GOV-0001`.
7. WHEN a bulk-delete request passes all governance checks, THE `Pacs.Fas.Api` SHALL process each voucher as an individual DELETE (producing one `sync_outbox` row per voucher) and return a per-row outcome map.
8. THE `Pacs.Fas.Api` SHALL allocate `sequence_no` values using `SequenceAllocator` inside the same Dapper transaction as the business write, ensuring monotonic contiguous allocation (invariant I-4).
9. THE `Pacs.Fas.Api` SHALL compute `payload_json` using `CanonicalJsonWriter` and `payload_hash` using `PayloadHasher` before inserting the `sync_outbox` row.
10. THE `Pacs.Fas.Api` SHALL apply `[TraceableAction]` with `Severity=TraceSeverity.Warning` on the `DeleteVoucher` handler so the third audit witness is emitted via `Intellect.Erp.Traceability`.
11. THE `Pacs.Fas.Api` SHALL use `IAppLogger<T>` for all logging, call `BeginOperation` at API entry, and call `Checkpoint` at each persistence boundary and fault hook site.
12. THE `Pacs.Fas.Api` SHALL use `IErrorFactory.FromCatalog` for all thrown exceptions, with error codes sourced from `packaging/error-catalog/harness.yaml`.
13. THE `Pacs.Fas.Api` SHALL use `ICacheProvider` for lookup data (voucher types) with a 6-hour TTL and SHALL degrade gracefully to a MySQL read when Redis is unavailable (fail-open).
14. WHEN `Harness:TestMode=false`, THE `Pacs.Fas.Api` SHALL return `404` for all requests to `/api/test/*` routes.
15. THE `Pacs.Fas.Api` SHALL expose `/health/ready` checking MySQL_PACS, Redis_PACS, and Kafka (Kafka down = degraded, not unhealthy), and `/health/live` returning 200 whenever the process is running.
16. THE `Pacs.Fas.Api` SHALL read all configuration values — ports, connection strings, thresholds, intervals, topic names — from `appsettings.json` / environment variables via typed `*Options.cs` classes; no value SHALL be hardcoded.

*Traces to: I-1, I-2, I-4, G-04, G-20; Tests: SYNC-POS-001..004, CRIT-001, CRIT-011, CRIT-013, NEG-007, SEC-008.*

---

### Requirement 4: Pacs.Loans.Api — Loans Backend with Maker-Checker

**User Story:** As a Field Operator, I want to create and manage loan applications through a maker-checker workflow with mandatory amendment reasons and approver capture, so that every high-risk mutation produces a complete three-witness audit trail that satisfies regulatory retention requirements.

#### Acceptance Criteria

1. THE `Pacs.Loans.Api` SHALL expose: `POST /api/loan-applications`, `PUT /api/loan-applications/{id}`, `POST /api/loan-applications/{id}/submit`, `POST /api/loan-applications/{id}/approve`, `POST /api/loan-applications/{id}/reject`, `POST /api/loan-applications/{id}/amend`, `DELETE /api/loan-applications/{id}`, `GET /api/loan-applications` (paged), and `GET /api/loan-applications/{id}/timeline`.
2. WHEN a `POST /api/loan-applications/{id}/amend` request is received, THE `Pacs.Loans.Api` SHALL validate at the API boundary — before any database write — that `reason` is non-empty and `approver` is non-null and differs from the authenticated user; IF either check fails, THEN THE `Pacs.Loans.Api` SHALL return `422` with error code `ERP-PACS-VAL-0008`.
3. WHEN an amendment passes validation, THE `Pacs.Loans.Api` SHALL SELECT the loan application `FOR UPDATE`, UPDATE the loan application row, INSERT a `loan_amendment_history` row with `before_state_json`, `after_state_json`, `reason`, `approver`, and `correlation_id`, allocate a `sequence_no`, INSERT a `sync_outbox` row with `change_type='AMENDMENT'`, `priority=20`, and `amendmentMeta={reason, approver}` — all in a single transaction.
4. THE `Pacs.Loans.Api` SHALL apply `[TraceableAction(action: "Loans.LoanApp.Amend", Severity=TraceSeverity.Critical, Retention=RetentionClass.REGULATORY_10Y, CaptureBeforeAfter=true, PublishToKafka=true)]` on the `AmendAsync` handler.
5. WHEN a `POST /api/loan-applications/{id}/approve` request is received, THE `Pacs.Loans.Api` SHALL verify that the authenticated user's role is `checker` and that the checker differs from the maker; IF either check fails, THEN THE `Pacs.Loans.Api` SHALL return `422` with error code `ERP-PACS-VAL-0008`.
6. WHEN `Iam:Enabled=false`, THE `Pacs.Loans.Api` SHALL read identity from `X-Test-User` and role from `X-Test-Role` HTTP headers for all role checks.
7. THE `Pacs.Loans.Api` SHALL produce three witnesses after every amend or delete: (1) a `sync_outbox` row, (2) a `loan_amendment_history` or `voucher_deletion_audit` row, and (3) an `erp_traceability.audit_activity` row linked by the same `correlation_id`.
8. FOR ALL valid amendment requests, THE `Pacs.Loans.Api` SHALL ensure that querying `sync_outbox`, `loan_amendment_history`, and `erp_traceability.audit_activity` by `correlation_id` each returns exactly one row (three-witness property).
9. THE `Pacs.Loans.Api` SHALL expose `GET /api/loan-applications/{id}/timeline` returning a chronological list of artefacts (business row, outbox row, Kafka offset, NLDR received event, ACK, checkpoint) linked by `correlation_id`.
10. THE `Pacs.Loans.Api` SHALL use `IAppLogger<T>`, `IErrorFactory`, `ICacheProvider` (fail-open), and `SequenceAllocator` following the same patterns as `Pacs.Fas.Api`.
11. THE `Pacs.Loans.Api` SHALL read all configuration from `appsettings.json` / environment variables via typed `*Options.cs` classes with no hardcoded values.
12. WHEN `Harness:TestMode=false`, THE `Pacs.Loans.Api` SHALL return `404` for all `/api/test/*` routes.

*Traces to: I-2, G-06, G-21; Tests: SYNC-POS-004, CRIT-012, NEG-008, NEG-009.*

---

### Requirement 5: Pacs.SyncWorker — Outbox Drain and Inbox Consume Worker

**User Story:** As a Field Operator, I want a background worker that continuously drains the sync outbox to NLDR, consumes ACKs and commands from NLDR, sends heartbeats, recovers stale locks, and detects clock drift — all as resilient hosted services — so that sync operates autonomously without operator intervention.

#### Acceptance Criteria

1. THE `Pacs.SyncWorker` SHALL host the following `TraceableBackgroundService` derivatives: `OutboundRelayService`, `InboundConsumerService`, `HeartbeatService`, `LockReaperService`, `ReconciliationService`, `FileChunkUploaderService`, `CircuitBreakerStateLogger`, and `ClockDriftDetector`.
2. THE `OutboundRelayService` SHALL drain `sync_outbox` rows with `status='PENDING'` using `SELECT … FOR UPDATE SKIP LOCKED` ordered by `priority ASC, sequence_no ASC` in batches of `Sync:Outbox:BatchSize`.
3. WHEN the `OutboundRelayService` picks up a row, THE `Pacs.SyncWorker` SHALL update `status='IN_FLIGHT'` and `sent_at=NOW(6)` in a committed transaction before attempting to publish, so a crash after the update leaves the row resumable.
4. THE `OutboundRelayService` SHALL publish each envelope to the `Sync:OutboundTopic` Kafka topic using `IKafkaProducer` with the required Kafka headers (`correlationId`, `causationId`, `eventId`, `eventType`, `schemaVersion`, `pacsId`, `sequenceNo`, `idempotencyKey`).
5. WHEN `Sync:UseHttpTransport=true`, THE `OutboundRelayService` SHALL also POST the envelope to `Sync:NldrIngestUrl` using a `CorrelationDelegatingHandler`-wrapped `HttpClient`.
6. THE `InboundConsumerService` SHALL consume from `Sync:AcksTopic` and `Sync:CommandsTopic` using `[SubscribeToOutbox]` handlers; on receiving an ACK, THE `Pacs.SyncWorker` SHALL update the matching `sync_outbox` row to `status='ACKED'` and advance `sync_checkpoints.last_acked_sequence` only when the sequence is contiguous with the previous checkpoint.
7. WHEN a NACK is received, THE `Pacs.SyncWorker` SHALL update the `sync_outbox` row to `status='FAILED'`, increment `retry_count`, record `last_error`, and apply the configured retry backoff.
8. THE `HeartbeatService` SHALL publish a heartbeat envelope to `Sync:HeartbeatTopic` every `Sync:Heartbeat:IntervalSeconds` seconds containing `pacsId`, `sentAtUtc`, `outboxDepth`, `lastAckedSequence`, `buildVersion`, `gitSha`, `dataRootFreeGb`, and `uptimeSeconds`.
9. THE `LockReaperService` SHALL run every 30 seconds and reset any `sync_outbox` row with `status='IN_FLIGHT'` and `sent_at` older than `Sync:Outbox:ProcessingLockTimeoutSeconds` back to `status='PENDING'`.
10. THE `ClockDriftDetector` SHALL compare the local `IClock.UtcNow` to the `Date` header returned by NLDR every 60 seconds; WHEN drift exceeds `Sync:ClockDrift:MaxAllowedSeconds`, THE `Pacs.SyncWorker` SHALL emit a warning log with error code `ERP-PACS-HLT-0010`; WHEN drift exceeds `Sync:ClockDrift:BlockingSeconds`, THE `Pacs.SyncWorker` SHALL pause outbound relay and emit `ERP-PACS-SYN-0001`.
11. WHEN `retry_count` reaches `Sync:Outbox:QuarantineAfterAttempts`, THE `Pacs.SyncWorker` SHALL set `status='DEADLETTER'` and publish the envelope to `Sync:DeadletterTopic`.
12. WHEN the Kafka circuit breaker is OPEN, THE `OutboundRelayService` SHALL sleep for `Sync:Circuit:OpenDurationSeconds` before transitioning to HALF_OPEN and attempting a single probe publish.
13. THE `Pacs.SyncWorker` SHALL use `IClock` (not `DateTime.UtcNow`) for all timestamp comparisons so that `OffsetClock` can simulate time travel in tests.
14. THE `Pacs.SyncWorker` SHALL read all configuration from `appsettings.json` / environment variables via typed `*Options.cs` classes with no hardcoded values.
15. THE `Pacs.SyncWorker` SHALL expose `/health/ready` checking MySQL_PACS, Kafka, Redis, and NLDR HTTP probe (NLDR down = degraded, not unhealthy).

*Traces to: I-2, I-3, I-4, G-01, G-04, G-12, G-19; Tests: SYNC-POS-001..010, OFF-001..006, PWR-001..008, FAIL-001..010, SEQ-001..012.*

---

### Requirement 6: Pacs.OperatorUi — Field Operator Razor MVC UI

**User Story:** As a Field Operator, I want a Razor MVC web application with separate areas for FAS vouchers and Loans, a live online/offline banner, a sync dashboard, and a TestControl panel (in test mode), so that I can perform all business operations and observe sync state without needing direct database access.

#### Acceptance Criteria

1. THE `Pacs.OperatorUi` SHALL be a Razor MVC application with the following Areas: `Fas` (voucher list, create, edit, delete, bulk-delete confirmation, voucher detail with timeline), `Loans` (loan application list, create, amend, approve, reject, detail with timeline), `Shared/SyncDashboard` (outbox status counts, conflict list, retry-all button), and `Shared/TestControl` (fault injection controls).
2. THE `Pacs.OperatorUi` SHALL poll `GET /api/sync/status` every `Ui:Polling:StatusIntervalMs` milliseconds and update the online/offline banner colour: green when `online=true AND circuitState!='OPEN' AND (now - heartbeatLastOkAt) < 2 × Sync:Heartbeat:IntervalSeconds`; red otherwise.
3. WHEN the network to NLDR is blocked, THE `Pacs.OperatorUi` SHALL display the red "Offline" banner within `2 × Sync:Heartbeat:IntervalSeconds` seconds of the last successful heartbeat acknowledgement.
4. WHEN the network to NLDR is restored, THE `Pacs.OperatorUi` SHALL display the green "Online" banner within `2 × Sync:Heartbeat:IntervalSeconds` seconds of the first successful heartbeat acknowledgement.
5. THE `Pacs.OperatorUi` SHALL render a voucher/loan timeline at `GET /vouchers/{id}/timeline` and `GET /loan-applications/{id}/timeline` showing: business row created, outbox PENDING, Kafka publish (topic/partition/offset), NLDR received, ACK received, and checkpoint advance — all linked by `correlation_id`.
6. THE `Pacs.OperatorUi` SHALL render a conflicts page at `/conflicts` listing open `conflict_log` rows with side-by-side local vs remote state and three resolution buttons: "Keep Local", "Take Remote", and "Manual Merge".
7. WHEN `Harness:TestMode=true`, THE `Pacs.OperatorUi` SHALL render the `TestControl` area with buttons: "Go Offline", "Go Online", "Kafka Down", "Redis Flush", "Pause-after-DB-commit", "Drop-next-ACK", "Tamper Hash", "Time Jump", and "Run Demo …".
8. WHEN `Harness:TestMode=false`, THE `Pacs.OperatorUi` SHALL NOT render the `TestControl` area and SHALL return `404` for all `/api/test/*` requests.
9. THE `Pacs.OperatorUi` SHALL use `ICacheProvider` (fail-open) for session storage and SHALL continue to function correctly when Redis is flushed mid-session.
10. THE `Pacs.OperatorUi` SHALL propagate `X-Correlation-Id` headers on all outgoing HTTP calls to `Pacs.Fas.Api` and `Pacs.Loans.Api` using `CorrelationDelegatingHandler`.
11. THE `Pacs.OperatorUi` SHALL read all configuration from `appsettings.json` / environment variables via typed `*Options.cs` classes with no hardcoded values.

*Traces to: I-1, G-12, G-13, G-25; Tests: UI-001..005, OFF-004, CRIT-005.*


---

### Requirement 7: Nldr.Api — Strict Central Receiver

**User Story:** As a Central Admin, I want a strict NLDR API that validates every inbound event through a 12-step pipeline — rejecting tampered, out-of-order, schema-invalid, or unauthenticated events with precise error codes — so that the central repository maintains a trustworthy, tamper-evident record of all PACS activity.

#### Acceptance Criteria

1. THE `Nldr.Api` SHALL expose `POST /api/sync/ingest` that processes inbound envelopes through the following 12 steps in order: (1) parse JSON, (2) validate mTLS/test-token, (3) validate envelope schema, (4) check fault hook mode, (5) recompute and compare `payload_hash`, (6) validate sequence number, (7) validate change-type-specific constraints, (8) apply business state, (9) insert `received_event`, (10) insert `sync_inbox` row for dedupe, (11) enqueue ACK, (12) return 200.
2. IF the envelope JSON is malformed, THEN THE `Nldr.Api` SHALL return `400` with error code `ERP-NLDR-VAL-0001` without executing any subsequent pipeline step.
3. IF the mTLS certificate or test token is invalid, THEN THE `Nldr.Api` SHALL return `401` with error code `ERP-NLDR-SEC-0001`.
4. IF the recomputed `payload_hash` does not match `envelope.payloadHash`, THEN THE `Nldr.Api` SHALL return `422` with error code `ERP-NLDR-VAL-0002`, insert a `received_event` row with `apply_status='REJECTED'`, and NOT apply any business state change (invariant I-5).
5. IF `sequenceNo` is less than or equal to the last acknowledged sequence for the `pacsId` AND the `event_id` matches an existing `received_event` row, THEN THE `Nldr.Api` SHALL return `200` with `status='DUPLICATE'` and NOT insert a second business row (invariant I-3).
6. IF `sequenceNo` is less than or equal to the last acknowledged sequence for the `pacsId` AND the `event_id` does NOT match any existing row, THEN THE `Nldr.Api` SHALL return `422` with error code `ERP-NLDR-SEC-0002` (replayed event with altered payload).
7. IF `sequenceNo` is greater than `last_acked + 1`, THEN THE `Nldr.Api` SHALL insert `sequence_gap` rows for each missing sequence number and set `apply_status='GAP_WAITING'`.
8. IF `change_type='DELETE'` and `beforeState` is absent, THEN THE `Nldr.Api` SHALL return `422` with error code `ERP-NLDR-VAL-0006`.
9. IF `change_type='AMENDMENT'` and `amendmentMeta.reason` is empty or `amendmentMeta.approver` is absent, THEN THE `Nldr.Api` SHALL return `422` with error code `ERP-NLDR-VAL-0007`.
10. WHEN applying a `DELETE` event, THE `Nldr.Api` SHALL set `is_deleted=1`, `deleted_at`, and `deletion_reason` on the `nldr_business_voucher` or `nldr_business_loan` row and SHALL NOT physically delete the row.
11. WHEN applying an `AMENDMENT` event, THE `Nldr.Api` SHALL update the central business row and insert a `nldr_amendment_history` row.
12. THE entire 12-step pipeline SHALL execute inside a single MySQL transaction so that a failure at step 8 rolls back steps 9 and 10.
13. THE `Nldr.Api` SHALL expose `POST /api/files/{file_id}/chunks/{index}` for file chunk upload, `POST /api/commands` for creating NLDR-to-PACS commands, and read-only query endpoints for the dashboard.
14. THE `Nldr.Api` SHALL expose TestControl routes (`POST /api/test/failure-mode`, `POST /api/test/cert/reject-next`, `POST /api/test/db/restart`, `POST /api/test/tamper/{eventId}`, `POST /api/test/commands/duplicate/{commandId}`, `GET /api/test/state`) only when `Harness:TestMode=true`; WHEN `Harness:TestMode=false`, THE `Nldr.Api` SHALL return `404` for all `/api/test/*` routes.
15. THE `Nldr.Api` SHALL support all eight NLDR failure modes: `healthy`, `http500`, `timeout`, `dropAck`, `rateLimit`, `badAck`, `hashStrict`, and `sequenceStrict` as defined in §13.2 of the design.
16. THE `Nldr.Api` SHALL use `IAppLogger<T>`, `IErrorFactory`, `ICacheProvider` (fail-open), and `IAppLogger<T>.BeginOperation` / `Checkpoint` following the same patterns as the PACS APIs.
17. THE `Nldr.Api` SHALL read all configuration from `appsettings.json` / environment variables via typed `*Options.cs` classes with no hardcoded values.

*Traces to: I-3, I-4, I-5, G-01, G-03; Tests: SYNC-POS-001..010, SEC-001..008, NEG-001..020, CRIT-001..020.*

---

### Requirement 8: Nldr.SyncWorker — Central ACK and Command Publisher

**User Story:** As a Central Admin, I want a background worker on the NLDR side that publishes ACKs and commands to Kafka, consumes heartbeats, assembles file chunks, and runs reconciliation, so that PACS nodes receive timely feedback and the central state stays consistent.

#### Acceptance Criteria

1. THE `Nldr.SyncWorker` SHALL host the following `TraceableBackgroundService` derivatives: `AckPublisherService`, `CommandPublisherService`, `IngestKafkaConsumer`, `HeartbeatConsumer`, `FileChunkAssembler`, and `ReconciliationService`.
2. THE `AckPublisherService` SHALL drain the NLDR outbox and publish ACK/NACK envelopes to `epacs.nldr.acks` with `pacs_id` as the Kafka message key.
3. THE `CommandPublisherService` SHALL publish commands created via `POST /api/commands` to `epacs.nldr.commands`.
4. THE `IngestKafkaConsumer` SHALL consume from `epacs.pacs.outbound` and process each message through the same 12-step ingest pipeline as `POST /api/sync/ingest`.
5. THE `HeartbeatConsumer` SHALL consume from `epacs.pacs.heartbeat` and insert a row into the `heartbeat` table for each message received.
6. THE `FileChunkAssembler` SHALL assemble received file chunks into a complete file, verify the full-file SHA-256 against `file_received.file_sha256`, set `status='COMPLETED'` on match, and set `status='REJECTED'` on mismatch.
7. THE `Nldr.SyncWorker` SHALL read all configuration from `appsettings.json` / environment variables via typed `*Options.cs` classes with no hardcoded values.

*Traces to: I-3; Tests: SYNC-POS-001..010, OFF-004, SYNC-POS-010.*

---

### Requirement 9: Nldr.DashboardUi — Central Observability Dashboard

**User Story:** As a Central Admin, I want a Razor MVC dashboard that shows live counts of received events, ACKs, NACKs, sequence gaps, conflicts, heartbeats, files, and commands per PACS node, so that I can monitor the health of all field nodes from a single screen.

#### Acceptance Criteria

1. THE `Nldr.DashboardUi` SHALL provide the following pages: Overview, Received Events (paged with filters), Sequence Gaps (with Reconcile button), Conflicts, Heartbeats (last 50 per PACS), Files, Commands (with create form), Reconciliation (last 10 reports), and TestControl (only when `Harness:TestMode=true`).
2. THE Overview page SHALL display live counts: events received today, ACKed, NACKed, gaps, conflicts, files received, and heartbeat status per PACS node.
3. THE Received Events page SHALL support filtering by `pacs_id`, `change_type`, and `apply_status`, and clicking a row SHALL navigate to a drill-down view showing all pipeline steps for that event.
4. THE Sequence Gaps page SHALL list open `sequence_gap` rows per `pacs_id` and SHALL provide a "Reconcile" button that calls `POST /api/reconciliation/{pacsId}`.
5. THE Commands page SHALL provide a form to create a new command (policy update, master data, correction) targeted at a specific PACS node.
6. WHEN `Harness:TestMode=true`, THE `Nldr.DashboardUi` SHALL render the TestControl page with buttons matching the NLDR TestControl routes defined in §13.2 of the design.
7. WHEN `Harness:TestMode=false`, THE `Nldr.DashboardUi` SHALL NOT render the TestControl page.
8. THE `Nldr.DashboardUi` SHALL propagate `X-Correlation-Id` headers on all outgoing HTTP calls to `Nldr.Api`.
9. THE `Nldr.DashboardUi` SHALL read all configuration from `appsettings.json` / environment variables via typed `*Options.cs` classes with no hardcoded values.

*Traces to: G-12, G-13; Tests: UI-001..005.*

---

### Requirement 10: Fault Injection and TestControl

**User Story:** As a QA / Test Engineer, I want in-process TestControl routes on both the PACS and NLDR sides that can arm fault hooks, toggle failure modes, inject sequence gaps, tamper with outbox rows, and control the clock — all gated by `Harness:TestMode=true` — so that every test case in the test plan can be executed deterministically without modifying production code paths.

#### Acceptance Criteria

1. THE PACS TestControl SHALL expose all routes listed in §13.1 of the design, including: `POST /api/test/offline`, `POST /api/test/network/block`, `POST /api/test/kafka/stop`, `POST /api/test/kafka/start`, `POST /api/test/redis/flush`, `POST /api/test/hooks/{hookId}`, `POST /api/test/clock/jump`, `POST /api/test/clock/reset`, `POST /api/test/tamper/last-outbox`, `POST /api/test/sequence/skip`, `POST /api/test/scenarios/{name}/run`, and `GET /api/test/state`.
2. THE NLDR TestControl SHALL expose all routes listed in §13.2 of the design, supporting all eight failure modes: `healthy`, `http500`, `timeout`, `dropAck`, `rateLimit`, `badAck`, `hashStrict`, and `sequenceStrict`.
3. THE `FaultHook` enum SHALL include all thirteen hook IDs: `BeforeDbCommit`, `AfterDbCommit`, `BeforeKafkaPublish`, `AfterKafkaPublish`, `BeforeAckUpdate`, `AfterAckUpdate`, `BeforeInboxApply`, `AfterInboxApply`, `BeforeOutboxFetch`, `AfterMarkInFlight`, `BeforeFileChunkUpload`, `AfterFileChunkAck`, and `BeforeHeartbeatPublish`.
4. WHEN a fault hook is armed with `mode='pause'`, THE `IFaultInjector` SHALL block execution at the hook site until either `durationMs` elapses or the hook is manually released.
5. WHEN a fault hook is armed with `mode='crash'`, THE `IFaultInjector` SHALL call `Environment.Exit(1)` at the hook site, simulating a hard process kill.
6. WHEN a fault hook is armed with `mode='throw'`, THE `IFaultInjector` SHALL raise the specified exception type at the hook site, exercising the retry path.
7. WHEN a fault hook is armed with `count=N`, THE `IFaultInjector` SHALL auto-disarm the hook after it has fired `N` times.
8. THE fault hook state SHALL be stored in Redis (`pacs:fault:*`) so that all processes in the same PACS profile see the same armed hooks.
9. WHEN `POST /api/test/sequence/skip` is called with `count=N`, THE PACS TestControl SHALL advance `sync_sequence.next_sequence` by `N` to inject a gap, enabling SEQ-002 and CRIT-008 test scenarios.
10. WHEN `POST /api/test/tamper/last-outbox` is called, THE PACS TestControl SHALL directly mutate the specified field of the most recent `sync_outbox` row, enabling SEC-001 and CRIT-010 test scenarios.
11. WHEN `POST /api/test/clock/jump` is called with `offsetSeconds=N`, THE TestControl SHALL write `N` to Redis key `pacs:clock-offset-seconds` so that all processes using `OffsetClock` immediately reflect the new offset.
12. THE `GET /api/test/state` endpoint SHALL return a JSON object listing all currently armed hooks with their remaining `count` values and all active failure modes.
13. WHEN `Harness:TestMode=false`, ALL TestControl routes on both PACS and NLDR sides SHALL return `404` without executing any logic.

*Traces to: G-02, G-03, G-07, G-22; Tests: PWR-001..008, FAIL-001..010, SEC-001..008, SEQ-001..012, CRIT-001..020.*

---

### Requirement 11: File Sync with Chunked Upload and Resume

**User Story:** As a Field Operator, I want to upload files (voucher attachments, loan documents) that are chunked, queued in the outbox, and uploaded to NLDR with resume capability after a power cut or network interruption, so that large files are reliably transferred without re-uploading already-acknowledged chunks.

#### Acceptance Criteria

1. WHEN a file is uploaded via `POST /api/files/upload`, THE `Pacs.Fas.Api` or `Pacs.Loans.Api` SHALL stream the file to `{DataRoot}/files/staging/{guid}.tmp`, compute SHA-256 on the fly, check `file_sync_registry` for an existing row with the same `(pacs_id, file_sha256)`, and if a duplicate is found, return the existing `file_id` without creating a new registry row.
2. WHEN a new file is accepted, THE API SHALL insert a `file_sync_registry` row with `status='PENDING'`, `total_chunks = ceil(file_size / Sync:File:ChunkSizeBytes)`, and move the file to `{DataRoot}/files/queue/{file_id}.dat`.
3. THE `Pacs.SyncWorker.FileChunkUploaderService` SHALL upload chunks of `Sync:File:ChunkSizeBytes` bytes to `Nldr.Api POST /api/files/{file_id}/chunks/{index}` in order, updating `chunks_acked` after each acknowledged chunk.
4. WHEN the `FileChunkUploaderService` restarts after a crash, THE `Pacs.SyncWorker` SHALL resume from chunk index `chunks_acked + 1` without re-uploading already-acknowledged chunks (resume property).
5. FOR ALL files of any size, THE `FileChunkUploaderService` SHALL ensure that `chunks_acked` after a resume equals the number of chunks successfully sent before the crash (invariant property).
6. WHEN NLDR receives the final chunk, THE `Nldr.SyncWorker.FileChunkAssembler` SHALL verify the full-file SHA-256 against `file_received.file_sha256`; IF the hash matches, THEN THE `Nldr.SyncWorker` SHALL set `status='COMPLETED'` and enqueue an ACK; IF the hash does not match, THEN THE `Nldr.SyncWorker` SHALL set `status='REJECTED'` and enqueue a NACK.
7. THE `file_sync_registry` priority column SHALL default to `50` for files ≤ `Sync:File:SmallFileThresholdKb` KB and `80` for files above that threshold, ensuring small files drain before large files during reconnect.
8. THE `FileChunkUploaderService` SHALL respect the `Sync:File:MaxConcurrentChunks` configuration limit for parallel chunk uploads.
9. THE `Pacs.Fas.Api` and `Pacs.Loans.Api` SHALL enforce `Sync:File:MaxFileSizeMb` and return `400` with an appropriate error code for files exceeding the limit.
10. THE file staging and queue paths SHALL be read from `Sync:File:StagingPath` and `Sync:File:QueuePath` configuration; no path SHALL be hardcoded.

*Traces to: G-08; Tests: SYNC-POS-010, OFF-006, PWR-008, NEG-020.*

---

### Requirement 12: Sequence Integrity and Idempotency

**User Story:** As a QA / Test Engineer, I want the harness to enforce monotonic, contiguous sequence numbers per PACS node and stream, and to handle duplicate events idempotently, so that I can prove the sync architecture never loses, duplicates, or reorders events.

#### Acceptance Criteria

1. THE `SequenceAllocator` SHALL guarantee that for any two concurrent calls with the same `(pacs_id, stream_name)`, the allocated sequence numbers are distinct and form a contiguous range with no gaps (monotonic contiguous property, invariant I-4).
2. WHEN the `Nldr.Api` receives an event with `sequenceNo == last_acked + 1`, THE `Nldr.Api` SHALL apply the event and advance the checkpoint.
3. WHEN the `Nldr.Api` receives an event with `sequenceNo > last_acked + 1`, THE `Nldr.Api` SHALL insert `sequence_gap` rows for each missing sequence number between `last_acked + 1` and `sequenceNo - 1`.
4. WHEN the `Nldr.Api` receives an event with `sequenceNo <= last_acked` and the same `event_id` as an existing `received_event` row, THE `Nldr.Api` SHALL return `200` with `status='DUPLICATE'` and SHALL NOT create a second business row (invariant I-3).
5. FOR ALL events replayed N times with the same `event_id`, THE `Nldr.Api` SHALL ensure that the count of business rows for that `entity_id` equals 1 and the count of `sync_inbox` rows with `status='DUPLICATE'` equals N-1 (idempotency property).
6. WHEN the `Nldr.Api` receives an event with the same `idempotency_key` but a different `event_id`, THE `Nldr.Api` SHALL treat it as a duplicate and return `200` with `status='DUPLICATE'`.
7. THE `sync_checkpoints.last_acked_sequence` SHALL advance only when ACKs are received in contiguous order; out-of-order ACKs SHALL be buffered until the gap is filled.
8. FOR ANY sequence of ACKs received in any order, THE checkpoint value SHALL equal the highest contiguous sequence number acknowledged from the base (checkpoint correctness property).
9. WHEN `POST /api/test/sequence/skip` is called with `count=N`, THE PACS TestControl SHALL advance `sync_sequence.next_sequence` by `N`, causing the next event to have a gap of `N` in the sequence, which NLDR SHALL detect and record in `sequence_gap`.
10. THE `Nldr.Api` SHALL enforce a `UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no)` constraint on `received_event` so that a duplicate sequence number from the same PACS is rejected at the database level.

*Traces to: I-3, I-4; Tests: SEQ-001..012, CRIT-008, CRIT-009, NEG-010, NEG-018.*

---

### Requirement 13: Tamper Detection and Security

**User Story:** As a QA / Test Engineer, I want the harness to reject any envelope whose payload hash does not match the recomputed canonical hash, to enforce mTLS or test-token authentication, and to redact PII from all logs and support bundles, so that I can prove the sync architecture is tamper-evident and compliant with data protection requirements.

#### Acceptance Criteria

1. FOR ALL valid envelopes `e`, mutating any byte of `e.payload`, `e.beforeState`, or `e.amendmentMeta` SHALL cause `PayloadHasher` to produce a different hash, and THE `Nldr.Api` SHALL reject the mutated envelope with `ERP-NLDR-VAL-0002` (tamper detection property, invariant I-5).
2. THE `Nldr.Api` SHALL recompute `payload_hash` from the received `payload`, `beforeState`, and `amendmentMeta` fields using `CanonicalJsonWriter` and `PayloadHasher` and SHALL compare the result to `envelope.payloadHash` before applying any business state.
3. WHEN `Sync:UseHttpTransport=true`, THE `Pacs.SyncWorker` SHALL present a client certificate on every HTTP call to `Nldr.Api`; THE `Nldr.Api` SHALL reject requests without a valid certificate with `401 ERP-NLDR-SEC-0001`.
4. WHEN `Harness:TestMode=true`, THE `Nldr.Api` SHALL accept a `X-Test-Token` header as an alternative to mTLS for local development.
5. THE `[SensitiveAttribute]`, `[DoNotLogAttribute]`, and `[MaskAttribute]` annotations on DTO fields SHALL cause the `Intellect.Erp.Observability.Core` redaction engine to mask those values in all structured log output.
6. FOR ALL DTOs with annotated PII fields, THE log output SHALL never contain the raw field value (PII redaction property).
7. THE `POST /api/support/bundle` endpoint SHALL run `Harness.Common.Redaction.SupportBundleScanner` over the generated ZIP and SHALL refuse to emit the bundle if any un-redacted PII field is detected.
8. THE `Nldr.Api` in `hashStrict` mode SHALL reject any envelope where the recomputed hash differs from `envelope.payloadHash`, even if the difference is a single character.
9. THE `Nldr.Api` SHALL validate that `envelope.createdAtUtc` is not more than `Sync:ClockDrift:MaxAllowedSeconds` seconds in the future relative to the NLDR server clock; IF it is, THEN THE `Nldr.Api` SHALL return `400` with error code `ERP-NLDR-VAL-0005`.
10. THE `Pacs.Fas.Api` bulk-delete endpoint SHALL verify the governance override token by comparing `SHA256(token)` to `Governance:OverrideTokenHashSha256`; the raw token value SHALL never be stored or logged.

*Traces to: I-5, G-05; Tests: SEC-001..008, CRIT-010, CRIT-020, NEG-020.*


---

### Requirement 14: Reconciliation

**User Story:** As a Central Admin, I want an automated reconciliation service that compares PACS outbox state to NLDR received-event state and produces a structured JSON report, so that I can prove end-to-end completeness and detect any tampering or data drift between the field node and the central repository.

#### Acceptance Criteria

1. THE `ReconciliationRunner.RunAsync(pacsId, windowFrom, windowTo)` SHALL execute the following seven checks in order: (1) local gap check via `LAG()` on `sync_outbox.sequence_no`, (2) local-vs-central completeness (every ACKed outbox row has a matching `received_event` row), (3) hash integrity (local `payload_hash` equals NLDR `payload_hash` for every common `event_id`), (4) duplicate central rows (no `entity_id` appears more than once in `nldr_business_voucher` or `nldr_business_loan`), (5) orphan central rows (no NLDR business row lacks a matching PACS outbox row), (6) checkpoint correctness (`sync_checkpoints.last_acked_sequence` is between the max ACKed sequence and the max sequence), and (7) manual-tampering detection (recompute hash from current NLDR business state for sampled rows and compare to `received_event.payload_hash`).
2. THE reconciliation report SHALL be saved as JSON to `{DataRoot}/reconciliation/RUN-{utcDate}.json` with the structure: `{ pacsId, windowFrom, windowTo, status: "PASS"|"FAIL", checks: [...], summary: { expected, localAck, centralReceived, hashMismatches, gaps, orphans, duplicates } }`.
3. WHEN all seven checks pass, THE reconciliation report SHALL have `status='PASS'`; WHEN any check fails, THE report SHALL have `status='FAIL'` with the failing check identified in the `checks` array.
4. THE `ReconciliationService` SHALL run automatically at `02:00` local time daily and SHALL also run on demand when `POST /api/reconciliation/run` is called.
5. FOR ANY set of events where PACS outbox and NLDR received-event are in perfect sync, THE reconciliation report SHALL accurately report zero gaps, zero hash mismatches, zero orphans, and zero duplicates (reconciliation correctness property).
6. THE reconciliation report SHALL be triggered automatically after every CRIT-* automated test scenario and after every backup/restore operation.
7. THE `Nldr.DashboardUi` Reconciliation page SHALL display the last 10 reconciliation reports with PASS/FAIL status and a link to the full JSON.
8. THE `ReconciliationRunner` path for the report output SHALL be read from `Pacs:DataRoot` configuration; no path SHALL be hardcoded.

*Traces to: G-10; Tests: SEQ-011, CRIT-017, BAK-006.*

---

### Requirement 15: Conflict Detection and Resolution

**User Story:** As a Central Admin, I want the NLDR to detect when an incoming PACS event conflicts with a diverged central state, surface the conflict in the dashboard, and allow resolution via "Keep Local", "Take Remote", or "Manual Merge" — so that data integrity is maintained even when the same entity is modified independently at both ends.

#### Acceptance Criteria

1. WHEN the `Nldr.Api` receives an `UPDATE` or `AMENDMENT` event and the current central business row has been modified since the `beforeState` version implied by the event, THE `Nldr.Api` SHALL insert a `conflict_log` row with `resolution='PENDING'` instead of applying the event, and SHALL return an ACK with `outcome='CONFLICT'`.
2. THE `Pacs.OperatorUi` `/conflicts` page SHALL display open `conflict_log` rows with side-by-side local state (from the PACS payload) and remote state (from the NLDR business table).
3. WHEN the operator selects "Keep Local", THE `Nldr.Api` SHALL overwrite the central business row with the PACS payload, set `conflict_log.resolution='LOCAL'`, and publish a correction command to `epacs.nldr.commands` so the PACS records the resolution.
4. WHEN the operator selects "Take Remote", THE `Nldr.Api` SHALL publish a correction command to `epacs.nldr.commands` instructing the PACS to apply the remote state locally, and set `conflict_log.resolution='REMOTE'`.
5. WHEN the operator selects "Manual Merge", THE `Nldr.DashboardUi` SHALL require a non-empty `reason` and a non-null `approver` before accepting the merged state, and SHALL produce a full audit trail via `[TraceableAction]`.
6. WHEN a conflict is resolved, BOTH the PACS and NLDR `conflict_log` rows SHALL have `resolution != NULL` and `resolved_at` populated.
7. THE reconciliation report SHALL report `status='FAIL'` when any `conflict_log` row has `resolution='PENDING'`.

*Traces to: G-09; Tests: CRIT-019.*

---

### Requirement 16: Demo Mode — Scenario Player

**User Story:** As a Demo Presenter, I want to run any of the built-in demo scenarios with a single button click and receive a complete evidence folder automatically, so that stakeholder demonstrations are reproducible, reliable, and produce forensic artefacts without manual steps.

#### Acceptance Criteria

1. THE `Harness.ScenarioPlayer` SHALL expose `POST /api/test/scenarios/{name}/run` returning `{ runId, scenarioName, startedAt }` and SHALL stream progress over SignalR hub `/hubs/scenario/{runId}` or polled at `GET /api/test/scenarios/runs/{runId}`.
2. THE `Harness.ScenarioPlayer` SHALL implement all sixteen built-in scenarios listed in §20.2 of the design: `demo-happy-path`, `demo-offline-reconnect`, `demo-ack-lost`, `demo-kafka-down`, `demo-redis-flush`, `demo-power-cut-after-commit`, `demo-sequence-gap`, `demo-duplicate-replay`, `demo-tamper`, `demo-delete-with-before-state`, `demo-amend-with-reason`, `demo-bulk-delete-guardrail`, `demo-conflict-edit`, `demo-30-day-offline-compressed`, `demo-clock-drift`, and `demo-support-bundle-pii`.
3. WHEN a scenario completes, THE `Harness.ScenarioPlayer` SHALL automatically invoke the evidence collector to produce an `Evidence/RUN-{utc}-{scenarioName}/` folder with the structure defined in §24.1 of the design.
4. THE `demo-30-day-offline-compressed` scenario SHALL use `OffsetClock` to advance the clock by 30 days and generate 30,000 events with spread timestamps, completing the simulation in under 10 minutes on a developer laptop.
5. WHEN the same scenario is run twice with the same seed, THE `Harness.ScenarioPlayer` SHALL produce evidence folders with identical structure and equivalent content (reproducibility property).
6. THE `Pacs.OperatorUi` TestControl area SHALL display a "Run Demo" button for each built-in scenario when `Harness:TestMode=true`.
7. THE `Harness.ScenarioPlayer` SHALL be a library project hosted inside both `Pacs.OperatorUi` and `Nldr.DashboardUi` without being a separate process.
8. THE scenario name parameter SHALL be validated against the list of known scenario names; IF an unknown name is provided, THE endpoint SHALL return `404`.

*Traces to: G-13; Tests: CRIT-001..020.*


---

### Requirement 17: Database Schema and Migrations

**User Story:** As a Release Engineer, I want all database schemas defined as versioned SQL migration scripts that DbUp can apply idempotently, so that the harness can be reset, upgraded, and packaged into the installer payload without manual SQL intervention.

#### Acceptance Criteria

1. THE `db/mysql/pacs/` directory SHALL contain exactly four migration files: `V001__core_business.sql`, `V002__sync_tables.sql`, `V003__orchestration_compat.sql`, and `V004__seed.sql`.
2. THE `V001__core_business.sql` SHALL define tables: `voucher`, `voucher_line`, `voucher_deletion_audit`, `loan_application`, `loan_amendment_history`, and `file_sync_registry` with all columns, constraints, and indexes specified in §6.1 of the design.
3. THE `V002__sync_tables.sql` SHALL define tables: `sync_sequence`, `sync_outbox`, `sync_inbox`, `sync_checkpoints`, and `conflict_log` with all columns, constraints, and indexes specified in §6.1 of the design.
4. THE `V003__orchestration_compat.sql` SHALL create the `OutboxMessages` view mapping `sync_outbox` columns to the `utils-orchestration` NuGet's expected column names, and SHALL create the `InboxMessages` and `SagaInstances` tables for orchestration compatibility.
5. THE `V004__seed.sql` SHALL insert `sync_sequence` base rows for `PACS-AP-0001` and `PACS-AP-0002` on streams `pacs.outbound` and `pacs.heartbeat`, and SHALL insert lookup data for voucher types and loan statuses.
6. THE `db/mysql/nldr/` directory SHALL contain exactly seven migration files: `V001__received_event.sql`, `V002__central_policy.sql`, `V003__ack_log.sql`, `V004__conflict_log.sql`, `V005__file_received.sql`, `V006__amendment_history.sql`, and `V007__heartbeat.sql`.
7. THE `sync_outbox` table SHALL have a `UNIQUE KEY uq_event (event_id)` and a `UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no)` constraint to enforce invariants I-3 and I-4 at the database level.
8. THE `received_event` table SHALL have a `UNIQUE KEY uq_event (event_id)` and a `UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no)` constraint.
9. WHEN DbUp applies all migrations to a fresh MySQL instance, THE resulting schema SHALL allow `dotnet test Harness.IntegrationTests` to pass without manual schema changes.
10. THE `scripts/reset-lab.ps1` and `scripts/reset-lab.sh` SHALL drop and recreate both databases, re-apply all migrations, re-seed data, recreate Kafka topics, and confirm all services are healthy — completing in under 60 seconds on a developer laptop.

*Traces to: G-01, G-04, I-2, I-3, I-4; Tests: M0 milestone acceptance.*

---

### Requirement 18: Docker Compose Infrastructure

**User Story:** As a QA / Test Engineer, I want Docker Compose files that bring up the complete harness infrastructure — Kafka in KRaft mode, two MySQL instances, two Redis instances, and all seven .NET projects — with a single command, so that any team member can reproduce the full test environment on a developer laptop.

#### Acceptance Criteria

1. THE `docker/docker-compose.yml` SHALL define services for: `kafka` (KRaft mode, port 9092), `pacs-mysql` (port 3307), `nldr-mysql` (port 3308), `pacs-redis` (port 6380), `nldr-redis` (port 6381), `pacs-fas-api` (port 5101), `pacs-loans-api` (port 5102), `pacs-sync` (no external port), `pacs-operator-ui` (port 5301), `nldr-api` (port 5201), `nldr-sync` (no external port), and `nldr-dashboard-ui` (port 5401).
2. THE `docker/docker-compose.minimal.yml` SHALL define only the infrastructure services (Kafka, two MySQL, two Redis) for local `dotnet run` development.
3. THE `docker/docker-compose.lab.yml` SHALL define a VM lab profile with network latency injection for realistic offline/reconnect testing.
4. WHEN `docker compose -f docker/docker-compose.yml up -d --build` is executed, ALL services SHALL reach a healthy state within 120 seconds.
5. THE `docker/env/pacs.env` and `docker/env/nldr.env` files SHALL supply all environment variable overrides for the Docker profile; no connection strings or secrets SHALL be hardcoded in `docker-compose.yml`.
6. THE Kafka service SHALL use KRaft mode (no ZooKeeper) with `KAFKA_CFG_PROCESS_ROLES=broker,controller`.
7. THE `Pacs.SyncWorker` service SHALL declare `depends_on` for `pacs-mysql`, `pacs-redis`, `kafka`, and `nldr-api` with health-check conditions.

*Traces to: G-26; Tests: Harness.IntegrationTests, Harness.ChaosTests.*

---

### Requirement 19: Operational Scripts

**User Story:** As a QA / Test Engineer, I want a complete set of PowerShell and shell scripts for lab management — reset, go-online/offline, kill/restart services, hard power-cut, evidence collection, multi-PACS seeding, and time-jump — so that every test scenario can be set up and torn down reproducibly from the command line.

#### Acceptance Criteria

1. THE `scripts/` directory SHALL contain: `reset-lab.ps1`, `reset-lab.sh`, `go-online.ps1`, `go-offline.ps1`, `kill-service.ps1`, `restart-service.ps1`, `hard-powercut-vm.ps1`, `collect-evidence.ps1`, `seed-multi-pacs.ps1`, and `time-jump.ps1`.
2. THE `reset-lab.ps1` and `reset-lab.sh` SHALL execute `docker compose down -v`, wait for health, re-apply migrations, re-seed data, recreate Kafka topics, and confirm all services are healthy — completing in under 60 seconds.
3. THE `go-offline.ps1` SHALL call `POST /api/test/offline` with `{ enabled: true }` on the PACS TestControl and SHALL optionally add an OS firewall rule blocking traffic to NLDR.
4. THE `go-online.ps1` SHALL call `POST /api/test/offline` with `{ enabled: false }` and SHALL remove any firewall rules added by `go-offline.ps1`.
5. THE `hard-powercut-vm.ps1` SHALL call `Stop-VM -TurnOff` on the specified Hyper-V VM, simulating a hard power cut without graceful shutdown.
6. THE `collect-evidence.ps1` SHALL produce an `Evidence/RUN-{utc}-{scenario}/` folder with the structure defined in §24.1 of the design: `run-sheet.md`, `environment/`, `screenshots/`, `sql/`, `kafka/`, `redis/`, `logs/`, `reconciliation/`, `support-bundle/`, and `faults/`.
7. THE `seed-multi-pacs.ps1` SHALL configure a second PACS profile (`PACS-AP-0002`) with its own schema and `sync_sequence` rows for multi-PACS testing (SEQ-009, SEQ-010).
8. THE `time-jump.ps1` SHALL call `POST /api/test/clock/jump` with the specified `offsetSeconds` on the PACS TestControl.
9. ALL scripts SHALL accept parameters for service URLs, database connection strings, and other environment-specific values; no values SHALL be hardcoded in the scripts.

*Traces to: G-26; Tests: PWR-001..008, OFF-001..006, SEQ-009, SEQ-010.*

---

### Requirement 20: Test Projects

**User Story:** As a QA / Test Engineer, I want four test projects — contract tests, integration tests, chaos tests, and long-offline tests — that together cover all 100+ test cases from the test plan, so that every invariant and scenario can be verified automatically in CI.

#### Acceptance Criteria

1. THE `Harness.ContractTests` project SHALL test: envelope schema validation, hash canonicalization determinism, sequence allocation rules, and idempotency key format — all without requiring Docker or network access.
2. THE `Harness.ContractTests` project SHALL include a property-based test using a PBT library (e.g., FsCheck or CsCheck) that verifies: for any object with any key insertion order, `CanonicalJsonWriter` produces the same output (determinism property).
3. THE `Harness.ContractTests` project SHALL include a round-trip property test: for any valid `EventEnvelope`, `parse(serialize(envelope))` produces an equivalent envelope.
4. THE `Harness.ContractTests` project SHALL include a property test verifying that for any valid inputs, `IdempotencyKey.Format(inputs)` produces a string matching the pattern `^[A-Z0-9-]+:[a-z_]+:[^:]+:(INSERT|UPDATE|DELETE|AMENDMENT):\d{4}-\d{2}-\d{2}T`.
5. THE `Harness.IntegrationTests` project SHALL use Testcontainers to spin up MySQL, Kafka, and Redis for each test class, and SHALL test the full API + Worker + DB + Kafka flow end-to-end.
6. THE `Harness.IntegrationTests` project SHALL include a `ThreeWitnessTests` class that verifies all three witnesses exist after every amend and delete operation.
7. THE `Harness.IntegrationTests` project SHALL include tests covering: SYNC-POS-001..010, OFF-001..006, SEQ-001..012, FAIL-001..010, and NEG-001..020.
8. THE `Harness.ChaosTests` project SHALL implement power-cut, network-partition, and service-kill scenarios using the TestControl routes and operational scripts, covering PWR-001..008.
9. THE `Harness.LongOfflineTests` project SHALL implement 7-day, 30-day, and 60-day simulation runners using `OffsetClock` and compressed time, covering CRIT-017, SEQ-011, and PERF-001..006.
10. THE `Harness.LongOfflineTests` project SHALL assert performance targets from `Performance:Targets:*` configuration: drain throughput ≥ 500 events/min, p99 INSERT latency < 50 ms, Kafka lag returns to 0 within 5 minutes after backlog.
11. ALL test projects SHALL use xUnit + FluentAssertions + Moq following the naming convention `MethodName_Scenario_ExpectedResult`.
12. ALL test projects SHALL use `Intellect.Erp.Observability.Testing` fakes for logging assertions.

*Traces to: G-02, G-05, G-10; Tests: all test classes.*

---

### Requirement 21: Configuration and Zero-Hardcoding

**User Story:** As a Release Engineer, I want every configurable value — ports, connection strings, thresholds, intervals, topic names, paths, and credentials — to come from typed `*Options.cs` classes bound to `appsettings.json` or environment variables, so that the harness can be deployed to any environment by changing configuration alone.

#### Acceptance Criteria

1. THE `Harness.Common` library SHALL define the following `*Options.cs` classes: `PacsOptions`, `SyncOptions`, `HarnessOptions`, `GovernanceOptions`, `NldrOptions`, `UiOptions`, `MessagingOptions`, `CachingOptions`, and `TraceabilityOptions`.
2. EACH `*Options.cs` class SHALL have a `public const string SectionName` property and SHALL be registered via `services.Configure<TOptions>(configuration.GetSection(TOptions.SectionName))`.
3. THE `SyncOptions` class SHALL expose typed sub-options for: `OutboxOptions` (PollIntervalMs, BatchSize, ProcessingLockTimeoutSeconds, OutboxRetentionDays), `RetryOptions` (MaxAttempts, BaseDelayMs, MaxDelayMs, JitterFactor, RespectRetryAfter), `CircuitOptions` (FailureThreshold, OpenDurationSeconds, HalfOpenProbeCount), `HeartbeatOptions` (IntervalSeconds, OnlineWindowSeconds), `FileOptions` (ChunkSizeBytes, MaxConcurrentChunks, StagingPath, QueuePath, MaxFileSizeMb, SmallFileThresholdKb), `PriorityOptions` (VoucherDefault, LoanAmendment, LoanDefault, FileSmall, FileLarge, Heartbeat), and `ClockDriftOptions` (MaxAllowedSeconds, BlockingSeconds).
4. THE `HarnessOptions` class SHALL expose `TestMode` (bool, default false), `Profile` (string, default "Default"), and `ScenarioPlayerEnabled` (bool, default false).
5. THE `Harness.Common.HostBuilderExtensions.AddHarnessConfiguration(builder)` extension method SHALL register `AddJsonFile` for `appsettings.json`, `appsettings.{Environment}.json`, `appsettings.Profile.{Harness:Profile}.json`, and `AddEnvironmentVariables` — in that order — for all projects.
6. WHEN the harness runs under the installer with `Harness:Profile="Installer"`, THE configuration SHALL be read from `${DataRoot}/config/appsettings.Production.json` generated by the installer from the `.epcfg` site config pack.
7. THE `Performance:Targets:*` configuration section SHALL define all performance SLOs: `MinDrainEventsPerMinute`, `MaxInsertLatencyP99Ms`, `MaxKafkaLagReturnMinutes`, `MaxDbSizeGb`, `WarmCacheP95Ms`, `ColdCacheP95Ms`, and `MaxHeapGrowthMb`.
8. WHEN the same harness binary is run with different configuration values, THE behaviour SHALL change accordingly — verified by `Harness.ContractTests` running with multiple configuration permutations.

*Traces to: G-17; Tests: M0 milestone acceptance, all test projects.*

---

### Requirement 22: Installer Integration

**User Story:** As a Release Engineer, I want the harness to integrate with the ePACS offline installer so that it can be packaged as a payload, installed as native Windows services, configured from a `.epcfg` site config pack, and smoke-tested automatically after installation.

#### Acceptance Criteria

1. THE `packaging/service-map.yaml` SHALL register all seven harness processes as native Windows services with start order: Kafka → MySQL → Redis → `Pacs.Fas.Api` → `Pacs.Loans.Api` → `Pacs.SyncWorker` → `Pacs.OperatorUi`.
2. THE `packaging/installer-manifest-stub.yaml` SHALL define the harness payload entry compatible with the installer's `release-manifest.yaml` format.
3. WHEN the installer runs with `--demo` flag, THE installer SHALL also install the NLDR-side services (`Nldr.Api`, `Nldr.SyncWorker`, `Nldr.DashboardUi`) on the same machine.
4. WHEN the installer runs without `--demo` flag, THE installer SHALL install only the PACS-side services; the NLDR URL SHALL be configured via `Pacs:NldrBaseUrl` in the generated `appsettings.Production.json`.
5. WHEN the installer completes, THE installer SHALL automatically run the `demo-happy-path` scenario as a smoke test and report PASS/FAIL.
6. THE installer SHALL set `Harness:TestMode=false` and `Harness:Profile="Installer"` in the generated `appsettings.Production.json`.
7. THE harness SHALL support self-contained publish (`dotnet publish -r win-x64 --self-contained`) for all seven source projects so that no .NET runtime is required on the target machine.
8. THE `packaging/error-catalog/harness.yaml` SHALL be included in the installer payload and SHALL be loaded by `IErrorFactory` at runtime.

*Traces to: G-14; Tests: Demo F (§21.6 of design), M12 milestone acceptance.*

---

### Requirement 23: Multi-PACS Profile

**User Story:** As a QA / Test Engineer, I want a multi-PACS profile that runs two independent PACS instances (PACS-AP-0001 and PACS-AP-0002) against the same Kafka cluster and NLDR, so that I can prove sequence isolation between PACS nodes and test wrong-PACS-ID startup rejection.

#### Acceptance Criteria

1. WHEN `Harness:Profile="Multi-Pacs"`, THE harness SHALL run a second set of PACS services configured for `PACS-AP-0002` with its own MySQL schema `epacs_pacs_0002` and its own `sync_sequence` rows.
2. THE `received_event` table at NLDR SHALL store events from both PACS nodes distinguished by `pacs_id`, with `UNIQUE KEY uq_pacs_seq (pacs_id, sequence_no)` ensuring sequence isolation per node.
3. FOR ANY two events from different PACS nodes, THE `received_event` table SHALL allow the same `sequence_no` value as long as `pacs_id` differs (sequence isolation property, invariant I-4).
4. WHEN a PACS process starts with a `Pacs:PacsId` that does not match the `pacs_id` in the `sync_sequence` table, THE process SHALL fail startup with error code `ERP-PACS-INS-0005`.
5. THE `Pacs:PacsId` configuration value SHALL be validated at startup against the regex `^PACS-[A-Z]{2}-\d{4}$`; IF the value does not match, THE process SHALL fail startup with `ERP-PACS-INS-0005`.
6. THE `scripts/seed-multi-pacs.ps1` SHALL configure the second PACS profile and verify that both PACS nodes can sync independently to NLDR without sequence collision.

*Traces to: G-11; Tests: SEQ-009, SEQ-010.*

---

### Requirement 24: Observability and Structured Logging

**User Story:** As a QA / Test Engineer, I want every service to emit structured logs with correlation IDs, operation scopes, and checkpoint markers — and to propagate correlation across HTTP and Kafka boundaries — so that any event can be traced end-to-end through the entire system using a single correlation ID.

#### Acceptance Criteria

1. EVERY service SHALL use `IAppLogger<T>` (not raw `ILogger<T>`) for all log output.
2. EVERY API entry point SHALL call `_logger.BeginOperation(module, feature, operation)` to establish a structured operation scope.
3. EVERY persistence boundary and fault hook site SHALL call `_logger.Checkpoint(name, data)` with relevant context data.
4. THE `Observability.AspNetCore.UseCorrelationId()` middleware SHALL be registered in all API projects to read or generate a `X-Correlation-Id` header on every request.
5. THE `CorrelationDelegatingHandler` SHALL be registered on all outgoing `HttpClient` instances to propagate `X-Correlation-Id` on every outgoing HTTP call.
6. THE `Observability.Propagation.KafkaHeaders` SHALL be used to write and read correlation headers (`correlationId`, `causationId`, `eventId`, `pacsId`) on every Kafka message.
7. EVERY `TraceableBackgroundService` derivative SHALL scope each work item (consumed message or outbox row) inside a correlation scope tied to the inbound event's `correlationId`.
8. NO business handler SHALL call `Guid.NewGuid()` or `DateTime.UtcNow` directly; correlation IDs SHALL come from the middleware or `EventIdProvider`, and timestamps SHALL come from `IClock`.
9. THE `Observability.AspNetCore.UseGlobalExceptionHandler()` middleware SHALL be registered in all API projects to convert unhandled exceptions to structured error responses using `IErrorFactory`.
10. THE `Observability.AspNetCore.UseRequestLogging()` middleware SHALL be registered in all API projects to log request/response metadata at the Information level.

*Traces to: G-15, G-23; Tests: CRIT-020, UI-005.*

---

### Requirement 25: Health Checks

**User Story:** As a QA / Test Engineer, I want every service to expose `/health/ready` and `/health/live` endpoints with per-dependency status, so that Docker Compose, the installer, and automated tests can determine service readiness without parsing logs.

#### Acceptance Criteria

1. THE `Pacs.Fas.Api` and `Pacs.Loans.Api` SHALL expose `/health/ready` checking MySQL_PACS (unhealthy if down), Redis_PACS (degraded if down), and Kafka (degraded if down — not unhealthy, per Engineer Guide §6).
2. THE `Pacs.SyncWorker` SHALL expose `/health/ready` checking MySQL_PACS (unhealthy), Kafka (unhealthy), Redis (degraded), and NLDR HTTP probe (degraded if down).
3. THE `Nldr.Api` and `Nldr.SyncWorker` SHALL expose `/health/ready` checking MySQL_NLDR (unhealthy), Kafka (unhealthy), and Redis_NLDR (degraded).
4. THE `Pacs.OperatorUi` SHALL expose `/health/ready` checking `Pacs.Fas.Api` (degraded if down), `Pacs.Loans.Api` (degraded if down), and Redis_PACS (degraded if down).
5. THE `Nldr.DashboardUi` SHALL expose `/health/ready` checking `Nldr.Api` (degraded if down).
6. ALL services SHALL expose `/health/live` returning HTTP 200 whenever the process is running, regardless of dependency health.
7. THE `Pacs.OperatorUi` top bar SHALL display a coloured health badge per dependency (green/yellow/red) that updates on each status poll.
8. THE health check implementations SHALL use `Harness.Common.Health` builders and SHALL read all connection strings and URLs from configuration.

*Traces to: G-16; Tests: FAIL-007, CRIT-005.*

---

### Requirement 26: Performance Requirements

**User Story:** As a QA / Test Engineer, I want the harness to meet defined performance SLOs under sustained load, so that I can certify the sync architecture is fit for production use at PACS nodes with realistic event volumes.

#### Acceptance Criteria

1. WHEN draining a backlog of 10,000 events on a developer laptop, THE `OutboundRelayService` SHALL sustain a throughput of at least `Performance:Targets:MinDrainEventsPerMinute` (default 500) events per minute.
2. WHEN draining on a Hyper-V test VM with 4 vCPU and 8 GB RAM, THE `OutboundRelayService` SHALL sustain at least 1,500 events per minute.
3. THE p99 INSERT latency for `sync_outbox` rows SHALL be less than `Performance:Targets:MaxInsertLatencyP99Ms` (default 50) milliseconds under normal load.
4. AFTER a backlog of events accumulates, THE Kafka consumer lag SHALL return to zero within `Performance:Targets:MaxKafkaLagReturnMinutes` (default 5) minutes.
5. THE warm Redis cache lookup p95 latency SHALL be less than `Performance:Targets:WarmCacheP95Ms` (default 5) milliseconds; the cold (cache-miss) p95 latency SHALL be less than `Performance:Targets:ColdCacheP95Ms` (default 50) milliseconds.
6. DURING an 8-hour soak test with simulated network flapping, THE heap growth of each service SHALL be less than `Performance:Targets:MaxHeapGrowthMb` (default 50) MB and no `sync_outbox` row SHALL remain stuck in `IN_FLIGHT` status.
7. THE `sync_outbox` table size after 30 days of simulated activity SHALL be less than `Performance:Targets:MaxDbSizeGb` (default 8) GB.
8. ALL performance targets SHALL be read from `Performance:Targets:*` configuration; no numeric SLO SHALL be hardcoded in test assertions.

*Traces to: G-28; Tests: PERF-001..006.*

---

### Requirement 27: Non-Functional Requirements

**User Story:** As a Release Engineer, I want the harness to meet non-functional requirements for resilience, security, maintainability, and offline operation, so that it can serve as a credible proof of the production ePACS architecture.

#### Acceptance Criteria

1. THE harness SHALL operate fully offline after initial Docker image pull; no runtime internet access SHALL be required.
2. THE harness SHALL be self-contained for Windows (win-x64) deployment; no .NET runtime SHALL be required on the target machine for installer-packaged deployments.
3. EVERY long-running operation (outbox drain, file upload, reconciliation) SHALL be resumable from the last checkpoint after a hard power cut (`innodb_flush_log_at_trx_commit=1` enforced in MySQL configuration).
4. THE Redis cache SHALL be configured with `AbortOnConnectFail=false` in all services so that a Redis outage never causes a business operation to fail.
5. THE harness SHALL use `write-then-rename` for all atomic file operations (staging → queue path) to prevent partial writes.
6. ALL binaries produced by the harness SHALL be Authenticode-signed when built in CI for installer-packaged deployments.
7. THE `Harness:TestMode=false` configuration SHALL be the default in all `appsettings.json` files; TestMode SHALL only be enabled explicitly in development and test environments.
8. THE harness SHALL NOT store raw governance override tokens; only the SHA-256 hash SHALL be stored in configuration.
9. THE harness SHALL NOT log raw PII values; all PII fields SHALL be annotated with `[Sensitive]`, `[DoNotLog]`, or `[Mask]` and the redaction engine SHALL be enabled in all environments.
10. THE harness code coverage SHALL meet the minimum thresholds defined in `Directory.Build.props` (recommended: 80% line coverage for `Harness.Common`, 70% for API projects).

*Traces to: I-1, I-2, G-17; Tests: all test projects.*


---

### Requirement 28: Correctness Properties (Property-Based Testing)

**User Story:** As a QA / Test Engineer, I want the harness to include property-based tests for all core invariants — canonicalization, hashing, sequence allocation, idempotency, three-witness audit, PII redaction, and reconciliation — so that edge cases discovered by random input generation are caught before integration testing.

#### Acceptance Criteria

1. THE `Harness.ContractTests` project SHALL include a property test verifying that for any object `x` with any permutation of key insertion order, `CanonicalJsonWriter.Serialize(x)` produces the same string (canonicalization determinism).
2. THE `Harness.ContractTests` project SHALL include a round-trip property test verifying that for any valid `EventEnvelope` `e`, `EventEnvelope.Parse(e.Serialize())` produces an envelope equal to `e`.
3. THE `Harness.ContractTests` project SHALL include a property test verifying that for any valid envelope `e`, mutating any field in `{ payload, beforeState, amendmentMeta }` produces a different `PayloadHasher` output (tamper detection property).
4. THE `Harness.ContractTests` project SHALL include a property test verifying that for any N concurrent `SequenceAllocator.GetNextAsync` calls with the same `(pacs_id, stream_name)`, the resulting set of sequence numbers is `{base, base+1, ..., base+N-1}` with no gaps or duplicates (monotonic contiguous property).
5. THE `Harness.ContractTests` project SHALL include a property test verifying that for any event replayed N times with the same `event_id`, the `SyncInboxStore` returns `DUPLICATE` for all calls after the first (idempotency property).
6. THE `Harness.ContractTests` project SHALL include a property test verifying that for any DTO with `[Sensitive]`, `[DoNotLog]`, or `[Mask]` annotated fields, the serialised log output does not contain the raw field value (PII redaction property).
7. THE `Harness.ContractTests` project SHALL include a property test verifying that for any sequence of ACKs received in any order, `sync_checkpoints.last_acked_sequence` equals the highest contiguous sequence number from the base (checkpoint correctness property).
8. THE `Harness.ContractTests` project SHALL include a property test verifying that for any set of events where PACS outbox and NLDR received-event are in perfect sync, `ReconciliationRunner` reports `status='PASS'` with zero gaps, mismatches, orphans, and duplicates.
9. THE `Harness.ContractTests` project SHALL include a property test verifying that for any amendment request where `approver == currentUser`, `Pacs.Loans.Api` returns `422` (maker-checker enforcement property).
10. THE `Harness.ContractTests` project SHALL include a property test verifying that for any file of any size, `FileChunkUploaderService` resumes from `chunks_acked + 1` after a simulated crash and the final `chunks_acked` equals `total_chunks` (file resume property).
11. THE `Harness.ContractTests` project SHALL include a property test verifying that for any two events from different `pacs_id` values, the `received_event` table allows the same `sequence_no` without a unique constraint violation (sequence isolation property).
12. THE `Harness.ContractTests` project SHALL include a property test verifying that for any valid `(pacsId, entityType, entityId, changeType, timestamp)` tuple, `IdempotencyKey.Format(inputs)` produces a string that `IdempotencyKey.Parse` can decompose back to the original components (round-trip property).

*Traces to: I-2, I-3, I-4, I-5, G-05, G-06; Tests: Harness.ContractTests.*

---

### Requirement 29: Error Catalog

**User Story:** As a Backend Developer, I want all error codes defined in a single YAML catalog (`packaging/error-catalog/harness.yaml`) that `IErrorFactory` loads at startup, so that every error has a consistent HTTP status code, category, operator-facing message, and severity — and no error code is ever hardcoded in application code.

#### Acceptance Criteria

1. THE `packaging/error-catalog/harness.yaml` SHALL define all `ERP-PACS-*` error codes: `ERP-PACS-VAL-0001` through `ERP-PACS-VAL-0010`, `ERP-PACS-GOV-0001`, `ERP-PACS-INS-0001` through `ERP-PACS-INS-0005`, `ERP-PACS-SYN-0001`, and `ERP-PACS-HLT-0010`.
2. THE `packaging/error-catalog/harness.yaml` SHALL define all `ERP-NLDR-*` error codes: `ERP-NLDR-VAL-0001` through `ERP-NLDR-VAL-0007`, `ERP-NLDR-SEC-0001` through `ERP-NLDR-SEC-0002`.
3. EACH error code entry SHALL specify: `http` (HTTP status code), `category` (VAL/GOV/INS/SYN/HLT/SEC), `operator` (human-readable message), and `severity` (Warning/Error/Critical).
4. THE `IErrorFactory.FromCatalog(code, contextMessage)` SHALL be the only mechanism for creating typed exceptions in the harness; direct `throw new Exception(...)` SHALL NOT appear in business code.
5. WHEN an unknown error code is passed to `IErrorFactory.FromCatalog`, THE factory SHALL throw an `InvalidOperationException` at startup (fail-fast) rather than silently returning a generic error at runtime.
6. THE error catalog format SHALL be compatible with the existing `packaging/error-catalog/core.yaml` and `packaging/error-catalog/installer.yaml` files in the repository.

*Traces to: G-24; Tests: all test projects.*

---

### Requirement 30: Milestone Acceptance Criteria

**User Story:** As a Release Engineer, I want each build milestone to have clear, independently demoable acceptance criteria, so that progress can be tracked and each milestone can be signed off before the next begins.

#### Acceptance Criteria

1. THE M0 (Skeleton) milestone SHALL be complete when: `dotnet build ePACS.SyncHarness.sln` is clean, `Harness.ContractTests` passes without infrastructure, all `*Options.cs` classes are defined, `harness.yaml` is populated, and the Docker Compose minimal profile starts all infrastructure services.
2. THE M1 (Happy Path) milestone SHALL be complete when: `demo-happy-path` scenario passes end-to-end, CRIT-001 and SYNC-POS-001 evidence folders are produced, and the voucher timeline drill-down shows all six artefacts linked by `correlation_id`.
3. THE M2 (Sync Invariants) milestone SHALL be complete when: CRIT-003, CRIT-009, and SEQ-001..007 pass with evidence.
4. THE M3 (Offline + Reconnect) milestone SHALL be complete when: `demo-offline-reconnect` passes and OFF-001..005 pass with evidence.
5. THE M4 (Power-Cut Robustness) milestone SHALL be complete when: all `demo-power-cut-*` scenarios pass and PWR-001..006 pass with evidence.
6. THE M5 (Delete + Amendment) milestone SHALL be complete when: CRIT-011, CRIT-012, and CRIT-013 pass with three-witness evidence.
7. THE M6 (Security + Tamper) milestone SHALL be complete when: SEC-001..008, CRIT-010, and CRIT-020 pass with evidence.
8. THE M7 (File Sync) milestone SHALL be complete when: SYNC-POS-010, OFF-006, PWR-008, and NEG-020 pass with evidence.
9. THE M8 (Long Offline + Drift) milestone SHALL be complete when: CRIT-017, CRIT-018, SEQ-011 pass and PERF-001..002 are within budget.
10. THE M9 through M13 milestones SHALL be complete when their respective test cases pass as defined in §29 of the design.
11. THE final exit criteria SHALL be met when: all 100+ test cases from `ePACS_Sync_Test_Cases_and_Simulation_Plan_v1.0` have a PASS result in the evidence folder, the reconciliation report shows PASS, and the support bundle scanner reports zero PII leaks.

*Traces to: G-27; Tests: all milestones.*

