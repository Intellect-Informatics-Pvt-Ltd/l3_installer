# ePACS Sync Test Harness — Tester's Guide

> Comprehensive handoff document for QA engineers testing the ePACS offline-first sync architecture.
> Supplements `docs/ePACS_Test_Engineer_Guide_Offline_Sync_v1.0.docx` with concrete execution steps.

**Version:** 1.0  
**Date:** 2026-05-15  
**Audience:** QA engineers, test automation engineers, pilot-site testers

---

## Table of Contents

1. [Overview — Why This Testing Is Different](#1-overview--why-this-testing-is-different)
2. [What Is Being Tested](#2-what-is-being-tested)
3. [Environment Setup — Docker Path](#3-environment-setup--docker-path)
4. [Environment Setup — Native EXE Path](#4-environment-setup--native-exe-path)
5. [Test Execution Sequence](#5-test-execution-sequence)
6. [Complete Test Case Matrix](#6-complete-test-case-matrix)
7. [Positive Test Cases — Execution & Expected Results](#7-positive-test-cases--execution--expected-results)
8. [Negative Test Cases — Execution & Expected Results](#8-negative-test-cases--execution--expected-results)
9. [Failure & Resilience Tests](#9-failure--resilience-tests)
10. [Power-Cut Tests](#10-power-cut-tests)
11. [Security Tests](#11-security-tests)
12. [Performance Tests](#12-performance-tests)
13. [Building the Test Matrix](#13-building-the-test-matrix)
14. [Evidence Collection](#14-evidence-collection)
15. [Gotchas, Pitfalls & Known Issues](#15-gotchas-pitfalls--known-issues)
16. [Glossary](#16-glossary)

---

## 1. Overview — Why This Testing Is Different

### 1.1 This is NOT regular API testing

Traditional testing verifies: "I send a request, I get a response, the database has the right data."

**This harness tests something fundamentally harder:**

- Does the system **survive power loss** mid-transaction and recover correctly?
- Does the system **queue work for days** while offline and drain it correctly on reconnect?
- Does the system **detect tampering** of data in transit?
- Does the system **guarantee exactly-once delivery** even when the network drops ACKs?
- Does the system **maintain ordering** across thousands of events from multiple producers?

### 1.2 The mental model

Think of a PACS node as a **bank branch in a village with no internet**. It operates independently for days or weeks. When connectivity returns (even briefly), it must sync all accumulated transactions to the central NLDR without losing, duplicating, or reordering anything.

**Every test must verify not just "did it work?" but "did it work AND leave the system in a provably correct state?"**

### 1.3 The five invariants every test must respect

| # | Invariant | What breaks if violated |
|---|---|---|
| **I-1** | Local MySQL is the source of truth | Business data lost or corrupted |
| **I-2** | Business write + outbox write are atomic (same transaction) | Events lost — NLDR never sees the transaction |
| **I-3** | Same event processed exactly once at receiver | Duplicate transactions at central |
| **I-4** | Sequence numbers are monotonic and contiguous | Gap detection fails, ordering lost |
| **I-5** | Payload hash mismatch = rejected | Tampered data accepted silently |

### 1.4 What makes this testing unique

| Aspect | Regular Testing | This Harness |
|--------|----------------|--------------|
| Failure mode | Unexpected | **Deliberately injected** via fault hooks |
| Network | Always available | **Offline for days**, then reconnect |
| Power | Always on | **Hard power-off** mid-transaction |
| Time | Real-time | **Simulated 30-day gaps** via clock manipulation |
| Verification | Response code | **Multi-table cross-system state assertion** |
| Evidence | Test report | **Folder with DB dumps, logs, Kafka offsets** |

---

## 2. What Is Being Tested

### 2.1 Offline Behaviour (PACS operates without NLDR)

- Business operations continue normally (create/update/delete vouchers, loan applications)
- Events accumulate in `sync_outbox` with correct sequence numbers
- UI shows "Offline" banner but remains fully functional
- Redis cache flush does not affect business operations
- Kafka unavailability does not block business writes

### 2.2 Online Behaviour (PACS reconnects to NLDR)

- Backlog drains in priority order (financial first, then files, then heartbeat)
- NLDR validates every envelope (hash, sequence, schema)
- ACKs flow back and advance checkpoints
- Duplicate events are detected and deduplicated
- Gaps trigger reconciliation requests
- Files resume from last acknowledged chunk

### 2.3 Boundary Conditions

- Power-cut at every checkpoint (13 fault hooks)
- Clock drift detection and sync pause
- Tampered payloads rejected
- Amendments without approver rejected
- Bulk deletes without governance token rejected
- Multi-PACS sequence isolation

---

## 3. Environment Setup — Docker Path

### 3.1 Prerequisites

| Tool | Version | Verify with |
|------|---------|-------------|
| .NET 8 SDK | 8.x | `dotnet --version` |
| Docker Desktop | 4.x+ | `docker --version` |
| Docker Compose | v2 | `docker compose version` |
| curl | any | `curl --version` |
| jq | any | `jq --version` (optional, for pretty JSON) |
| MySQL client | any | `mysql --version` (optional, for DB inspection) |

### 3.2 Initial Setup (one-time)

```bash
# Clone and navigate
cd harness

# Restore NuGet packages
dotnet restore ePACS.SyncHarness.sln

# Build the solution
dotnet build ePACS.SyncHarness.sln

# Start infrastructure (Kafka + MySQL × 2 + Redis × 2)
docker compose -f docker/docker-compose.minimal.yml up -d

# Wait for containers to be healthy (~30 seconds)
docker compose -f docker/docker-compose.minimal.yml ps
# All should show "healthy" status
```

### 3.3 Starting Services (in order)

Open 4 terminal windows:

```bash
# Terminal 1: PACS FAS API
dotnet run --project src/Pacs.Fas.Api

# Terminal 2: NLDR API
dotnet run --project src/Nldr.Api

# Terminal 3: PACS Sync Worker (outbox relay + ACK consumer)
dotnet run --project src/Pacs.SyncWorker

# Terminal 4: NLDR Sync Worker (ACK publisher)
dotnet run --project src/Nldr.SyncWorker
```

### 3.4 Verify All Services Are Healthy

```bash
curl -s http://localhost:5101/health/ready | jq .   # Pacs.Fas.Api
curl -s http://localhost:5201/health/ready | jq .   # Nldr.Api
curl -s http://localhost:5103/health/ready | jq .   # Pacs.SyncWorker
curl -s http://localhost:5203/health/ready | jq .   # Nldr.SyncWorker
```

All should return `{"status":"Healthy"}`.

### 3.5 Resetting Between Test Runs

```bash
# Full reset: drop DBs, flush Redis, delete Kafka topics, remigrate, reseed
bash scripts/reset-lab.sh

# Quick reset (keeps infra, just clears data):
bash scripts/reset-lab.sh quick
```

**CRITICAL:** Always reset between test categories. Never run security tests on data from positive tests.

### 3.6 Connecting to Databases (for verification)

```bash
# PACS MySQL
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs

# NLDR MySQL
mysql -h 127.0.0.1 -P 3308 -u root -proot epacs_nldr

# Redis (PACS)
redis-cli -p 6380

# Redis (NLDR)
redis-cli -p 6381
```

---

## 4. Environment Setup — Native EXE Path

### 4.1 Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| Windows 10/11 Pro x64 | Build 17763+ | Target machine |
| MySQL 8.4 | Installed as service | Port 3306 |
| Microsoft Garnet | Installed as service | Port 6379 |
| Apache Kafka 3.7 | Installed as service (KRaft) | Port 9092 |
| Eclipse Temurin JRE 17 | For Kafka | — |
| Harness EXEs | Published via `publish-win-x64.ps1` | — |

### 4.2 Publishing the EXEs

```powershell
# On the build machine (with .NET 8 SDK)
cd harness
.\scripts\publish-win-x64.ps1 -CreateZip

# Copy publish/harness-pacs-win-x64.zip to the target machine
# Copy publish/harness-nldr-win-x64.zip if running demo mode
```

### 4.3 Installing via the Offline Installer

```powershell
# On the target Windows machine
Installer.CLI.exe /quiet /config:D:\site-config.epcfg /mode:install /demo

# This will:
# 1. Extract all payloads (MySQL, Garnet, Kafka, JRE, harness EXEs)
# 2. Generate appsettings.Production.json from .epcfg
# 3. Register all services as Windows services
# 4. Start services in dependency order
# 5. Run smoke test (health checks)
```

### 4.4 Manual Installation (without installer)

```powershell
# 1. Extract harness-pacs-win-x64.zip to C:\Program Files\ePACS\current\harness\
# 2. Copy appsettings.Installer.json to same directory as appsettings.Production.json
# 3. Edit connection strings to match your MySQL/Redis/Kafka setup
# 4. Register services:
sc.exe create "ePACS.Harness.FasApi" binPath= "C:\Program Files\ePACS\current\harness\Pacs.Fas.Api.exe --urls http://127.0.0.1:5101" start= auto
sc.exe create "ePACS.Harness.SyncWorker" binPath= "C:\Program Files\ePACS\current\harness\Pacs.SyncWorker.exe" start= auto
# ... repeat for other services

# 5. Start services
sc.exe start "ePACS.Harness.FasApi"
sc.exe start "ePACS.Harness.SyncWorker"
```

### 4.5 Verify Native Installation

```powershell
# Check services are running
sc.exe query "ePACS.Harness.FasApi" | findstr STATE
sc.exe query "ePACS.Harness.SyncWorker" | findstr STATE

# Health checks
Invoke-RestMethod http://127.0.0.1:5101/health/ready
Invoke-RestMethod http://127.0.0.1:5201/health/ready
```

### 4.6 Key Differences: Docker vs Native

| Aspect | Docker Path | Native EXE Path |
|--------|-------------|-----------------|
| MySQL port | 3307 (PACS), 3308 (NLDR) | 3306 (shared instance, separate DBs) |
| Redis port | 6380 (PACS), 6381 (NLDR) | 6379 (shared, DB isolation) |
| Kafka port | 9092 | 9092 |
| Reset method | `bash scripts/reset-lab.sh` | `reset-lab.ps1` or manual SQL |
| TestMode | true (default) | false (installer) or true (demo) |
| Fault injection | Always available | Only in demo mode (`/demo` flag) |
| Power-cut testing | `docker stop` / `docker kill` | `Stop-VM -TurnOff` or pull power |
| Log location | stdout (Docker logs) | `D:\ePACSData\logs\harness\` |

---

## 5. Test Execution Sequence

### 5.1 Recommended Order

Execute test categories in this order. Each category builds on the confidence established by the previous one.

```
1. SYNC-POS (Positive sync)     ← Proves happy path works
2. SEQ (Sequence integrity)     ← Proves ordering is correct
3. OFF (Offline behaviour)      ← Proves offline operation is safe
4. FAIL (Failure handling)      ← Proves retry/circuit breaker work
5. PWR (Power-cut resilience)   ← Proves crash recovery works
6. CRIT (Critical scenarios)    ← Proves edge cases are handled
7. SEC (Security)               ← Proves tamper detection works
8. NEG (Negative/rejection)     ← Proves invalid input is rejected
9. PERF (Performance)           ← Proves SLOs are met
10. BAK (Backup integration)    ← Proves backup hooks work
11. UI (UI behaviour)           ← Proves operator experience
```

### 5.2 Before Each Category

1. Reset the lab: `bash scripts/reset-lab.sh`
2. Verify all services healthy
3. Note the starting state: `SELECT COUNT(*) FROM sync_outbox; SELECT COUNT(*) FROM received_event;`

### 5.3 After Each Test

1. Collect evidence (see §14)
2. Verify invariants I-1 through I-5 still hold
3. Document any unexpected behaviour

---

## 6. Complete Test Case Matrix

### 6.1 Test ID Prefixes

| Prefix | Category | Count | Invariants Tested |
|--------|----------|-------|-------------------|
| SYNC-POS | Positive sync (happy path) | 10 | I-1, I-2, I-4 |
| SEQ | Sequence integrity | 12 | I-4 |
| OFF | Offline behaviour | 6 | I-1, I-2 |
| FAIL | Failure handling (retry, circuit breaker) | 10 | I-1, I-3 |
| PWR | Power-cut resilience | 8 | I-1, I-2 |
| CRIT | Critical edge cases | 20 | All |
| SEC | Security (tamper, auth) | 8 | I-5 |
| NEG | Negative (rejection) | 20 | I-3, I-4, I-5 |
| PERF | Performance SLOs | 6 | — |
| BAK | Backup integration | 6 | I-1 |
| UI | UI behaviour | 5 | — |

**Total: 111 test cases**

---

## 7. Positive Test Cases — Execution & Expected Results

### SYNC-POS-001: Create voucher → outbox → NLDR ingest → ACK

**Steps:**
```bash
# 1. Create a voucher
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-2026-00001","voucherDate":"2026-05-15","voucherType":"CR","narration":"Test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":5000}]}' | jq .

# 2. Wait 2 seconds for relay + ACK cycle
sleep 2

# 3. Verify outbox status
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT outbox_id, sequence_no, status, event_id FROM sync_outbox ORDER BY outbox_id DESC LIMIT 1;"

# 4. Verify NLDR received it
mysql -h 127.0.0.1 -P 3308 -u root -proot epacs_nldr -e \
  "SELECT event_id, apply_status, sequence_no FROM received_event ORDER BY received_id DESC LIMIT 1;"
```

**Expected:**
- Step 1: HTTP 201 with `voucherId` in response
- Step 3: `status = 'ACKED'`, `sequence_no = 1`
- Step 4: `apply_status = 'APPLIED'`, same `event_id` as step 3

### SYNC-POS-002: Update voucher → outbox → NLDR applies update

**Steps:**
```bash
# 1. Create voucher (get the ID from response)
VOUCHER_ID=$(curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-2026-00002","voucherDate":"2026-05-15","voucherType":"DB","narration":"Original","createdBy":"admin","lines":[{"accountCode":"2001","debitAmount":1000,"creditAmount":0}]}' | jq -r '.voucherId')

sleep 2

# 2. Update the voucher
curl -s -X PUT http://localhost:5101/api/vouchers/$VOUCHER_ID \
  -H "Content-Type: application/json" \
  -d '{"narration":"Updated narration","totalAmount":1000}' | jq .

sleep 2

# 3. Verify two outbox entries (INSERT + UPDATE)
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT sequence_no, change_type, status FROM sync_outbox ORDER BY sequence_no;"
```

**Expected:**
- Two rows: sequence 1 (INSERT, ACKED) and sequence 2 (UPDATE, ACKED)
- NLDR `received_event` has both rows with `apply_status = 'APPLIED'`

### SYNC-POS-003: Delete voucher → before-state captured → NLDR soft-deletes

**Steps:**
```bash
# 1. Create, then delete
VOUCHER_ID=$(curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-2026-00003","voucherDate":"2026-05-15","voucherType":"JV","narration":"To delete","createdBy":"admin","lines":[{"accountCode":"3001","debitAmount":500,"creditAmount":0}]}' | jq -r '.voucherId')

sleep 2

curl -s -X DELETE http://localhost:5101/api/vouchers/$VOUCHER_ID \
  -H "Content-Type: application/json" \
  -d '{"reason":"Test deletion"}' | jq .

sleep 2

# 2. Verify before-state was captured
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT deletion_id, before_state_json IS NOT NULL as has_before_state FROM voucher_deletion_audit;"

# 3. Verify NLDR soft-deleted (not hard-deleted)
mysql -h 127.0.0.1 -P 3308 -u root -proot epacs_nldr -e \
  "SELECT voucher_id, is_deleted, deleted_at FROM nldr_business_voucher WHERE voucher_no='VCH-2026-00003';"
```

**Expected:**
- `voucher_deletion_audit` has a row with `has_before_state = 1`
- NLDR: `is_deleted = 1`, `deleted_at` is set (NOT physically deleted)
- Outbox: DELETE event with `before_state_json` populated

### SYNC-POS-004: Loan amendment → three-witness audit

**Steps:**
```bash
# 1. Create loan application
LOAN_ID=$(curl -s -X POST http://localhost:5102/api/loan-applications \
  -H "Content-Type: application/json" \
  -d '{"loanAppNo":"LA-2026-00001","memberNo":"M001","memberName":"Test Member","requestedAmount":50000,"purpose":"Agriculture","maker":"user1"}' | jq -r '.loanAppId')

sleep 2

# 2. Amend the loan
curl -s -X POST http://localhost:5102/api/loan-applications/$LOAN_ID/amend \
  -H "Content-Type: application/json" \
  -H "X-Test-User: checker1" \
  -H "X-Test-Role: checker" \
  -d '{"reason":"Amount correction","approver":"manager1","fields":{"requestedAmount":75000}}' | jq .

sleep 2

# 3. Verify three witnesses
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT 'sync_outbox' as witness, COUNT(*) as present FROM sync_outbox WHERE change_type='AMENDMENT'
   UNION ALL
   SELECT 'amendment_history', COUNT(*) FROM loan_amendment_history WHERE loan_app_id=$LOAN_ID;"
```

**Expected:**
- Three witnesses all present: `sync_outbox` (AMENDMENT), `loan_amendment_history`, and traceability audit row
- Amendment has `reason` and `approver` populated

---

## 8. Negative Test Cases — Execution & Expected Results

### NEG-007: DELETE without before-state → rejected

**Steps:**
```bash
# Attempt to send a DELETE event directly to NLDR without beforeState
curl -s -X POST http://localhost:5201/api/sync/ingest \
  -H "Content-Type: application/json" \
  -d '{
    "schemaVersion":"1.0",
    "eventId":"'$(uuidgen)'",
    "correlationId":"test-neg-007",
    "pacsId":"PACS-AP-0001",
    "sourceSystem":"PACS",
    "targetSystem":"NLDR",
    "sequenceNo":9999,
    "streamName":"pacs.outbound",
    "idempotencyKey":"PACS-AP-0001:voucher:99:DELETE:2026-05-15T00:00:00Z",
    "changeType":"DELETE",
    "entityType":"voucher",
    "entityId":"99",
    "payload":null,
    "beforeState":null,
    "payloadHash":"0000000000000000000000000000000000000000000000000000000000000000",
    "createdAtUtc":"2026-05-15T00:00:00Z"
  }' -w "\nHTTP Status: %{http_code}\n"
```

**Expected:**
- HTTP 422
- Error code: `ERP-NLDR-VAL-0006`
- Message: "DELETE event is missing the mandatory beforeState field"
- `received_event.apply_status = 'REJECTED'`

### NEG-009: Amendment without approver → rejected at API boundary

**Steps:**
```bash
# Create a loan first
LOAN_ID=$(curl -s -X POST http://localhost:5102/api/loan-applications \
  -H "Content-Type: application/json" \
  -d '{"loanAppNo":"LA-2026-NEG009","memberNo":"M002","memberName":"Test","requestedAmount":10000,"purpose":"Test","maker":"user1"}' | jq -r '.loanAppId')

sleep 1

# Attempt amendment without approver
curl -s -X POST http://localhost:5102/api/loan-applications/$LOAN_ID/amend \
  -H "Content-Type: application/json" \
  -H "X-Test-User: checker1" \
  -H "X-Test-Role: checker" \
  -d '{"reason":"","approver":"","fields":{"requestedAmount":20000}}' -w "\nHTTP Status: %{http_code}\n"
```

**Expected:**
- HTTP 422
- Error code: `ERP-PACS-VAL-0008`
- No `sync_outbox` row created (rejected before DB write)

### NEG-010: Duplicate sequence number → rejected at NLDR

**Steps:**
```bash
# 1. Create a voucher (generates sequence 1)
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-NEG010","voucherDate":"2026-05-15","voucherType":"CR","narration":"Test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":100}]}'

sleep 2

# 2. Manually inject a duplicate sequence via TestControl
curl -s -X POST http://localhost:5101/api/test/tamper/last-outbox \
  -H "Content-Type: application/json" \
  -d '{"field":"sequence","newValue":1}'

# 3. Create another voucher (will get sequence 2 from allocator, but we tampered it to 1)
# The NLDR should reject the duplicate
```

**Expected:**
- NLDR rejects with `apply_status = 'REJECTED'` or `'DUPLICATE'`
- `sync_outbox` row stays in `FAILED` or `DEADLETTER` status

### NEG-020: Tampered file hash → chunk rejected

**Steps:**
```bash
# Upload a file, then tamper the registry hash before sync
# (Requires file sync to be implemented — M7)
```

**Expected:**
- NLDR rejects the file with `file_received.status = 'REJECTED'`
- Error logged with `ERP-NLDR-VAL-0002`

---

## 9. Failure & Resilience Tests

### FAIL-001: NLDR returns 500 → retry with backoff

**Steps:**
```bash
# 1. Arm NLDR to return 500 for next 3 requests
curl -s -X POST http://localhost:5201/api/test/failure-mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"http500","count":3}'

# 2. Create a voucher
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-FAIL001","voucherDate":"2026-05-15","voucherType":"CR","narration":"Retry test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":100}]}'

# 3. Watch outbox status over time (should retry and eventually succeed)
watch -n 2 'mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e "SELECT status, retry_count FROM sync_outbox ORDER BY outbox_id DESC LIMIT 1;"'

# 4. After ~10 seconds, NLDR should be healthy again and event should be ACKED
```

**Expected:**
- `retry_count` increments from 0 to 3
- After 3 failures, NLDR returns to healthy (count exhausted)
- Event eventually reaches `ACKED` status
- Total time: ~10-15 seconds (exponential backoff)

### FAIL-004: Circuit breaker opens after threshold

**Steps:**
```bash
# 1. Arm NLDR to return 500 indefinitely
curl -s -X POST http://localhost:5201/api/test/failure-mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"http500","count":100}'

# 2. Create 6 vouchers rapidly (exceeds circuit breaker threshold of 5)
for i in $(seq 1 6); do
  curl -s -X POST http://localhost:5101/api/vouchers \
    -H "Content-Type: application/json" \
    -d "{\"voucherNo\":\"VCH-CB-$i\",\"voucherDate\":\"2026-05-15\",\"voucherType\":\"CR\",\"narration\":\"CB test $i\",\"createdBy\":\"admin\",\"lines\":[{\"accountCode\":\"1001\",\"debitAmount\":0,\"creditAmount\":100}]}"
  sleep 0.5
done

# 3. Check circuit state
curl -s http://localhost:5101/api/test/state | jq .circuitState

# 4. Reset NLDR and wait for half-open probe
curl -s -X POST http://localhost:5201/api/test/failure-mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"healthy"}'

# Wait for circuit to transition: OPEN → HALF_OPEN → CLOSED
sleep 65  # OpenDurationSeconds = 60
```

**Expected:**
- After 5 consecutive failures: circuit state = `OPEN`
- No further publish attempts while OPEN
- After 60 seconds: transitions to `HALF_OPEN`, sends one probe
- Probe succeeds → `CLOSED`, backlog drains

### FAIL-007: Redis down → business operations continue (fail-open)

**Steps:**
```bash
# 1. Flush Redis (simulates Redis crash)
curl -s -X POST http://localhost:5101/api/test/redis/flush

# 2. Stop Redis container
docker stop harness-redis-pacs

# 3. Create a voucher — should still succeed
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-REDIS-DOWN","voucherDate":"2026-05-15","voucherType":"CR","narration":"Redis down test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":100}]}' -w "\nHTTP Status: %{http_code}\n"

# 4. Restart Redis
docker start harness-redis-pacs
```

**Expected:**
- Step 3: HTTP 201 (business write succeeds despite Redis being down)
- Cache lookups degrade to MySQL reads (no error to user)
- Logs show warning about Redis unavailability

---

## 10. Power-Cut Tests

### PWR-001: Power-cut before DB commit → no partial state

**Steps:**
```bash
# 1. Arm fault hook to crash before commit
curl -s -X POST http://localhost:5101/api/test/hooks/BeforeDbCommit \
  -H "Content-Type: application/json" \
  -d '{"mode":"crash","count":1}'

# 2. Create a voucher (process will crash)
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-PWR001","voucherDate":"2026-05-15","voucherType":"CR","narration":"Power cut test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":100}]}'
# This will fail (connection reset)

# 3. Restart the service
dotnet run --project src/Pacs.Fas.Api &

# 4. Verify NO partial state exists
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT COUNT(*) as voucher_count FROM voucher WHERE voucher_no='VCH-PWR001';
   SELECT COUNT(*) as outbox_count FROM sync_outbox WHERE idempotency_key LIKE '%VCH-PWR001%';"
```

**Expected:**
- Both counts = 0 (transaction rolled back, no partial state)
- No orphan outbox row without a business row
- No orphan business row without an outbox row

### PWR-002: Power-cut after DB commit, before Kafka publish → resumes on restart

**Steps:**
```bash
# 1. Arm fault hook to crash after commit
curl -s -X POST http://localhost:5101/api/test/hooks/AfterDbCommit \
  -H "Content-Type: application/json" \
  -d '{"mode":"crash","count":1}'

# 2. Create a voucher
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-PWR002","voucherDate":"2026-05-15","voucherType":"CR","narration":"Resume test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":100}]}'

# 3. Verify outbox row exists with status PENDING
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT status FROM sync_outbox WHERE idempotency_key LIKE '%VCH-PWR002%';"

# 4. Restart SyncWorker — it should pick up the PENDING row
dotnet run --project src/Pacs.SyncWorker &
sleep 5

# 5. Verify it eventually gets ACKED
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT status FROM sync_outbox WHERE idempotency_key LIKE '%VCH-PWR002%';"
```

**Expected:**
- Step 3: `status = 'PENDING'` (committed to DB but not yet published)
- Step 5: `status = 'ACKED'` (SyncWorker resumed and completed delivery)

---

## 11. Security Tests

### SEC-001: Tampered payload hash → rejected by NLDR

**Steps:**
```bash
# 1. Arm NLDR in hashStrict mode
curl -s -X POST http://localhost:5201/api/test/failure-mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"hashStrict"}'

# 2. Tamper the last outbox row's hash
curl -s -X POST http://localhost:5101/api/test/tamper/last-outbox \
  -H "Content-Type: application/json" \
  -d '{"field":"hash","newValue":"deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"}'

# 3. Create a voucher (the relay will send the tampered hash)
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-SEC001","voucherDate":"2026-05-15","voucherType":"CR","narration":"Tamper test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":100}]}'

sleep 3

# 4. Verify NLDR rejected it
mysql -h 127.0.0.1 -P 3308 -u root -proot epacs_nldr -e \
  "SELECT event_id, apply_status, reject_reason FROM received_event WHERE apply_status='REJECTED';"
```

**Expected:**
- `apply_status = 'REJECTED'`
- `reject_reason` contains `ERP-NLDR-VAL-0002` (payload hash mismatch)
- PACS outbox row moves to `FAILED` (NACK received)

### SEC-003: Invalid authentication → 401

**Steps:**
```bash
# 1. Arm NLDR to reject next auth
curl -s -X POST http://localhost:5201/api/test/cert/reject-next \
  -H "Content-Type: application/json" \
  -d '{"count":1}'

# 2. Create a voucher and wait for relay attempt
curl -s -X POST http://localhost:5101/api/vouchers \
  -H "Content-Type: application/json" \
  -d '{"voucherNo":"VCH-SEC003","voucherDate":"2026-05-15","voucherType":"CR","narration":"Auth test","createdBy":"admin","lines":[{"accountCode":"1001","debitAmount":0,"creditAmount":100}]}'

sleep 3

# 3. Check outbox — should be FAILED with auth error
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT status, last_error FROM sync_outbox ORDER BY outbox_id DESC LIMIT 1;"
```

**Expected:**
- `status = 'FAILED'` or retrying
- `last_error` contains `ERP-NLDR-SEC-0001`
- After auth is restored, retry succeeds

---

## 12. Performance Tests

### PERF-001: 1000 vouchers created in < 60 seconds

**Steps:**
```bash
# Generate 1000 vouchers
time for i in $(seq 1 1000); do
  curl -s -X POST http://localhost:5101/api/vouchers \
    -H "Content-Type: application/json" \
    -d "{\"voucherNo\":\"VCH-PERF-$(printf '%05d' $i)\",\"voucherDate\":\"2026-05-15\",\"voucherType\":\"CR\",\"narration\":\"Perf test $i\",\"createdBy\":\"admin\",\"lines\":[{\"accountCode\":\"1001\",\"debitAmount\":0,\"creditAmount\":100}]}" > /dev/null
done

# Check all 1000 are in outbox
mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -e \
  "SELECT COUNT(*) as total, SUM(status='ACKED') as acked FROM sync_outbox;"
```

**Expected:**
- Creation time: < 60 seconds for 1000 vouchers
- All 1000 eventually reach ACKED (may take additional time for relay)
- No sequence gaps: `SELECT MAX(sequence_no) - MIN(sequence_no) + 1 = COUNT(*) FROM sync_outbox;`

### PERF-002: Reconnect drain of 1000 events in < 120 seconds

**Steps:**
```bash
# 1. Go offline
curl -s -X POST http://localhost:5101/api/test/offline -d '{"enabled":true}'

# 2. Create 1000 vouchers (all queue in outbox as PENDING)
for i in $(seq 1 1000); do
  curl -s -X POST http://localhost:5101/api/vouchers \
    -H "Content-Type: application/json" \
    -d "{\"voucherNo\":\"VCH-DRAIN-$(printf '%05d' $i)\",\"voucherDate\":\"2026-05-15\",\"voucherType\":\"CR\",\"narration\":\"Drain $i\",\"createdBy\":\"admin\",\"lines\":[{\"accountCode\":\"1001\",\"debitAmount\":0,\"creditAmount\":100}]}" > /dev/null
done

# 3. Go online and time the drain
curl -s -X POST http://localhost:5101/api/test/offline -d '{"enabled":false}'
time bash -c 'while true; do
  ACKED=$(mysql -h 127.0.0.1 -P 3307 -u root -proot epacs_pacs -N -e "SELECT COUNT(*) FROM sync_outbox WHERE status=\"ACKED\";")
  echo "ACKed: $ACKED / 1000"
  [ "$ACKED" -ge 1000 ] && break
  sleep 2
done'
```

**Expected:**
- All 1000 events drain and reach ACKED in < 120 seconds
- Priority ordering respected (if mixed entity types)

---

## 13. Building the Test Matrix

### 13.1 Dimensions

Build your test matrix by crossing these dimensions:

| Dimension | Values |
|-----------|--------|
| **Operation** | INSERT, UPDATE, DELETE, AMENDMENT |
| **Entity** | voucher, loan_application, file |
| **Network state** | online, offline, reconnecting |
| **Infra state** | all-healthy, kafka-down, redis-down, mysql-slow, nldr-500 |
| **Power state** | normal, crash-before-commit, crash-after-commit, crash-during-relay |
| **Concurrency** | single, burst-10, burst-100, burst-1000 |
| **Sequence** | normal, gap-injected, duplicate-injected, out-of-order |
| **Security** | valid-hash, tampered-hash, expired-cert, no-auth |

### 13.2 Priority Matrix

Not all combinations need testing. Use this priority guide:

| Priority | Criteria | Example |
|----------|----------|---------|
| **P0** (must test) | Invariant violation possible | INSERT + crash-after-commit + reconnect |
| **P1** (should test) | User-visible failure | DELETE + offline + reconnect drain order |
| **P2** (nice to have) | Edge case | AMENDMENT + redis-down + burst-100 |

### 13.3 Coverage Mapping

Map each test case to the invariant it proves:

```
Test SYNC-POS-001 → proves I-2 (atomic outbox write)
Test SEQ-001      → proves I-4 (monotonic sequence)
Test SEC-001      → proves I-5 (tamper detection)
Test PWR-001      → proves I-2 (no partial state after crash)
Test FAIL-007     → proves I-1 (MySQL is truth, Redis is disposable)
```

### 13.4 Regression Matrix

After any code change, run at minimum:
1. All SYNC-POS tests (happy path still works)
2. PWR-001 and PWR-002 (crash safety preserved)
3. SEC-001 (tamper detection preserved)
4. The specific test category affected by the change

---

## 14. Evidence Collection

### 14.1 What to Collect

For each test run, collect:

```
Evidence/
├── RUN-{timestamp}/
│   ├── test-id.txt              # Which test was run
│   ├── pre-state/
│   │   ├── sync_outbox.csv      # SELECT * before test
│   │   ├── received_event.csv   # SELECT * before test
│   │   └── sync_sequence.csv
│   ├── post-state/
│   │   ├── sync_outbox.csv      # SELECT * after test
│   │   ├── received_event.csv
│   │   ├── sync_sequence.csv
│   │   └── conflict_log.csv
│   ├── logs/
│   │   ├── pacs-fas-api.json
│   │   ├── pacs-sync-worker.json
│   │   ├── nldr-api.json
│   │   └── nldr-sync-worker.json
│   ├── kafka/
│   │   └── topic-offsets.txt    # kafka-consumer-groups --describe
│   └── result.json              # PASS/FAIL + assertion details
```

### 14.2 Automated Evidence Collection

```bash
# After each test, run:
bash scripts/collect-evidence.sh <test-id>

# This creates the Evidence/RUN-* folder automatically
```

### 14.3 Invariant Assertions (run after every test)

```sql
-- I-2: No orphan outbox rows (business row must exist)
SELECT o.outbox_id, o.entity_type, o.entity_id
FROM sync_outbox o
LEFT JOIN voucher v ON o.entity_id = CAST(v.voucher_id AS CHAR) AND o.entity_type = 'voucher'
LEFT JOIN loan_application l ON o.entity_id = CAST(l.loan_app_id AS CHAR) AND o.entity_type = 'loan_application'
WHERE v.voucher_id IS NULL AND l.loan_app_id IS NULL AND o.change_type != 'DELETE';
-- Expected: 0 rows

-- I-4: No sequence gaps
SELECT a.sequence_no + 1 as gap_start, MIN(b.sequence_no) - 1 as gap_end
FROM sync_outbox a
LEFT JOIN sync_outbox b ON b.sequence_no > a.sequence_no
WHERE NOT EXISTS (SELECT 1 FROM sync_outbox c WHERE c.sequence_no = a.sequence_no + 1)
  AND b.sequence_no IS NOT NULL
GROUP BY a.sequence_no;
-- Expected: 0 rows

-- I-5: All ACKED events have matching hash at NLDR
SELECT o.event_id, o.payload_hash as pacs_hash, r.payload_hash as nldr_hash
FROM sync_outbox o
JOIN received_event r ON o.event_id = r.event_id
WHERE o.payload_hash != r.payload_hash;
-- Expected: 0 rows
```

---

## 15. Gotchas, Pitfalls & Known Issues

### 15.1 Timing Issues

| Gotcha | Mitigation |
|--------|-----------|
| Test checks outbox before relay has run | Always `sleep 2` after creating data, or poll until status changes |
| Circuit breaker timing is non-deterministic | Use `GET /api/test/state` to confirm circuit state before asserting |
| Kafka consumer lag | Check consumer group offsets, not just DB state |
| Clock drift tests affect other tests | Always `POST /api/test/clock/reset` after clock tests |

### 15.2 Docker-Specific

| Gotcha | Mitigation |
|--------|-----------|
| MySQL container takes 15-20s to be ready | Wait for `docker compose ps` to show "healthy" |
| Kafka topic auto-creation race | Services create topics at startup; restart if "topic not found" |
| Port conflicts with host services | Ensure nothing else uses 3307, 3308, 6380, 6381, 9092 |
| Docker Desktop memory | Allocate at least 4 GB to Docker Desktop |
| Volume persistence between resets | Always use `scripts/reset-lab.sh` (drops volumes) |

### 15.3 Native EXE-Specific

| Gotcha | Mitigation |
|--------|-----------|
| Services start before MySQL is ready | Service map has dependency ordering; check health before testing |
| Firewall blocks localhost ports | Run `netsh advfirewall` to allow harness ports |
| Single MySQL instance for both DBs | Ensure both `epacs_pacs` and `epacs_nldr` schemas exist |
| Log files grow unbounded in long tests | Check `D:\ePACSData\logs\harness\` size periodically |
| TestMode=false blocks fault injection | Use `/demo` flag or manually set `Harness:TestMode=true` |

### 15.4 Common Mistakes

| Mistake | Consequence | Fix |
|---------|-------------|-----|
| Not resetting between test categories | Leftover data causes false positives/negatives | Always `reset-lab.sh` |
| Checking NLDR before ACK cycle completes | Event shows as not-received | Wait for `status=ACKED` in outbox |
| Running power-cut tests on production | Data loss | Only on dedicated test VM/laptop |
| Forgetting to disarm fault hooks | Next test fails unexpectedly | `POST /api/test/hooks/clear` |
| Using `docker stop` instead of `docker kill` for power-cut | Graceful shutdown ≠ power-cut | Use `docker kill` or `Stop-VM -TurnOff` |

### 15.5 Test Isolation

- Each test case should be **independently runnable** after a lab reset
- Never depend on state from a previous test (except within a multi-step scenario)
- If a test creates data, use unique identifiers (e.g., `VCH-{TEST-ID}-001`)
- If a test arms fault hooks, always disarm at the end (even on failure)

### 15.6 Offline Simulation Accuracy

The harness simulates offline by:
1. `POST /api/test/offline` — sets a flag that pauses the outbound relay
2. `POST /api/test/network/block` — adds firewall rules (Docker only)
3. Physical network disconnect (two-laptop setup)

**Important:** Option 1 only pauses the relay. Kafka and HTTP are still technically reachable. For true network partition testing, use option 2 or 3.

### 15.7 Power-Cut Simulation Accuracy

| Method | Fidelity | Use when |
|--------|----------|----------|
| Fault hook `crash` mode | Medium — `Environment.Exit(1)` | Most tests (fast, repeatable) |
| `docker kill` | High — SIGKILL, no cleanup | Docker integration tests |
| `Stop-VM -TurnOff` | Highest — simulates power loss | Hyper-V lab, final validation |
| Physical power pull | Production-grade | Pilot site acceptance only |

### 15.8 Multi-PACS Testing

For SEQ-009/SEQ-010 (cross-PACS isolation):
- Use `scripts/seed-multi-pacs.ps1` to configure a second PACS profile
- Set `Pacs__PacsId=PACS-AP-0002` on the second instance
- Verify sequences are independent (PACS-AP-0001 seq 1,2,3 and PACS-AP-0002 seq 1,2,3)

---

## 16. Glossary

| Term | Definition |
|------|-----------|
| **PACS** | Primary Agricultural Credit Society — a village-level cooperative bank branch |
| **NLDR** | National Level Data Repository — the central aggregation system |
| **Outbox** | `sync_outbox` table — stores events to be synced, in the same transaction as business data |
| **Relay** | `Pacs.SyncWorker.OutboundRelayService` — polls outbox and publishes to Kafka/HTTP |
| **Ingest** | `Nldr.Api` 12-step pipeline that validates and applies incoming events |
| **ACK** | Acknowledgement from NLDR that an event was successfully applied |
| **NACK** | Negative acknowledgement — event was rejected (with reason code) |
| **Sequence** | Monotonically increasing number per `(pacs_id, stream_name)` — guarantees ordering |
| **Envelope** | The JSON wrapper around business data (includes hash, sequence, correlation) |
| **Canonical JSON** | Deterministic JSON serialization (sorted keys, no whitespace) used for hashing |
| **Fault Hook** | Named checkpoint in code where failures can be injected during testing |
| **TestControl** | HTTP routes (`/api/test/*`) that arm fault hooks and simulate failures |
| **Circuit Breaker** | Pattern that stops retrying after N consecutive failures, waits, then probes |
| **Dead Letter** | Events that exceeded max retries — require manual intervention |
| **Three-Witness** | Audit pattern: sync_outbox + domain audit table + traceability row must all exist |
| **Before-State** | Snapshot of entity before DELETE/AMENDMENT — mandatory for audit trail |
| **.epcfg** | Site Configuration Pack — signed file with site-specific settings |
| **DataRoot** | `D:\ePACSData` — durable storage for all ePACS data on a PACS node |

---

## Appendix A: Quick Reference — Fault Injection Commands

```bash
# ─── NLDR Failure Modes ───────────────────────────────────
curl -X POST http://localhost:5201/api/test/failure-mode -H "Content-Type: application/json" -d '{"mode":"healthy"}'
curl -X POST http://localhost:5201/api/test/failure-mode -H "Content-Type: application/json" -d '{"mode":"http500","count":3}'
curl -X POST http://localhost:5201/api/test/failure-mode -H "Content-Type: application/json" -d '{"mode":"timeout","delayMs":5000}'
curl -X POST http://localhost:5201/api/test/failure-mode -H "Content-Type: application/json" -d '{"mode":"dropAck","count":1}'
curl -X POST http://localhost:5201/api/test/failure-mode -H "Content-Type: application/json" -d '{"mode":"rateLimit","retryAfterSec":20}'
curl -X POST http://localhost:5201/api/test/failure-mode -H "Content-Type: application/json" -d '{"mode":"hashStrict"}'
curl -X POST http://localhost:5201/api/test/failure-mode -H "Content-Type: application/json" -d '{"mode":"sequenceStrict"}'

# ─── PACS Fault Hooks ─────────────────────────────────────
curl -X POST http://localhost:5101/api/test/hooks/BeforeDbCommit -H "Content-Type: application/json" -d '{"mode":"crash","count":1}'
curl -X POST http://localhost:5101/api/test/hooks/AfterDbCommit -H "Content-Type: application/json" -d '{"mode":"pause","durationMs":5000}'
curl -X POST http://localhost:5101/api/test/hooks/BeforeKafkaPublish -H "Content-Type: application/json" -d '{"mode":"throw","count":1}'
curl -X POST http://localhost:5101/api/test/hooks/clear

# ─── Network / Offline ────────────────────────────────────
curl -X POST http://localhost:5101/api/test/offline -H "Content-Type: application/json" -d '{"enabled":true}'
curl -X POST http://localhost:5101/api/test/offline -H "Content-Type: application/json" -d '{"enabled":false}'
curl -X POST http://localhost:5101/api/test/redis/flush
curl -X POST http://localhost:5101/api/test/kafka/stop
curl -X POST http://localhost:5101/api/test/kafka/start

# ─── Clock / Time ─────────────────────────────────────────
curl -X POST http://localhost:5101/api/test/clock/jump -H "Content-Type: application/json" -d '{"offsetSeconds":86400}'
curl -X POST http://localhost:5101/api/test/clock/reset

# ─── State Inspection ─────────────────────────────────────
curl -s http://localhost:5101/api/test/state | jq .
curl -s http://localhost:5201/api/test/state | jq .
```

---

## Appendix B: SQL Verification Queries

```sql
-- ═══ PACS Side ═══════════════════════════════════════════

-- Outbox summary
SELECT status, COUNT(*) as cnt FROM sync_outbox GROUP BY status;

-- Latest events
SELECT outbox_id, sequence_no, change_type, entity_type, status, retry_count, created_at
FROM sync_outbox ORDER BY outbox_id DESC LIMIT 20;

-- Sequence continuity check
SELECT MAX(sequence_no) - MIN(sequence_no) + 1 as expected,
       COUNT(*) as actual,
       MAX(sequence_no) - MIN(sequence_no) + 1 - COUNT(*) as gaps
FROM sync_outbox;

-- Dead letter queue
SELECT outbox_id, event_id, last_error, retry_count FROM sync_outbox WHERE status = 'DEADLETTER';

-- Checkpoint status
SELECT * FROM sync_checkpoints;

-- ═══ NLDR Side ═══════════════════════════════════════════

-- Received events summary
SELECT apply_status, COUNT(*) as cnt FROM received_event GROUP BY apply_status;

-- Rejected events (with reasons)
SELECT event_id, pacs_id, sequence_no, reject_reason FROM received_event WHERE apply_status = 'REJECTED';

-- Sequence gaps detected
SELECT * FROM sequence_gap WHERE resolved_at IS NULL;

-- ACK log
SELECT event_id, ack_status, nack_reason FROM ack_log ORDER BY ack_id DESC LIMIT 20;

-- Heartbeat history
SELECT pacs_id, received_at, payload_json FROM heartbeat ORDER BY heartbeat_id DESC LIMIT 10;
```

---

## Appendix C: Test Execution Checklist Template

```markdown
## Test Run: [TEST-ID]
**Date:** ____  **Tester:** ____  **Environment:** Docker / Native EXE

### Pre-conditions
- [ ] Lab reset completed
- [ ] All services healthy (health endpoints return 200)
- [ ] Pre-state captured (sync_outbox count, received_event count)

### Execution
- [ ] Step 1: ____
- [ ] Step 2: ____
- [ ] Step 3: ____

### Verification
- [ ] Expected HTTP response received
- [ ] Outbox state correct (status, sequence, retry_count)
- [ ] NLDR state correct (apply_status, reject_reason)
- [ ] Invariant assertions pass (Appendix B queries)
- [ ] No unexpected errors in logs

### Evidence
- [ ] Evidence folder created: Evidence/RUN-{timestamp}/
- [ ] Pre/post DB dumps captured
- [ ] Logs captured
- [ ] Result documented

### Result: PASS / FAIL
**Notes:** ____
```

---

*End of Tester's Guide. For design details, see `docs/test-harness/00-design-overview.md`. For developer setup, see `harness/README.md`.*
