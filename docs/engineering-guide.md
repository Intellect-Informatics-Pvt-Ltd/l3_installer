# ePACS Offline Installer — Engineering Guide

> Technical analysis, solutions, mitigations, and pitfalls for the development team.
> Compiled from architecture review sessions covering: DDL drift, differential sync,
> deletion handling, ID seeding, file sync, rural resilience, and observability.

---

## 1. AUTO_INCREMENT Seeding and ID Space Partitioning

### 1.1 Technical Query

How do we prevent AUTO_INCREMENT primary key collisions between:
- Multiple offline PACS nodes generating records independently
- NLDR (central) generating records that flow down to PACS
- Records syncing from PACS to NLDR without re-keying

### 1.2 Analysis

The DDL (`docs/AP_DDL.sql`) reveals a **range-partitioned BIGINT ID space**:

**Evidence from the schema:**
- `ln_applicationmain` AUTO_INCREMENT = 21,152,377,172,115,393
- `fa_ledger` AUTO_INCREMENT = 133,779,293,356,240
- `ln_productpurposemapping` AUTO_INCREMENT = 2,209,997,707,094,314,824

These massive numbers are not sequential counters — they are **PACS-prefixed composite IDs**.

**Supporting infrastructure:**
- `sequences` table: per-PACS, per-table sequence counters (`pacsid`, `sequencename`, `nextsequenceid`)
- `sequencetables` table: registry of tables using sequence-based IDs
- `pacsserialnumbers` table: PACS-specific human-readable serial registry

**Three ID columns on ~646 business tables:**

| Column | Type | Purpose |
|--------|------|---------|
| PRIMARY KEY (SlNo, ApplicationNo, etc.) | BIGINT AUTO_INCREMENT | Globally unique — seeded per-PACS |
| `idgeneratorforpacs` | BIGINT | PACS-local sequence counter for offline generation |
| `SerialNumberOfPacs` | LONGTEXT | Human-readable serial (voucher/receipt numbers) |
| `SourceId` | TINYINT | Origin: 1=local PACS, 2=from NLDR, 3=legacy |

### 1.3 Solution

**Formula:** `Seed = pacsid * RANGE_SIZE`

Where `RANGE_SIZE = 10,000,000,000` (10^10 = 10 billion IDs per PACS per table).

```
BIGINT ID Space (0 to 9.2 x 10^18):

[NLDR Reserved: 1 — 9,999,999,999]
[PACS 2115: 21,150,000,000,000 — 21,159,999,999,999]
[PACS 2116: 21,160,000,000,000 — 21,169,999,999,999]
[PACS 3042: 30,420,000,000,000 — 30,429,999,999,999]
...
```

**Installer actions during fresh install:**
1. Read `pacsid` from `.epcfg`
2. Compute seed: `pacsid * 10,000,000,000`
3. `ALTER TABLE <table> AUTO_INCREMENT = <seed>` for all 646+ business tables
4. Initialize `sequences` table with `nextsequenceid = <seed>` per table
5. All locally-created records get `SourceId = 1`

### 1.4 Mitigation

- **Installer Agent monitors** AUTO_INCREMENT values daily against BIGINT ceiling (9.2 x 10^18)
- Alert at 50% usage; block upgrades at 75%
- Currently only `ln_productpurposemapping` is at 24% — all others well under 1%

### 1.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| Two PACS assigned same `pacsid` | ID collision, data corruption | Validate uniqueness during `.epcfg` generation; installer checks against state registry |
| RANGE_SIZE too small | PACS exhausts its range | 10 billion IDs per table is effectively infinite for a single PACS (would take 300+ years at 100 records/second) |
| NLDR uses IDs in PACS range | Collision on inbound sync | NLDR restricted to range 1–9,999,999,999; enforced by application |
| `sequences` table out of sync with AUTO_INCREMENT | Duplicate key errors | Installer verifies `sequences.nextsequenceid >= MAX(PK)` on every upgrade |
| Legacy data migration with conflicting IDs | Collision with new records | Legacy records use `SourceId = 3`; migrated with original IDs preserved in NLDR range |

### 1.6 Additional Considerations

- **Restore to new machine**: AUTO_INCREMENT is part of the MySQL datadir. Restoring a backup preserves the correct seed. No re-seeding needed.
- **Schema fingerprint**: The seed value is NOT part of the schema fingerprint (it's data, not structure). Drift detection ignores AUTO_INCREMENT values.
- **Sync idempotency**: The globally-unique PK serves as the natural idempotency key for sync. If NLDR receives a record with a PK it already has, it's a duplicate.

---

## 2. Deletion Handling and Sync Consistency

### 2.1 Technical Query

How do we handle:
- Hard deletes (voucher deletion, PDS/Trading deletions) that remove data permanently
- In-place amendments (auditor corrections) that modify already-synced financial data
- Bulk deletions via Correction Tool
- NLDR becoming stale when PACS deletes/amends synced records

### 2.2 Analysis

**Current state (from `docs/deletionsenerio.md`):**
- **Hard deletes**: ✅ Implemented (physical DELETE from tables)
- **Soft deletes**: ❌ Not implemented
- **Data versioning**: ❌ Not implemented
- **Maker-checker for amendments**: ❌ Not implemented
- **Audit trail for deletions**: ✅ Partial (6 deletion audit tables exist)

**Deletion audit tables:**
- `fa_voucherdeletionmain` + `fa_voucherdeletiondetails` (FAS)
- `pds_purchasedelete` + `pds_salesdelete` (PDS)
- `tr_purchasedelete` + `tr_salesdelete` (Trading)

**Correction logs:**
- `CorrectionTool_LoansActivityLog`
- `CorrectionTool_MembershipActivityLog`

**The deletion workflow (e.g., voucher):**
1. Data saved to temp tables (`fa_vouchermaintemp`)
2. User posts → moves to permanent tables (`fa_vouchermain`)
3. User deletes → logs to deletion tables → hard DELETEs from temp tables

### 2.3 Solution

**Core principle:** "Nothing is ever truly deleted from the sync perspective. Deletions and amendments are EVENTS that must be propagated to NLDR."

**Implementation:**

```
Business Transaction (DELETE or UPDATE)
  BEGIN TRANSACTION
    -- Original business logic (hard delete or update)
    DELETE FROM fa_vouchermaintemp WHERE VoucherNo = ?

    -- NEW: Write sync event to outbox (same transaction)
    INSERT INTO sync_outbox (
      event_type, change_type, entity_type, entity_id,
      payload_json, before_state_json, payload_hash
    ) VALUES (
      'DATA_CHANGE', 'DELETE', 'fa_voucher', <voucher_id>,
      <before_state_json>, NULL, SHA2(<payload>, 256)
    )
  COMMIT
```

**Change types in sync_outbox:**
- `INSERT` → new record created (existing behavior)
- `UPDATE` → record modified (payload = after state)
- `DELETE` → record removed (payload = before state — the deleted data)
- `AMENDMENT` → auditor correction (payload = before + after + reason + approver)

**NLDR behavior on receiving events:**
- `INSERT` → creates record
- `UPDATE` → updates record
- `DELETE` → **soft-deletes** on NLDR side (marks as deleted, retains for audit)
- `AMENDMENT` → updates record + logs amendment trail

### 2.4 Mitigation

| Risk | Mitigation |
|------|-----------|
| Deletion without outbox entry (application bug) | Reconciliation detects missing records; nightly comparison |
| Bulk deletion exceeds safety threshold | Configurable threshold (default: 10 records); above threshold requires mandatory backup |
| Backdated correction on synced data | AMENDMENT event carries before+after state; NLDR applies and flags for audit review |
| Accidental bulk deletion | Correction Tool log + sync outbox DELETE events; restore from pre-operation backup |
| Amendment without reason/approver | Configurable enforcement: `AmendmentRequiresReason = true`, `AmendmentRequiresApprover = true` |

### 2.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| DELETE trigger fires but transaction rolls back | Orphan sync event (delete event for record that still exists) | Use application-level capture (same transaction), not triggers |
| Before-state JSON exceeds outbox column size | Sync event lost | Use LONGTEXT for `before_state_json`; compress if > 1 MB |
| Deletion of parent record with FK children | Cascade deletes not captured individually | Capture parent deletion; NLDR cascades on its side |
| Amendment to non-synced record (created after last sync) | Unnecessary AMENDMENT event | Check `sync_status` before creating AMENDMENT event; if never synced, treat as INSERT |
| Correction Tool bypasses outbox | NLDR never learns about the correction | Correction Tool MUST write to outbox; enforce via code review + CI check |

### 2.6 Future Enhancement (v2): Soft Delete

For v2, add soft-delete columns to critical financial tables:

```sql
ALTER TABLE fa_vouchermain
  ADD COLUMN is_deleted TINYINT(1) NOT NULL DEFAULT 0,
  ADD COLUMN deleted_at DATETIME NULL,
  ADD COLUMN deleted_by VARCHAR(100) NULL,
  ADD COLUMN deletion_reason VARCHAR(500) NULL,
  ALGORITHM=INSTANT;  -- non-breaking, zero downtime
```

This eliminates the need for before-state capture (the record is still there, just flagged).

---

## 3. DDL Drift and Schema Migration

### 3.1 Technical Query

How do we detect and handle:
- Schema modifications made outside the installer (manual ALTER TABLE in the field)
- Failed partial migrations (power-cut mid-migration)
- Irreversible DDL (column drops, type changes) that prevent rollback
- 1,057 tables with complex FK dependencies during migration

### 3.2 Analysis

**Schema profile (from `docs/AP_DDL.sql`):**
- 1,057 tables, 81 foreign keys, 42 views, 2,235 indexes
- 4,556 LOB columns (longtext/longblob) — prevent INSTANT DDL
- Mixed charsets: 24 tables utf8mb3, 1,033 tables utf8mb4
- No partitioned tables, no stored procedures/triggers
- Module prefixes: cm(152), ln(126), fa(98), tr(77), pds(58), cus(46), etc.

**MySQL 8.4 DDL algorithms:**
- INSTANT: metadata-only (milliseconds) — add nullable column, drop column, rename
- INPLACE: online rebuild (seconds-minutes) — add index, add FK
- COPY: full table rebuild (minutes-hours, TABLE LOCKED) — change type, change charset

### 3.3 Solution

**Schema Fingerprinting:**
1. On install/upgrade: capture full `INFORMATION_SCHEMA` snapshot (tables, columns, indexes, FKs)
2. Compute SHA-256 fingerprint hash
3. Before next upgrade: compare current fingerprint against expected baseline
4. Classify drift: Benign (extra indexes) → Compatible (additive columns) → Breaking (missing columns)
5. Breaking drift → block upgrade + generate remediation script

**Migration ordering (FK-aware):**
1. Disable FK checks
2. Drop all 42 views
3. Infrastructure tables (Hangfire — charset conversion)
4. Master tables (cm_* — FK parents)
5. Customer tables (cus_* — FK parents for loans)
6. Business tables (ln_*, fa_*, sca_*, trm_*)
7. Trading/PDS/Asset tables
8. Audit/reporting tables
9. Re-enable FK checks
10. Recreate views
11. Run pt-table-checksum on critical tables

**DDL classification per migration script:**
- Each script has a header: `-- @classification: INSTANT | INPLACE | COPY`
- COPY-algorithm on tables > 1M rows → use `pt-online-schema-change`
- Expand-migrate-contract pattern for irreversible DDL

### 3.4 Mitigation

| Risk | Mitigation |
|------|-----------|
| Power-cut during migration | Checkpoint-per-script in `schema_version_registry`; resume from last committed |
| Manual DDL in the field | Schema fingerprint detects drift before upgrade; blocks if breaking |
| pt-online-schema-change fails | Old table untouched (shadow copy pattern); retry or fall back to maintenance window |
| View breaks after column rename | Drop all views before migration; recreate after from versioned definitions |
| FK constraint violation during migration | Disable FK checks during migration; re-enable and validate after |

### 3.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| Fingerprint includes AUTO_INCREMENT values | False drift detection on every check | Exclude AUTO_INCREMENT from fingerprint (it's data, not structure) |
| LOB columns prevent INSTANT for many operations | Unexpected long migrations | DDL classifier identifies these at build time; CI validates |
| Mixed charset tables cause JOIN failures | Silent data corruption | One-time charset remediation in first upgrade (truncate Hangfire tables + ALTER) |
| `pt-online-schema-change` on table with triggers | pt-osc fails | ePACS has 0 triggers currently; if added, pt-osc v3.5+ handles them |
| Migration script order wrong (child before parent) | FK violation | Topological sort from `INFORMATION_SCHEMA.KEY_COLUMN_USAGE` |

---

## 4. Differential Backup and Data Sync

### 4.1 Technical Query

How do we efficiently backup and sync a 1,057-table database that can grow to 100+ GB at DCCB hubs, over intermittent 4G connectivity?

### 4.2 Analysis

**Three types of "delta" in the system:**
1. **Schema delta** — DDL changes between versions (handled by DbUp migration scripts)
2. **Data delta** — row-level changes for backup (handled by Percona XtraBackup incremental)
3. **Sync delta** — business data changes for NLDR (handled by transactional outbox)

**Backup challenge:**
- Full `mysqldump` of 100 GB = 15+ minutes + 100 GB disk space
- Daily full backup is impractical for large PACS
- Need incremental/differential approach

**Sync challenge:**
- Only 9 tables have `updated_at` with `ON UPDATE CURRENT_TIMESTAMP`
- 194 tables have `DataEntryWorkingDate` (INSERT only, not UPDATE)
- Cannot efficiently detect modified records without timestamps

### 4.3 Solution

**Backup tiers:**

| Tier | Tool | Type | When | Size (10 GB DB) |
|------|------|------|------|-----------------|
| Physical Full | Percona XtraBackup 8.4 | Full | Weekly | ~10 GB |
| Physical Incremental | Percona XtraBackup 8.4 | Incremental (page-level) | Daily | ~500 MB–2 GB |
| Logical Full | `mysqlsh util.dumpInstance` | Full logical | Pre-upgrade | ~8 GB |
| Logical Table | `mysqlsh util.dumpTables` | Specific tables | On-demand | Varies |

**Sync delta tracking:**
- New transactions: captured via transactional outbox (existing pattern)
- Master data changes: add `updated_at TIMESTAMP ON UPDATE CURRENT_TIMESTAMP` to all sync-eligible tables (INSTANT DDL — zero downtime)
- Deletions: captured via outbox DELETE events (Section 2)
- Amendments: captured via outbox AMENDMENT events (Section 2)

### 4.4 Mitigation

- **Percona XtraBackup** for physical incremental (only changed InnoDB pages — genuinely differential)
- **Content-hash deduplication** for file sync (same file = sync once regardless of path)
- **Priority-based sync drain** when connectivity returns (financial → audit → master data → telemetry)
- **Bandwidth-adaptive chunk sizing** (4G: 1 MB, 3G: 256 KB, 2G: 64 KB)

### 4.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| XtraBackup incremental chain breaks | Cannot restore without full + all incrementals | Weekly full backup resets the chain; verify chain integrity daily |
| `updated_at` column backfilled with NULL | Cannot distinguish "never modified" from "modified before migration" | NULL = "never modified since migration" — treat as original; don't sync |
| Large LOB columns inflate incremental size | Daily incremental larger than expected | LOB changes are rare (photos uploaded once); monitor incremental size |
| Sync outbox grows unbounded during long offline | Disk space exhaustion | Monitor outbox depth; alert at configurable threshold; oldest events archived |

---

## 5. File/Attachment Sync

### 5.1 Technical Query

How do we sync member photos, field verification uploads, and generated reports (PDFs) between PACS and NLDR, given:
- ~2 MB/day per PACS (photos + uploads)
- Reports 1–10 MB each (on-demand)
- Bidirectional (PACS → NLDR for uploads; NLDR → PACS for policies)
- Intermittent 4G connectivity
- Enable/disable feature flag

### 5.2 Analysis

**File naming convention:** `{state_id}/{dccb_id}/{branch_id}/{pacs_id}/{type}/{filename}`

**Storage:** `D:\ePACSData\attachments\` with hierarchical structure

**Transport options:** SFTP (resume support, large files) and HTTPS multipart (firewall-friendly, API-based)

### 5.3 Solution

**Content-hash-based deduplication:**
- Compute SHA-256 of each file
- Store in `file_sync_registry` MySQL table
- If hash unchanged since last sync → skip (no re-upload even if file renamed)
- Dual transport: HTTPS primary, SFTP fallback (configurable)

**Enable/disable:**
- `FileSync.Enabled = true/false` in appsettings.json
- Controllable via ePACS application UI at runtime
- When disabled: files accumulate locally, registry tracks them
- When enabled: backlog drains automatically (small files first)

### 5.4 Mitigation

- **Batch size limit** per sync cycle (configurable, default 50 MB)
- **Priority**: small files first (photos ~200 KB), then reports (1–10 MB)
- **Peak-hours awareness**: skip large file hashing during business hours
- **Resume support**: per-chunk ACK for HTTPS; SFTP native resume

### 5.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| Large report PDF blocks sync of small photos | Photos delayed | Priority queue: small files first |
| SFTP key compromised | Unauthorized access to PACS files | Key rotation via signed config update; key per-PACS |
| File deleted locally after sync | NLDR has file that PACS doesn't | File deletion events in sync (same as DB deletion pattern) |
| Duplicate file uploaded with different name | Wasted bandwidth | Content-hash dedup prevents re-upload |
| USB-copied files have future timestamps | Sync logic confused | Use content hash, not timestamp, as the delta key |

---

## 6. PACS Heartbeat and Online/Offline Detection

### 6.1 Technical Query

How does NLDR/CoopsIndia Dashboard know if a PACS is online or offline? How does a PACS proactively announce its connectivity status?

### 6.2 Analysis

NLDR has no way to "ping" a PACS (PACS is behind NAT/firewall, no inbound connectivity). The PACS must proactively announce itself.

### 6.3 Solution

**Proactive heartbeat from PACS to CoopsIndia Dashboard:**
- On connectivity detected → send immediate heartbeat
- While online → send every 5 minutes (configurable)
- On connectivity lost → stop sending
- Dashboard marks PACS offline after 2× interval (10 min) with no signal

**Dual protocol:**
- HTTPS POST to `/api/v1.0/pacsStatus` (stateless, firewall-friendly)
- WebSocket to `/ws/v1.0/pacsStatus` (real-time, persistent connection)
- Selected via configuration

**Heartbeat payload:**
```json
{
  "pacs_id": "AP-XYZ-0001",
  "state_id": "AP",
  "dccb_id": "XYZ",
  "branch_id": "001",
  "online_since": "2026-05-04T10:00:00Z",
  "last_sync_timestamp": "2026-05-04T09:45:00Z",
  "pending_outbox_count": 42,
  "pending_files_count": 7,
  "disk_usage_percent": 35,
  "stack_version": "3.2.1",
  "schema_version": 25,
  "last_backup_at": "2026-05-04T02:00:00Z",
  "health_status": "Healthy",
  "connectivity_mode": "4G",
  "uptime_seconds": 3600
}
```

### 6.4 Mitigation

- **Fire-and-forget**: heartbeat failure NEVER blocks business operations
- **Circuit breaker**: after 5 consecutive failures, stop sending for cooldown period
- **Bandwidth-minimal**: payload is < 1 KB; negligible even on 2G

### 6.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| Heartbeat endpoint down | Dashboard shows all PACS offline | Dashboard should distinguish "endpoint down" from "PACS offline" |
| Clock drift affects `online_since` | Misleading uptime data | Use relative `uptime_seconds` instead of absolute timestamps for duration |
| Heartbeat floods during reconnect | Dashboard overwhelmed | Rate-limit: max 1 heartbeat per interval regardless of reconnect events |
| WebSocket connection leak | Memory/resource exhaustion | Reconnect logic with exponential backoff; max 1 connection per PACS |

---

## 7. Rural Resilience and Power-Cut Recovery

### 7.1 Technical Query

How do we ensure the installer and all services survive hard power loss (bare-metal power-cut) at any point during any operation?

### 7.2 Analysis

**Environment reality:**
- UPS not guaranteed at all PACS sites
- Power cuts are a daily occurrence in rural India
- 8 GB RAM + SSD hardware (no spinning disks)
- 35–45°C operating temperatures

### 7.3 Solution

**Every operation is resumable from a checkpoint:**

| Operation | Recovery Behavior |
|-----------|-------------------|
| Fresh install (payload extract) | Resume from last extracted payload (progress tracked in staging-manifest.json) |
| Fresh install (DB init) | Drop and re-initialize (no data yet) |
| Upgrade (binary staging) | Junction still points to old version → old version starts normally |
| Upgrade (DB migration) | Resume from last committed script (checkpoint in schema_version_registry) |
| Upgrade (junction flip) | Junction not yet flipped → old version still active |
| Backup (in progress) | Incomplete backup discarded (missing manifest signature); previous valid backup retained |
| Restore (in progress) | Pre-restore safety backup exists → revert to safety backup |
| Business operation | InnoDB crash recovery → MySQL restarts → services restart via Windows recovery actions |
| Sync upload | Resume from last ACK'd chunk (checkpoint in MySQL) |
| Audit write | Transactional sink → InnoDB guarantees; deferred journal as fallback |

**MySQL hardening:**
```ini
innodb_flush_log_at_trx_commit = 1  # flush redo log on every commit
sync_binlog = 1                      # sync binlog on every commit
innodb_doublewrite = ON              # protect against torn pages
innodb_checksum_algorithm = crc32    # detect corruption
```

**Installer state checkpoint:**
- Write `state.json` with fsync on every phase transition
- Use write-then-rename pattern (atomic on NTFS)
- On restart: read state.json → resume from recorded phase

### 7.4 Mitigation

- **Atomic file operations**: write-then-rename for all config/state files
- **Transaction boundaries**: every business operation is a single MySQL transaction
- **Service recovery actions**: Windows service manager restarts crashed services (60s/120s/300s)
- **Installer Agent**: detects incomplete state on boot → enters RECOVERY mode

### 7.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| state.json itself is corrupt (torn write) | Cannot determine resume point | Write-then-rename makes this impossible on NTFS (rename is atomic) |
| MySQL redo log corrupt after power-cut | Database won't start | `innodb_doublewrite = ON` prevents torn pages; InnoDB crash recovery handles the rest |
| Kafka log corrupt after power-cut | Events lost | `flush.messages=1` ensures durability; but Kafka is NOT the durable anchor — MySQL outbox is |
| SSD wear from frequent fsync | Reduced SSD lifespan | Modern SSDs handle millions of write cycles; 8 GB RAM means most writes are buffered |
| Thermal throttling during long migration | Migration takes hours | Monitor temperature via WMI; pause at 85°C; reduce parallelism |

---

## 8. Observability and Error Handling

### 8.1 Technical Query

How do we ensure consistent, correlated, PII-safe logging across all services, and how do we handle errors in a way that's useful for both operators and support engineers?

### 8.2 Analysis

**Existing platform utilities:**
- `Intellect.Erp.Observability` (10 NuGet packages): structured logging, correlation, redaction, audit hooks
- `Intellect.Erp.ErrorHandling`: typed exceptions, YAML error catalogs
- `Intellect.Erp.Traceability`: compliance-grade audit (11 tables, geo-tag, anomaly rules)

### 8.3 Solution

**Structured logging (schema v1):**
- Every log entry: `@timestamp`, `level`, `correlationId`, `app`, `module`, `feature`, `operation`, `pacsId`, `stateCode`
- PII redacted via `IRedactionEngine` (Aadhaar → `****-****-1234`, mobile → `******5678`)
- Correlation ID propagated across HTTP → Kafka → Sync Agent → NLDR

**Error handling:**
- YAML error catalog: `ERP-INST-{CATEGORY}-{NUMBER}` (e.g., `ERP-INST-PRE-0004`)
- Typed exceptions: `PrecheckException`, `InstallException`, `MigrationException`, etc.
- Operator sees: plain-language `userMessage`
- Support bundle contains: `supportMessage` with technical details + correlation ID

**Audit trail:**
- Hash-chained audit log for critical operations (tamper-evident)
- Traceability module integration via `TraceabilityBridgeAuditHook`
- Geo-tagged with fallback chain: GPS → Cell Tower → Site Config → Unavailable

### 8.4 Mitigation

- **Support bundle**: one-click generation, encrypted ZIP, PII redacted, correlation-filtered
- **Log rotation**: 30-day app, 90-day audit, 7-day MySQL (all configurable)
- **Disk protection**: max 10% of data partition or 50 GB for logs (whichever smaller)

### 8.5 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| PII leaks into logs via exception messages | Compliance violation | `IRedactionEngine` applied to all log output; regex patterns for Aadhaar/mobile/account |
| Correlation ID lost across Kafka boundary | Cannot trace end-to-end | `KafkaHeaders` helper propagates correlation ID in message headers |
| Error catalog code not found | Generic error shown to operator | Fallback to `ERP-CORE-SYS-0001` with original message preserved in support log |
| Log files fill disk | Services crash | Installer Agent enforces rotation; critical threshold blocks non-essential writes |
| Audit chain broken (tampered) | Cannot prove integrity | Chain verification runs on every Installer Agent health check; alerts on break |

---

## 9. Configuration Architecture

### 9.1 Technical Query

How do we ensure zero hardcoding while maintaining sensible defaults and supporting site-specific customization across hundreds of PACS?

### 9.2 Solution

**Configuration hierarchy (highest priority wins):**
1. `appsettings.json` — compiled defaults (shipped with binaries)
2. `appsettings.Production.json` — environment overrides (generated by installer from templates)
3. `.epcfg` (Site Config Pack) — site-specific values (signed, distributed out-of-band)
4. Environment variables — runtime overrides (for CI/testing)

**Key principle:** Every threshold, path, port, interval, and retry count is a named configuration value with a sensible default. No magic numbers in code.

### 9.3 Pitfalls

| Pitfall | Impact | Prevention |
|---------|--------|-----------|
| Operator edits appsettings.json manually | Config drift, upgrade overwrites changes | Drift detection (hourly); repair mode regenerates from templates |
| .epcfg signature invalid after manual edit | Installer refuses to use it | Clear error message: "Site configuration pack signature is invalid. Please request a new copy." |
| Environment variable overrides forgotten | Unexpected behavior in production | Installer Agent logs all active configuration sources on startup |
| Default value inappropriate for large PACS | Performance issues | Hardware profile detection: small/medium/large PACS with different defaults |

---

## 10. Summary: Critical Rules for Development Team

1. **Never hardcode** paths, ports, thresholds, or credentials. Everything from configuration.
2. **Every DELETE must write to sync_outbox** in the same transaction (before-state captured).
3. **Every amendment must include reason + approver** (configurable enforcement).
4. **AUTO_INCREMENT seed = pacsid × 10^10** — never manually set AUTO_INCREMENT.
5. **Every state transition writes a checkpoint** (fsync'd, write-then-rename).
6. **PII never appears in logs** — use `[Sensitive]`, `[DoNotLog]`, `[Mask]` attributes.
7. **Heartbeat/sync failures never block business operations** — fire-and-forget with circuit breaker.
8. **Schema migrations classified by DDL algorithm** — COPY on large tables uses pt-online-schema-change.
9. **Backup before every destructive operation** — mandatory, not optional.
10. **Test with power-cut simulation** — every long operation must survive hard shutdown.
