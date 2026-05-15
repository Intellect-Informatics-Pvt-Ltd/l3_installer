# Design Document — ePACS Sync Test Harness

> Authoritative design for the ePACS Sync Test Harness (v1.0). Based on design overview v2.0 (docs/test-harness/00-design-overview.md) and requirements.md. A developer implementing any component should be able to do so from this document alone.

---

## 1. Overview

The ePACS Sync Test Harness is a complete .NET 8 simulation environment that proves the offline-first ePACS synchronisation architecture end-to-end. It exercises all 100+ test cases from the Test Plan v1.0, validates the five non-negotiable invariants (I-1 through I-5), and integrates with the offline installer for pilot-site demos.

### 1.1 Five Non-Negotiable Invariants

| # | Invariant | Enforced by |
|---|---|---|
| I-1 | Local MySQL is source of truth | Dapper-only writes; no ORM magic |
| I-2 | Business row and sync_outbox row commit or roll back together | SequenceAllocator + SyncOutboxWriter inside same DbTransaction |
| I-3 | Same event_id/idempotency_key produces exactly one business effect | SyncInboxStore DUPLICATE detection; UNIQUE KEY uq_event |
| I-4 | Sequence numbers are monotonically increasing and contiguous per (pacs_id, stream_name) | SequenceAllocator UPDATE+read inside same tx; UNIQUE KEY uq_pacs_seq |
| I-5 | Payload hash mismatch is rejected; envelope is tamper-evident | PayloadHasher + Nldr.Api 12-step pipeline step 5 |
