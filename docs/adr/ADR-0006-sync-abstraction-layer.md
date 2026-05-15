# ADR-0006: Sync Abstraction Layer (Transactional Outbox + Kafka)

**Status:** Accepted  
**Date:** 2025-12-01  
**Deciders:** Architecture team

---

## Context

ePACS PACS nodes operate offline for days/weeks. Business data must eventually sync to the
central NLDR with guarantees:
- No data loss (even across power-cuts)
- Exactly-once delivery semantics (idempotent receiver)
- Ordered delivery per PACS per stream
- Tamper-evident envelopes

## Decision

Use the **transactional outbox pattern** with Kafka as the transport layer.

## Design

1. Business write + `sync_outbox` row + sequence allocation happen in **one MySQL transaction**
2. A background worker (`SyncWorker`) polls the outbox and publishes to Kafka
3. The NLDR consumes from Kafka, validates the envelope, and applies the business state
4. ACK/NACK flows back via a separate Kafka topic
5. Checkpoints track the last ACKed sequence for resumability

## Rationale

- Transactional outbox guarantees atomicity between business state and sync intent (I-2)
- Kafka provides durable, ordered delivery with consumer group semantics
- Sequence numbers enable gap detection and contiguous checkpoint advancement (I-4)
- SHA-256 payload hashing enables tamper detection (I-5)
- Idempotency keys enable exactly-once semantics at the receiver (I-3)

## Alternatives Considered

| Alternative | Why rejected |
|---|---|
| CDC (Debezium) | Requires Kafka Connect; complex setup for single-node |
| Polling from NLDR | NLDR can't reach offline PACS nodes |
| File-based sync (rsync) | No ordering guarantees; no atomicity with business writes |
| Direct HTTP push | No durability if NLDR is down; no replay capability |

## Consequences

- Every business write must include the outbox write in the same transaction
- The `SyncWorker` is a critical service — must be monitored and auto-restarted
- Sequence allocation adds one extra UPDATE per transaction (acceptable overhead)
- The outbox table grows until ACKed rows are archived (90-day retention default)
