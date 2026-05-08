# ePACS Offline Installer — Critical Analysis and Enhanced Implementation Plan

> **Revision**: v3.2 — Enhanced with supply-chain security (SLSA L3), formal STRIDE threat model, fleet management & HQ-side visibility, operational SLA/SLO matrix, locale/i18n integrity, expanded test rigor (mutation, fuzz, time-skip, locale), installer idempotency invariants, code-signing-key compromise runbook, NTP/root-CA staleness handling for fully air-gapped sites, GPO/domain-policy compatibility, advisory-lock concurrency around DbUp, and operator one-shot diagnostic ping.
>
> **Changes from v3.1**: Added G64–G92 (29 new gaps covering supply chain, threat modeling, fleet management, edge-case resilience, test rigor, idempotency, key compromise, NTP fallback, root-CA staleness, GPO interference, conflict-resolution UX, outbox backpressure, binlog retention, advisory locks, restore-on-different-hardware, locale integrity, health-endpoint throttling, build reproducibility, and operator diagnostic ping). Added Sections 15 (Build Provenance & Supply Chain Security), 16 (Threat Model and Security Architecture), 17 (Fleet Management & HQ Visibility), 18 (Operational SLA/SLO Matrix), 19 (Internationalization & Locale Integrity), 20 (Expanded Validation Test Matrix). Added Appendices I (STRIDE Threat Model), J (GPO/Domain-Policy Compatibility Matrix), K (Operator SLA/SLO Matrix), L (Installer Idempotency Invariants), M (Code-Signing Key Compromise Runbook), N (Expanded Test Matrix). Added R18–R28 to risk register. Updated Section 2 (Locked Decisions) with SLSA, NTP, and root-CA decisions. Updated Section 5.2 (Testing) with mutation, fuzz, time-skip, and locale testing.
>
> **Changes from v3.0**: Added G56–G63 (Traceability module integration, audit DB co-location, partition rotation in offline context, geo-tag capture in rural/GPS-denied areas, power-cut resilience hardening, intermittent connectivity handling, USB media corruption recovery, thermal/environmental stress). Added Section 13 (Rural Resilience Architecture). Added Section 14 (Traceability Module Integration). Added Appendix H (Traceability Integration Checklist).
>
> **Changes from v2**: Added G45–G55 (DDL drift detection, differential schema sync, Percona Toolkit integration, mixed charset remediation, LOB column migration strategy, AUTO_INCREMENT overflow risk, orphan FK handling, online DDL classification, schema fingerprinting, table-level migration ordering, data-only delta sync). Added Section 12 (DDL Drift and Differential Migration Architecture). Updated Phase 2 with Percona Toolkit integration. Added Appendix E–G.
>
> **Changes from v1**: 26 new gaps identified (G19–G44), uninstall workflow added, silent/unattended mode specified, Installer Agent fully defined, configuration drift detection added, log rotation policy added, disk space monitoring added, chaos testing framework specified, DR rehearsal cadence formalized, operator training content plan added, large-dataset migration strategy expanded, offline clock drift handling improved, Garnet monitoring specified, emergency hotfix fast-path added, and all phases restructured with explicit sub-deliverables and dependency gates.

---

## 1. Critical Analysis of the BRD

### 1.1 Strengths (retained from BRD — no changes needed)

- Correct architectural position: signed Windows bootstrapper over native services, not containers, not a loose runbook.
- Strict separation of replaceable binaries (`C:\Program Files\ePACS\`) from durable data (`D:\ePACSData\`).
- MySQL as the single source of truth for business data, audit, and outbox checkpoints. Redis/Kafka are explicitly demoted to transport/cache roles.
- Transactional outbox as the sync anchor, which survives Kafka loss and network outage.
- Stack-release manifest concept with signed SHA-256 payload hashes and compatibility gates (VR-001..VR-006).
- Backup-before-upgrade mandatory; uninstall preserves data by default with a governance-token purge path.
- Clear tamper-evident (not tamper-proof) framing, which is practical and defensible in audit.
- Comprehensive service stop/start ordering (BRD 14.2) with explicit dependency chain.
- Backup package layout (BRD 13.1) is well-structured with per-component encryption and checksums.
- Health endpoint contract (BRD 17.2) provides a clear, testable interface for every service.

### 1.2 Gaps, ambiguities, and risks (original G1–G18 + new G19–G44)

| # | Gap / Risk | Severity | Mitigation |
|---|---|---|---|
| G1 | Redis-compatible product unpinned | High | Locked: Microsoft Garnet (OSS, .NET 8, no licensing). Section 2. |
| G2 | Kafka KRaft single-node fragility; empty-storage metadata-loss risk; JRE supply-chain burden | High | Pin Kafka 3.7.x LTS + Temurin JRE 17; installer writes `meta.properties` atomically; pre-format `log.dirs` with `kafka-storage.sh random-uuid`; never auto-format on existing datadir; backup includes `__cluster_metadata-0/` partition. |
| G3 | Rollback after MySQL migration is not truly reversible for data-destructive changes | High | Side-by-side install via `current` junction is the default for ALL upgrades (not only major). Pre-upgrade backup + datadir copy to staging; commit flips junction. Irreversible DDL (column drops, type changes) requires explicit expand-migrate-contract: Phase A adds new column, Phase B migrates data, Phase C drops old column in next release only after rollback window closes. |
| G4 | NLDR API contract is out of scope of BRD but sync protocol version is pinned in manifest | High | Sync Agent behind `ISyncTransport` abstraction with "sync disabled" default for pilot; activate after NLDR v1 contract is published. Contract version negotiation handshake on first connect. |
| G5 | Key-escrow / restore-to-new-machine path under-specified (DPAPI is machine-bound) | High | Certificate-wrapped ASP.NET DP keyring under `D:\ePACSData\keys\`; recovery cert issued by NLPSV Release CA; escrow copy held by state federation. Documented DR rehearsal each phase. Quarterly key-recovery drill mandatory from Phase 2 onward. |
| G6 | Installer package size likely exceeds 2–3 GB | Medium | Target < 2.5 GB; per-payload 7z-LZMA2 compression; split-volume ZIPs for USB media (4 GB FAT32-safe parts); single-EXE stub re-assembles. Payload delta packages for patch upgrades (binaries-only, ~200 MB). |
| G7 | Schema migration not resumable after power loss mid-script | High | Checkpoint-per-script in `schema_version_registry`; each script wrapped in a transaction where DDL permits; expand-migrate-contract pattern enforced by DbUp. Migration runner detects incomplete checkpoint on restart and resumes from last committed script. |
| G8 | Windows Defender / third-party AV routinely corrupt MySQL/Kafka datadirs | High | Installer writes canonical AV-exclusion PowerShell script + documents enterprise AV exclusion policy; support bundle captures AV config; precheck warns if exclusions are not applied; health check detects quarantined files. |
| G9 | Operator UX decisions (data path, service account passwords, NLDR credentials) have no clear source in a rural field install | High | Signed Site Configuration Pack (`.epcfg`) distributed out-of-band; contains pacs_id, state_code, NLDR endpoint + device cert, backup target, language. Installer consumes this; falls back to guided wizard with sensible defaults. |
| G10 | BRD silent on Web hosting model (IIS vs Kestrel) | Medium | Kestrel self-hosted under Windows Service (no IIS dependency); HTTPS binding via Windows certificate store; reverse proxy optional for DCCB hubs. |
| G11 | Multi-user concurrency model for PACS not defined | Medium | Assume up to 5 concurrent users per PACS, up to 20 at DCCB hub. Drives Kestrel thread pool, MySQL `max_connections`, Garnet memory sizing. Phase 0 confirmation required. |
| G12 | No telemetry / phone-home channel | Medium | Opt-in health manifest upload (versions, last backup, sync lag, disk %) over sync channel. Stubbed `ITelemetrySink` interface only in v1. |
| G13 | Localization for operator UI not addressed | Medium | Installer UI uses `.resx`; ship English + Hindi + one state language for pilot. i18n framework in place from Day 1. RTL not required for v1. |
| G14 | "Governance override" has no concrete mechanism | Medium | Override = signed Override Token (JWT signed by Release CA, TTL-bound, pacs_id-bound); verified by installer; logged to audit trail; single-use nonce to prevent replay. |
| G15 | SBOM tooling and CVE policy unspecified | Medium | CycloneDX + `dotnet-sbom`; build fails on Critical CVEs; monthly CVE re-scan of released artifacts; CVE exception register for known-accepted risks. |
| G16 | Clock drift on rural Windows boxes corrupts audit log hash chain and sync idempotency | Medium | Installer configures w32time with NTP pool fallback; **offline drift detection**: Installer Agent compares system clock against MySQL `NOW()` and last-known-good timestamp from audit log; warns on drift > 30s; blocks sync if drift > 5 min; Sync Agent anchors on server-side timestamp for ordering. |
| G17 | Installer tooling not picked | Medium | WiX v4 + C# Managed BootstrapperApplication. OSS, aligned with .NET skills, no per-seat cost. |
| G18 | `temp` staging on small C: drives will fail | Medium | Installer detects free space; relocates temp to data-volume if C: < 10 GB free; documents in precheck E003. |
| **G19** | **Uninstall workflow missing from plan** | **High** | BRD explicitly requires uninstall with data preservation (AC-007). Implement UNINSTALL mode: stop services → deregister services → remove binaries under `C:\Program Files\ePACS\` → preserve `D:\ePACSData\` by default → purge data only with signed governance Override Token + typed confirmation. |
| **G20** | **Silent/unattended install mode missing** | **High** | BRD FR-06 requires response-file or CLI-switch driven silent install. Implement `/quiet /config:<path-to-epcfg>` mode; all wizard inputs sourced from `.epcfg`; exit codes mapped to specific failure categories; log to file only. |
| **G21** | **Installer Agent responsibilities undefined** | **High** | Define: (1) periodic health polling of all services, (2) configuration drift detection, (3) scheduled backup orchestration, (4) disk space monitoring with alerts, (5) certificate expiry monitoring, (6) log rotation enforcement, (7) AV exclusion verification, (8) clock drift detection, (9) support bundle generation on failure. Runs as `ePACSInstallerAgent` Windows service under LocalSystem. |
| **G22** | **Configuration drift detection absent** | **High** | Installer Agent computes SHA-256 of all generated config files on install/upgrade; stores hashes in `installation_registry`; periodic check (hourly) compares current hashes; drift detected → log warning + flag in health dashboard + include diff in support bundle. Does NOT auto-remediate (operator decision). |
| **G23** | **Log rotation policy missing** | **High** | Structured JSON logs will fill disks without rotation. Policy: daily rolling files, 30-day retention for application logs, 90-day for audit logs, 7-day for MySQL slow/error logs. Installer Agent enforces rotation via scheduled cleanup. Max log volume: 10% of data partition or 50 GB, whichever is smaller. |
| **G24** | **Disk space monitoring and alerting absent** | **High** | Installer Agent monitors data volume free space every 15 min. Thresholds: Yellow < 20% free, Red < 10% free, Critical < 5% free. Yellow → health dashboard warning. Red → operator notification via local alert + block new backups. Critical → block non-essential writes + emergency support bundle. |
| **G25** | **No chaos testing framework specified** | **Medium** | Use Pester + custom PowerShell chaos modules: `Invoke-PowerCutSimulation` (Hyper-V checkpoint + force-stop VM), `Invoke-DiskFullSimulation` (fill volume with temp file), `Invoke-AVInterference` (quarantine random binary), `Invoke-ClockSkew` (shift system time), `Invoke-NetworkPartition` (firewall rule injection). Automated in CI on every release candidate. |
| **G26** | **Large dataset migration testing insufficient** | **Medium** | Add 100 GB and 200 GB test datasets for DCCB hub scenario. Use `mysqlsh util.dumpInstance` for datasets > 5 GB. Migration rehearsal must complete within 2 hours for 100 GB. Include in Phase 2 exit criteria. |
| **G27** | **DR rehearsal cadence not formalized** | **High** | Quarterly DR drill from Phase 2 onward: (1) restore backup to clean machine, (2) verify key recovery with escrow cert, (3) verify sync resume after restore, (4) document results. Annual full-site DR simulation from Phase 5 onward. |
| **G28** | **Operator training content not planned** | **Medium** | Deliverables: (1) 30-min video walkthrough per mode (install/upgrade/backup/restore), (2) laminated quick-reference card (A4, both sides), (3) troubleshooting decision tree poster, (4) WhatsApp/Signal-friendly short guides. All in English + Hindi + pilot state language. |
| **G29** | **Garnet-specific failure modes not monitored** | **Medium** | Garnet is newer than Redis; failure modes may differ. Health check must verify: (1) Garnet process alive, (2) `PING` responds, (3) memory usage within bounds, (4) persistence file exists and is recent. If Garnet fails 3 consecutive health checks, Installer Agent restarts it and logs incident. Abstract behind `ICache` interface to allow Redis OSS fallback if Garnet proves unstable. |
| **G30** | **Emergency hotfix fast-path not defined** | **High** | Hotfix = signed package containing only changed binaries + updated manifest. No schema migration allowed in hotfix. Installer validates hotfix signature, stops affected service(s) only, replaces binaries, restarts, health check. Total downtime target: < 5 min. Hotfix must be promotable to next full release. |
| **G31** | **Repair mode underspecified** | **Medium** | REPAIR mode: (1) verify all payload hashes against manifest, (2) replace any mismatched binaries, (3) regenerate config from templates + current `installation_registry` values, (4) re-apply ACLs, (5) re-register services if missing, (6) restart all services, (7) run health checks. Does NOT touch data or run migrations. |
| **G32** | **Concurrent installer execution not guarded** | **Medium** | Named mutex `Global\ePACSInstaller` acquired at startup; if already held, display "Another installer instance is running" and exit. Stale lock detection via PID check after 30 min timeout. |
| **G33** | **USB media integrity not verified** | **Medium** | Installer stub computes SHA-256 of entire payload archive before extraction; compares against signed manifest embedded in stub. Catches bit-rot and incomplete USB copies. |
| **G34** | **Windows Update reboot during install/upgrade** | **Medium** | Installer sets `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU\NoAutoRebootWithLoggedOnUsers=1` during operation; restores original value on completion. Precheck warns if pending reboot detected. |
| **G35** | **Service recovery actions not specified** | **Medium** | All ePACS Windows services configured with: First failure = restart after 60s, Second failure = restart after 120s, Subsequent = restart after 300s + run support bundle collector. Reset failure count after 24 hours. |
| **G36** | **Backup target validation missing** | **Medium** | Before backup: verify target path exists, is writable, has sufficient free space (estimated backup size × 1.5), is not on same physical volume as data (warn if so). For USB targets: verify filesystem supports file sizes > 4 GB or auto-split. |
| **G37** | **No pre-upgrade MySQL Upgrade Checker integration** | **High** | BRD deep-research explicitly recommends MySQL Upgrade Checker before version transitions. Integrate `mysqlcheck --check-upgrade` + MySQL Shell `util.checkForServerUpgrade()` as mandatory pre-migration gate. Block upgrade if critical issues found. |
| **G38** | **Kafka topic auto-creation risk** | **Medium** | Disable `auto.create.topics.enable=false` in Kafka config. Installer pre-creates required topics (`epacs.local.sync-ready`, `epacs.local.dead-letter`, `epacs.local.commands`) with explicit partition count and retention. Prevents accidental topic proliferation. |
| **G39** | **No graceful degradation when Kafka is down** | **Medium** | Business services must continue operating when Kafka is unavailable. Outbox writes go to MySQL regardless. Outbox.Relay retries Kafka connection with backoff. Health dashboard shows Kafka status but does not block business operations. |
| **G40** | **Backup encryption key rotation not specified** | **Medium** | Backup encryption uses certificate-wrapped symmetric key per backup. Certificate rotation: new cert issued annually; old certs retained in keyring for decrypting old backups; `backup-manifest.yaml` records cert thumbprint used. |
| **G41** | **No network diagnostics for sync troubleshooting** | **Medium** | Support bundle includes: DNS resolution test for NLDR endpoint, TCP connectivity test (port 443), TLS handshake test with cert chain dump, last 10 sync attempts with response codes. Sync health dashboard shows latency histogram. |
| **G42** | **Installer does not handle .NET runtime conflicts** | **Medium** | Self-contained publish eliminates runtime dependency, but other .NET apps on same machine could conflict via shared GAC or environment variables. Precheck scans for conflicting `DOTNET_ROOT` or `ASPNETCORE_ENVIRONMENT` env vars; warns if found. |
| **G43** | **No post-install smoke test beyond health endpoints** | **Medium** | After fresh install or upgrade, run automated smoke test: (1) create test loan record via API, (2) verify it appears in DB, (3) verify outbox entry created, (4) delete test record, (5) verify audit trail. Smoke test uses dedicated `__smoke_test` user. |
| **G44** | **Attachment storage growth unbounded** | **Medium** | Installer Agent monitors `D:\ePACSData\attachments\` size. Warn at 80% of allocated quota (configurable, default 50 GB). Health dashboard shows attachment volume usage. Archival policy: attachments older than configurable threshold (default 2 years) eligible for archive-to-backup-target. |
| **G45** | **DDL drift between installed schema and new release schema is undetected** | **Critical** | Current plan uses DbUp forward-only scripts but has no mechanism to detect if the on-disk schema has drifted from the expected baseline (manual DDL, failed partial migrations, field corrections). Implement schema fingerprinting: on every install/upgrade, capture `INFORMATION_SCHEMA` snapshot (tables, columns, indexes, FKs, views) and store as `schema_fingerprint` in `installation_registry`. Before upgrade, compare on-disk fingerprint against expected baseline for current version. Drift → block upgrade + generate drift report. |
| **G46** | **No differential/delta schema migration strategy** | **Critical** | The schema has 1,057 tables. Full `mysqldump` + reload for every upgrade is impractical for large PACS (100+ GB). Need differential approach: (1) schema-level: DbUp scripts apply only the delta DDL, (2) data-level: Percona XtraBackup for physical incremental backups, (3) sync-level: outbox-based delta for NLDR sync. The plan must distinguish between schema delta (DDL changes) and data delta (row-level changes). |
| **G47** | **Mixed character sets (utf8mb3 vs utf8mb4) cause JOIN failures and migration issues** | **High** | DDL analysis reveals 24 tables using `utf8mb3` (Hangfire tables, Counter, DistributedLock, Hash, Job*, List, Set, Server, State) while 1,033 use `utf8mb4`. Mixed charsets cause implicit conversion in JOINs, index misuse, and potential data truncation during migration. Remediation: Phase 0 must classify all utf8mb3 tables; migration script converts them to utf8mb4 using `ALTER TABLE ... CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci` with `ALGORITHM=COPY` (required for charset conversion). Schedule during maintenance window. |
| **G48** | **4,556 LOB columns (longtext/longblob) severely impact migration and backup performance** | **High** | 681 tables have `SaltValue longblob` and `SerialNumberOfPacs longtext` columns (sync-related). LOB columns prevent `ALGORITHM=INSTANT` for many DDL operations and inflate backup sizes. Migration strategy must: (1) exclude LOB columns from differential checksums, (2) use `--single-transaction --quick` for mysqldump to avoid buffering LOBs in memory, (3) consider `mysqlsh util.dumpInstance` with parallel threads for LOB-heavy tables. |
| **G49** | **AUTO_INCREMENT values suggest composite key generation scheme that may overflow** | **High** | `ln_productpurposemapping` has AUTO_INCREMENT=2.2×10¹⁸ (near BIGINT max of 9.2×10¹⁸). `ln_applicationmain` at 2.1×10¹³, `fa_ledger` at 1.3×10¹⁴. These appear to be PACS-prefixed composite IDs (`idgeneratorforpacs` pattern). Migration must: (1) audit all AUTO_INCREMENT values against BIGINT ceiling, (2) alert if any table is within 50% of max, (3) plan ID space expansion strategy for tables approaching limit. |
| **G50** | **81 foreign keys create migration ordering dependency that DbUp doesn't handle natively** | **High** | FK constraints create a directed dependency graph for DDL operations. `ALTER TABLE` on a parent table can cascade-lock child tables. DbUp executes scripts sequentially but doesn't understand FK topology. Implement: (1) FK dependency graph builder from `INFORMATION_SCHEMA.KEY_COLUMN_USAGE`, (2) topological sort for migration script ordering, (3) option to temporarily disable FK checks during migration with `SET FOREIGN_KEY_CHECKS=0` (already in the DDL dump pattern). |
| **G51** | **No online DDL classification for migration scripts** | **High** | MySQL 8.4 supports INSTANT, INPLACE, and COPY algorithms for ALTER TABLE, with vastly different performance and locking characteristics. Adding a nullable column = INSTANT (milliseconds). Adding an index = INPLACE (seconds-minutes, concurrent DML allowed). Changing column type = COPY (minutes-hours, table locked). Each migration script must be classified by DDL algorithm and estimated duration. Scripts requiring COPY on tables > 1M rows must use `pt-online-schema-change` instead. |
| **G52** | **Percona Toolkit not integrated for safe DDL and data verification** | **High** | For a 1,057-table schema with large datasets, Percona Toolkit provides critical capabilities: (1) `pt-online-schema-change` for non-blocking DDL on large tables, (2) `pt-table-checksum` for verifying data consistency after migration, (3) `pt-table-sync` for repairing data drift between old and new schema, (4) Percona XtraBackup for physical incremental/differential backups. Integrate as optional but recommended tooling alongside DbUp. |
| **G53** | **No table-level migration ordering based on size and criticality** | **Medium** | Migration scripts should process tables in a specific order: (1) master/reference tables first (cm_* prefix, 152 tables), (2) then transactional tables (ln_*, fa_*, sca_*, trm_*), (3) then reporting/temp tables last. Large tables (> 1M rows) should be migrated with progress reporting. Hangfire tables (12 tables) should be truncated and rebuilt rather than migrated. |
| **G54** | **Data-only delta sync for NLDR not designed** | **High** | The outbox pattern handles new transactions, but NLDR also needs to receive master data changes (new members, new products, policy changes). Only 9 tables have `updated_at` with `ON UPDATE CURRENT_TIMESTAMP`; 194 tables have `DataEntryWorkingDate` but it's set only on INSERT. Delta tracking for master data requires: (1) add `updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP` to all sync-eligible tables, (2) or use MySQL binary log parsing for CDC, (3) or use trigger-based change capture into the outbox. Recommend option (1) as a migration step + outbox writes for master data changes. |
| **G55** | **42 views may break during schema migration if underlying tables change** | **Medium** | Views depend on specific column names and types. If a migration renames or drops a column referenced by a view, the view becomes invalid silently (MySQL doesn't enforce view dependencies at DDL time). Migration framework must: (1) drop all views before table migrations, (2) recreate views after migrations using versioned view definitions in `/migrations/R__*.sql` (repeatable migrations), (3) validate all views post-migration with `CHECK TABLE ... FOR UPGRADE`. |
| **G56** | **Traceability module (11 partitioned tables) not integrated into installer lifecycle** | **Critical** | The `Intellect.Erp.Traceability` module uses a separate `erp_traceability` database with 11 tables (4 partitioned by `RANGE(TO_DAYS(OccurredAtUtc))`), its own EF Core migrations, ULID-based PKs, and a dedicated outbox (`AuditOutbox`). The installer must: (1) create the `erp_traceability` database alongside the main DB, (2) run Traceability EF Core migrations as part of the upgrade pipeline, (3) manage partition rotation (monthly) via Installer Agent, (4) include Traceability DB in backup/restore, (5) include Traceability health check in overall health dashboard. |
| **G57** | **Audit DB partition rotation requires scheduled DDL in offline environment** | **High** | Traceability uses `PARTITION BY RANGE (TO_DAYS(OccurredAtUtc))` with monthly partitions. New partitions must be pre-created before the month starts; old partitions must be archived per retention policy (90 days to 10 years). In an offline PACS, there's no central scheduler. Installer Agent must: (1) pre-create 3 months of future partitions on install/upgrade, (2) run monthly partition maintenance (add next month, archive expired), (3) handle partition creation failure gracefully (audit writes must not fail). |
| **G58** | **Geo-tag capture unreliable in rural/hilly/indoor environments** | **High** | The Traceability module captures Latitude, Longitude, GeoHash, GeoAccuracyMeters, and LocationSource. In rural India: GPS may be unavailable indoors, cell tower triangulation is inaccurate in hilly terrain, and many PACS operate from buildings without GPS signal. Strategy: (1) PACS site coordinates stored in `.epcfg` as fallback geo-tag, (2) LocationSource enum: GPS, CellTower, WiFi, SiteConfig, Manual, (3) if device GPS unavailable, use site coordinates with LocationSource=SiteConfig, (4) never block business operations for missing geo-tag — record with NULL + LocationSource=Unavailable. |
| **G59** | **Bare-metal power-cut mid-operation is a realistic daily scenario** | **Critical** | UPS is not guaranteed at all PACS sites. The installer and all services must survive hard power loss at any point. Hardening: (1) MySQL `innodb_flush_log_at_trx_commit=1` + `sync_binlog=1` (already default, but enforce in `my.ini`), (2) Kafka `flush.messages=1` + `flush.ms=1000`, (3) Garnet AOF with `fsync=always`, (4) installer state machine writes checkpoint to disk before each phase transition, (5) on restart, Installer Agent detects incomplete state and enters RECOVERY mode, (6) all backup operations use write-then-rename pattern (atomic on NTFS), (7) migration runner resumes from last checkpoint, (8) DbUp scripts use `START TRANSACTION` / `COMMIT` where DDL permits. |
| **G60** | **4G connectivity drops daily — sync must handle mid-transfer interruption** | **High** | Sync Agent must: (1) use chunked uploads with per-chunk acknowledgment, (2) resume from last acknowledged chunk on reconnect (not restart from beginning), (3) implement exponential backoff with jitter (1s → 2s → 4s → ... → 5 min max), (4) circuit breaker opens after 5 consecutive failures, half-opens after 5 min, (5) all sync state persisted in MySQL (not in-memory), (6) connectivity probe: lightweight HTTPS HEAD to NLDR endpoint every 60s when circuit is open, (7) bandwidth-aware: detect 2G/3G/4G and adjust chunk size (4G: 1 MB chunks, 3G: 256 KB, 2G: 64 KB). |
| **G61** | **USB media corruption is common with cheap flash drives in hot/humid conditions** | **High** | Indian rural conditions: high humidity, temperatures 35–45°C, cheap USB drives with poor NAND quality. Mitigation: (1) installer payload has embedded SHA-256 manifest (G33), (2) if verification fails, display specific error: "Installation media may be damaged. Please request a new copy.", (3) support split-volume ZIPs with per-part checksums (if one part is corrupt, only that part needs re-copy), (4) installer can resume from last verified payload if power-cut during extraction, (5) recommend USB 3.0+ drives in operator guide, (6) include media verification tool that operators can run independently before calling installer. |
| **G62** | **Thermal throttling on low-end hardware during long operations** | **Medium** | 8 GB RAM + SSD machines in 40°C+ environments may thermal-throttle during long migrations or backups. Mitigation: (1) migration runner monitors system temperature via WMI (`Win32_TemperatureProbe`) where available, (2) if temperature > 85°C, pause migration for 5 min cooldown, (3) backup operations use `--parallel=2` (not 4) on small PACS hardware profile, (4) installer progress UI shows estimated time remaining (reduces operator anxiety during long operations), (5) document recommended operating temperature range in operator guide. |
| **G63** | **Traceability anomaly rules (impossible travel, volume spike) need offline-aware thresholds** | **Medium** | The 8 anomaly rules in the Traceability module are designed for online environments. In offline PACS: (1) AN-01 (Impossible Travel) — irrelevant for single-site PACS; disable or set radius to site boundary, (2) AN-05 (Out-of-Hours) — rural PACS may operate at non-standard hours; configure per-site hours in `.epcfg`, (3) AN-07 (Volume Spike) — EWMA baseline needs local calibration; seed with 30-day rolling average after pilot, (4) AN-04 (Maker-Checker) — critical for loan approvals; keep active with strict thresholds. Configure via `appsettings.Traceability.Anomaly` section generated from `.epcfg`. |
| **G64** | **No installer self-update / signed update channel for the installer itself** | **High** | Currently the plan assumes the installer is delivered via USB and never updated in-place. If a critical installer bug is discovered post-distribution, every site must be revisited physically. Implement: (1) "Installer-only patch" package format — a tiny signed bundle that updates only the bootstrapper + agent + scripts, no payloads, (2) Installer Agent on next sync window pulls a signed `installer-update-manifest.json` from NLDR (HEAD only — small), (3) operator confirms "Update installer to v3.2.4" via local prompt or `.epcfg` directive, (4) updated installer staged side-by-side and activated atomically. Version-pin policy: only N → N+1 self-update; major upgrades remain USB-delivered. |
| **G65** | **Installer Agent timer collisions cause race conditions** | **High** | Multiple agent timers (backup at 02:00, log rotation at 02:00, partition rotation on 1st 02:00, drift detection hourly, disk monitor 15-min) can collide and amplify load on small hardware or saturate I/O simultaneously. Implement: (1) global scheduler with priority queue + exclusive-resource locks (e.g., "DB-heavy", "disk-write-heavy"), (2) jitter window per task (±300s), (3) explicit lease tokens — only one DB-heavy operation at a time, (4) cooperative cancellation: low-priority tasks yield if high-priority is queued, (5) all schedules persisted in `installer_schedule` table with last-run/next-run/last-result. |
| **G66** | **Outbox unbounded growth when NLDR is offline for extended periods** | **High** | A 30+ day outage at NLDR or extended PACS disconnection causes `sync_outbox` to grow without bound, eventually exhausting disk and choking MySQL. Implement: (1) soft ceiling 500K events → health dashboard YELLOW + slow-down on optional events, (2) hard ceiling 2M events → health dashboard RED + telemetry/non-financial events deferred to a `sync_outbox_overflow` cold-storage table, (3) financial transactions NEVER deferred — they must always reach NLDR, (4) audit-log priority preserved, (5) operator-visible "estimated days until ceiling" gauge. |
| **G67** | **Build provenance, supply-chain integrity, and reproducible builds not formalized** | **Critical** | EV signing alone is insufficient against build-pipeline compromise (XZ-style, SolarWinds-style). Adopt SLSA Level 3 minimum: (1) hermetic builds in pinned VM image with no external network during compile/sign, (2) signed in-toto provenance attestations per artifact, (3) SBOM cross-checks at install time (manifest carries SBOM hash; installer recomputes SBOM of installed files and compares), (4) two-person rule for production signing, (5) pipeline IaC checked into git with required reviewer, (6) reproducible builds verified by independent rebuild on a separate VM image and bit-for-bit comparison of unsigned outputs. |
| **G68** | **Code-signing key compromise has no kill-switch / revocation procedure** | **Critical** | If the EV signing key is exfiltrated, every existing PACS will trust forged installers. Implement: (1) embedded short-TTL signing-key thumbprint allow-list inside Installer Agent (refreshed via signed config), (2) rotation cadence: signing key rotated annually + emergency rotation procedure (within 24h target), (3) CRL/OCSP stapling — installer also verifies a signed Anti-Tamper Allowlist (ATA) issued by NLPSV Release CA, (4) ATA can blacklist specific compromised cert thumbprints; PACS pull ATA on every sync window, (5) compromise runbook (Appendix M): notify, revoke, issue new cert, sign + distribute new ATA, audit which PACS may have installed forged artifacts. |
| **G69** | **Service-to-service authentication missing — localhost-only is single line of defense** | **High** | Currently DB/cache/eventing are localhost-only. If any service has SSRF/RCE, peer services have no authentication. Defense-in-depth: (1) MySQL: per-service users with column-level grants; service caches credentials encrypted with DPAPI + cert; (2) Garnet: AUTH password per service; (3) Kafka: SASL/PLAIN over `localhost` with per-service principals; (4) cross-service HTTP: mTLS via local CA generated at install (each service has its own client cert); (5) all secrets stored in encrypted credentials store with rotation. |
| **G70** | **NTP fully unavailable in air-gapped sites — w32time pool fallback fails** | **High** | The plan mentions NTP pool as fallback for w32time, but truly offline sites have no NTP. Without trusted time, audit-log hash-chain anchoring degrades, and TLS cert validity can drift. Implement: (1) optional bundled GPS-time-source USB (recommended for air-gapped sites), (2) DCCB hub acts as local stratum-2 NTP server when reachable on PACS LAN/WAN, (3) Installer Agent computes drift relative to last-known-good via `audit_log` hash-chain anchor + MySQL `NOW()` and warns if monotonicity violated, (4) document operator procedure: weekly "time-anchor check" against state federation phone-call confirmed timestamp, (5) leap-second handling: smear leap seconds via Microsoft's smear policy. |
| **G71** | **Windows root-CA store goes stale on offline machines (cert chain validation breaks)** | **High** | Windows root certs are updated via Windows Update; offline boxes accumulate stale roots. NLDR client cert validation may fail when root rotates. Implement: (1) installer bundles a curated root-CA bundle (Mozilla NSS + Microsoft CTL snapshot) refreshed per release, (2) Installer Agent applies the bundle to LocalMachine root store on install/upgrade, (3) bundle is signed and verified, (4) version of the bundle recorded in `installation_registry`, (5) explicit "root CA staleness" health check; > 18 months → YELLOW, > 24 months → RED. |
| **G72** | **Group Policy / domain policies override installer settings without notice** | **Medium** | Domain-joined PACS (DCCB hubs) may have GPO that disables services, blocks ports, forces AV scans, or removes scheduled tasks. Implement: (1) GPO compatibility matrix (Appendix J) listing required GPO settings, (2) precheck reads applied policies via `gpresult /r /scope:computer` and warns on conflicts, (3) Installer Agent monitors for policy-induced changes (e.g., service disabled by GPO) and surfaces clearly (not as "drift"), (4) document GPO exceptions for ePACS in operator + DCCB IT guides, (5) standalone (non-domain) install mode supported with reduced precheck strictness. |
| **G73** | **Locale and i18n integrity not validated end-to-end (Indic scripts, BOM, sort order)** | **Medium** | Hindi/Marathi/Telugu/Tamil names with conjuncts and matras require correct utf8mb4 collation; `.epcfg` JSON files may have UTF-8 BOMs that some parsers reject; Windows non-English locale alters file paths and date formats. Implement: (1) explicit BOM strip in `.epcfg` parser + canonicalized JSON for signature verification, (2) collation choice `utf8mb4_0900_ai_ci` validated against Indic test corpus (Section 19), (3) installer tested in Hindi/Marathi Windows locales, (4) date/time always serialized in ISO 8601 + UTC; localized presentation only at UI layer, (5) filename normalization (NFC) for attachments to prevent dup-uploads from Windows/macOS clients. |
| **G74** | **Test coverage gaps: no mutation testing, fuzz testing of `.epcfg`, locale matrix, time-skip** | **Medium** | Current testing covers happy paths and chaos; misses subtle correctness regressions. Add: (1) Stryker.NET mutation testing on Installer.Core, BackupRestore, Sync.* with target ≥ 60% mutation score, (2) fuzz testing of `.epcfg` parser using SharpFuzz with 1M inputs, (3) locale test matrix: en-US, en-IN, hi-IN, mr-IN, te-IN Windows locales × installer flows, (4) time-skip integration tests that fast-forward through partition rotation, cert expiry, log rotation, backup retention, (5) backup-restore across MySQL minor versions (8.4.0 → 8.4.x). |
| **G75** | **Installer idempotency invariants not formalized (re-running install must produce identical state)** | **High** | Operators in stress conditions may re-run install. Without idempotency invariants, each retry can corrupt state subtly. Define and test (Appendix L): (1) all file writes are write-then-rename atomic, (2) ACL operations use SDDL set, not delta, (3) service registration uses `sc create` with `OBJ` argument that overwrites existing, (4) database creation uses `CREATE DATABASE IF NOT EXISTS`, (5) DbUp's checkpoint table prevents script re-execution, (6) registry writes use deterministic key paths, (7) all idempotency invariants asserted in integration test "run-install-twice-and-diff-state". |
| **G76** | **No advisory lock around DbUp / migration runner — concurrent installer execution can corrupt schema_version_registry** | **High** | Although G32 introduces a process-level mutex, a remote scheduled task and the local installer could still attempt migrations concurrently across Windows sessions or RDP. Implement: (1) MySQL advisory lock `SELECT GET_LOCK('epacs_migration', 0)` at top of migration runner; non-zero return → already held → exit with E021, (2) lock released on transaction end or process exit, (3) lock owner records: pid, hostname, started_at — visible in support bundle, (4) integration test verifies two concurrent runners produce exactly one applied migration. |
| **G77** | **Percona XtraBackup target filesystem requirement not documented; USB/network backup target may corrupt** | **High** | XtraBackup writes datadir copies via raw page copy + LSN tracking. The target must support sparse files and atomic rename for crash-consistent recovery. USB FAT32 lacks proper rename semantics; SMB shares add latency that breaks LSN tracking. Document: (1) physical (XtraBackup) backups MUST be staged on local NTFS/ReFS; copy to USB/SMB only after staging completes and manifest is sealed, (2) precheck rejects USB/SMB as XtraBackup direct target, (3) logical (mysqlsh) backups can stream directly to USB, (4) backup-target validation (G36) extended with filesystem capability detection. |
| **G78** | **MySQL binlog retention not pinned — uncontrolled growth or premature deletion** | **Medium** | Binlog disk usage on a busy DCCB hub can balloon; conversely, if MySQL prunes binlogs aggressively, point-in-time recovery becomes impossible. Set in `my.ini`: `binlog_expire_logs_seconds = 1209600` (14 days), `max_binlog_size = 100M`, `binlog_row_image = MINIMAL` (default for 8.4 is FULL — switching to MINIMAL trades some restore richness for ~70% size reduction; verify against PITR requirements before flipping), `sync_binlog = 1`. Installer Agent monitors binlog volume; warns if > 20% of data partition. |
| **G79** | **Health endpoint hammering causes MySQL load on small hardware** | **Medium** | If a remote dashboard or watchdog hits `/health/ready` every 5 seconds, it queries MySQL every 5 seconds. On 8 GB RAM hardware this is non-trivial. Implement: (1) `/health/ready` cached for 10 seconds (deep checks); `/health/live` cached for 1 second (process-level only), (2) deep check rotation: probe MySQL on minute-mark, Kafka on minute+15s, Garnet on minute+30s, (3) explicit "max health probe rate" of 12/min documented in BRD AC update, (4) rate-limit at Kestrel layer (e.g., 60 req/min per source IP). |
| **G80** | **No operator one-shot diagnostic ping for "is everything OK?"** | **Medium** | Operators need a single command/click to confirm overall health quickly. Implement: `epacs-ping.exe` (ships in `C:\Program Files\ePACS\tools\`) — runs ~10 checks (services up, DB writable, disk free, last backup recent, sync state OK, cert expiry > 30d, schema fingerprint match, smoke test PASS, Traceability OK, partition health) and prints a one-page green/yellow/red summary in operator's language. Integrated as a desktop shortcut after install. Output also saved to `D:\ePACSData\diagnostics\ping-<timestamp>.txt` for support. |
| **G81** | **No SLA/SLO matrix for installer team (operations cadence implicit)** | **Medium** | Without explicit SLOs, support team and engineering disagree on response times. Define (Appendix K): (1) installer build/sign turnaround ≤ 48h from PR merge, (2) hotfix issuance ≤ 24h from confirmed P1, (3) support bundle triage ≤ 4 business hours, (4) DR drill cadence quarterly with sign-off (G27), (5) per-PACS uptime target 99.0% (excluding scheduled maintenance), (6) installer crash rate < 0.5% of executions, (7) backup verification success ≥ 99.9% across fleet, (8) sync-restore success ≥ 99% within 1 hour of reconnection. |
| **G82** | **STRIDE threat model not formalized** | **High** | Implicit threat coverage exists but no formal STRIDE walkthrough. Produce (Appendix I): (1) Spoofing (forged installer, forged `.epcfg`, forged Override Token, forged sync packets), (2) Tampering (binary tampering, schema tampering, audit-log tampering, backup tampering), (3) Repudiation (operator denies running upgrade, sync ACK forgery), (4) Information Disclosure (secrets in support bundle, attachments leak, plaintext keys, sync payload sniffing), (5) Denial of Service (disk fill, DB overload, backup loop, AV interference), (6) Elevation of Privilege (service-to-service hop, override-token replay, key-store extraction). Each threat → mitigation reference + test case. |
| **G83** | **No conflict-resolution UI for sync conflicts requiring operator decision** | **Medium** | BRD 12.6 lists conflict types; some require operator judgment (e.g., locally edited member record vs centrally edited). Currently the plan describes the resolution rules but not where the operator sees and acts on conflicts. Add: (1) "Sync Conflicts" page in ePACS Web with each conflict showing local vs central, (2) actions: accept-central (default), accept-local-with-override-token, defer to district, (3) accept-local requires Override Token (G14) with reason + checker approval, (4) all operator decisions logged to Traceability. |
| **G84** | **Backup retention not adaptive when storage is constrained** | **Medium** | Plan specifies "7 daily incrementals + 4 weekly fulls". On disk-pressure sites, this constant retention may starve out new backups. Implement: (1) when free space < 20%, evict oldest weekly full first (after verifying NLDR has been synced through that point), (2) maintain at minimum: latest full + last 3 daily incrementals + latest pre-upgrade backup, (3) operator-overridable "do not evict" flag per backup, (4) eviction log in audit trail. |
| **G85** | **Restore on different hardware profile (e.g., from small PACS to DCCB hub) not validated** | **Medium** | Restoring a 4-CPU/8 GB backup onto a 16-CPU/32 GB host means MySQL config (buffer pool, max connections) carried over from backup is sub-optimal; conversely going down may OOM. Implement: (1) restore-engine reads backup's `installation_registry` for source hardware profile, (2) prompts "target machine differs from source — apply target profile config?" (default Yes), (3) regenerates `my.ini`, `kafka.properties`, Garnet limits from target hardware profile, (4) test matrix covers small→hub, hub→small, same-class restores. |
| **G86** | **No automated supply-chain SBOM cross-check at install time** | **High** | Manifest declares payloads + SHA-256, but the SBOM produced at build time is not verified at install. Implement: (1) installer recomputes a runtime SBOM after extraction (file list + hashes + .NET assembly metadata), (2) compares against bundled `release-sbom.json`, (3) any mismatch → block install + alert, (4) optional CVE check against bundled offline NVD snapshot (≤ 30 days old) — warn on Critical CVEs in installed components. |
| **G87** | **Operator cannot prove "I ran a backup before X happened" — backup chain attestation** | **Medium** | Backups exist but there's no signed timeline/chain. Implement: (1) each backup manifest references previous backup's signature (Merkle-style chain), (2) chain signed by Installer Agent's per-PACS attestation key, (3) NLDR receives the chain (lightweight — just signed manifest hashes) for tamper-evident audit, (4) any gap or out-of-order entry surfaces in NLDR audit dashboard. |
| **G88** | **No "release readiness gate" automated check before pushing media to USB** | **Medium** | Currently relies on phase exit criteria. Add automated end-to-end gate: (1) signed artifacts present, (2) SBOM present and valid, (3) reproducible-build verification passes, (4) clean-VM fresh install + upgrade + restore + uninstall + repair + hotfix all pass within 24h, (5) chaos suite passes, (6) tamper negative tests pass, (7) signed release-readiness attestation generated by CI, (8) two-person manual approval recorded. Gate output: `release-attestation.json` (signed) — required to mint USB media. |
| **G89** | **Decimal/numeric precision drift across MySQL versions or charset migrations** | **Medium** | Financial tables (fa_ledger, ln_*) use `DECIMAL(p,s)`. ALGORITHM=COPY operations may silently change precision/scale defaults. Implement: (1) schema fingerprint includes column precision/scale, (2) drift detection flags any decimal column whose precision/scale changed unexpectedly, (3) post-migration validator runs `SELECT SUM(...)` on key ledger columns and compares against pre-migration sum (within tolerance for expected new transactions). |
| **G90** | **No installer-to-installer compatibility test on N+2 upgrades (skip-version)** | **Medium** | Plan tests N-2 → N-1 → N. But field reality: a site may skip a version (medical leave, holiday). Test N-2 → N (skip N-1): (1) DbUp must detect skipped version and apply both deltas, (2) expand-migrate-contract phases must compose correctly across two version jumps, (3) integration test for every supported skip path. Document: maximum 2-version skip; > 2 versions requires staged upgrade. |
| **G91** | **Garnet persistence (AOF) corruption recovery not specified** | **Medium** | Garnet AOF can corrupt under power-cut despite `fsync=always` (page-tear). Implement: (1) Installer Agent verifies Garnet AOF integrity on service start; if corrupt → quarantine AOF + start with empty cache (cache is non-authoritative; refilled from MySQL), (2) corruption event logged to audit trail, (3) automatic Garnet snapshot every 6 hours as additional safety, (4) snapshot included in backup. |
| **G92** | **Secrets in `.epcfg` not protected at rest on operator's laptop** | **Medium** | The signed `.epcfg` contains NLDR client cert thumbprint and possibly DB seed credentials. If the operator's laptop is lost between sites, these can be extracted. Implement: (1) `.epcfg` distribution mechanism: state federation portal generates per-site, per-installer-run, time-bound `.epcfg` (TTL 7 days), (2) `.epcfg` is encrypted with the target PACS's per-site public key (issued at site registration), (3) installer decrypts with PACS private key (stored in TPM if available), (4) revocation: `.epcfg` carries a `nonce` recorded once consumed; replay rejected. |

---

## 2. Locked Decisions

| Area | Decision | Rationale |
|---|---|---|
| Redis-compatible service | Microsoft Garnet (latest stable), runs as `ePACSCache` Windows service | OSS, .NET 8 native, no licensing cost, Windows-first; `ICache` abstraction allows fallback |
| Kafka | Kafka 3.7.x LTS + Temurin JRE 17 LTS, KRaft mode (no ZooKeeper) | LTS stability; KRaft eliminates ZooKeeper dependency; Temurin is OSS with no licensing |
| Installer tooling | WiX v4 Burn + C# Managed BootstrapperApplication | OSS, .NET-aligned, mature chain/rollback model, no per-seat cost |
| .NET publish mode | Self-contained, win-x64, .NET 8 LTS (pinned patch level) | Eliminates runtime drift across field sites; no framework install dependency |
| Web host | Kestrel behind Windows Service (no IIS) | Zero IIS dependency; simpler deployment; HTTPS via Windows cert store |
| Database | MySQL 8.4 LTS (ZIP distribution, installer-configured `my.ini`) | ZIP avoids MSI conflicts; installer owns full config lifecycle |
| Migration tool | DbUp (.NET native, scripts under `/migrations/V###__*.sql`) | .NET native; checkpoint-per-script; transaction wrapping; expand-migrate-contract enforceable |
| CI/CD | Azure DevOps Pipelines with self-hosted Windows build agent for signing | On-prem build agent control; HSM/Key Vault integration; artifact retention policies |
| Signing | Authenticode EV cert + RFC 3161 time-stamping; HSM or Azure Key Vault-backed | Tamper-evident; trusted by Windows SmartScreen; timestamp survives cert expiry |
| SBOM | CycloneDX JSON per payload + aggregated release SBOM | Industry standard; `dotnet-sbom` integration; machine-readable for CVE scanning |
| Backup encryption | AES-256-GCM with certificate-wrapped symmetric key | Per-backup unique key; cert-based wrapping enables restore-to-new-machine |
| Log format | Structured JSON with daily rolling files | Machine-parseable; supports correlation_id; compatible with future log aggregation |
| Silent install | `/quiet /config:<path>` CLI mode with `.epcfg` response file | Enables centrally managed rollouts per BRD FR-06 |
| DDL migration (large tables) | Percona `pt-online-schema-change` for COPY-algorithm DDL on tables > 1M rows | Non-blocking; trigger-based shadow copy; proven at scale; OSS |
| Physical backup (incremental) | Percona XtraBackup 8.4 for daily incremental + weekly full | Physical-level incremental; no table locking; 10x faster than logical for large DBs |
| Data consistency verification | Percona `pt-table-checksum` + `pt-table-sync` post-migration | Chunk-based checksumming; detects row-level drift; can auto-repair |
| Schema diff tool | Custom `INFORMATION_SCHEMA`-based fingerprinter + `mysqldiff` for human review | Detects DDL drift before upgrade; generates remediation scripts |
| Supply-chain compliance | SLSA Level 3 with in-toto provenance, hermetic builds, two-person sign-off, reproducible-build verification | Defense against build-pipeline compromise; aligns with Anthropic/govt sourcing trends |
| Service-to-service auth | Per-service MySQL/Garnet/Kafka credentials + per-service local mTLS via installer-generated CA | Defense in depth beyond localhost-only; survives single-service compromise |
| Time source | w32time → DCCB hub stratum-2 → optional GPS USB receiver → audit-anchor monotonicity check | Air-gapped sites still get monotonic time and tamper-evident audit chain |
| Root CA bundle | Curated Mozilla NSS + Microsoft CTL snapshot, signed and shipped per release | Offline machines stay current; staleness detection in agent |
| Installer self-update | Installer-only patch packages (small, signed); N → N+1 only; major upgrades remain USB | Critical bug fixes propagate without site visit; major upgrades retain governance |
| Code-signing key compromise response | ATA (Anti-Tamper Allowlist) signed by Release CA + embedded thumbprint allow-list, refreshed via sync | Rapid revocation without re-deploying installer to every site |
| Conflict-resolution UX | "Sync Conflicts" page in ePACS Web; central wins by default; local override requires Override Token | Operator visibility into governed conflicts; auditable decisions |
| Outbox backpressure | Soft 500K (YELLOW), hard 2M (overflow to cold table); financial events never deferred | Bounded growth under extended NLDR outage |
| Locale baseline | UTF-8 NFC normalization + utf8mb4_0900_ai_ci + ISO 8601 UTC + tested in en-US/en-IN/hi-IN/mr-IN | Indic script integrity across the stack |

---

## 3. Target Architecture

### 3.1 Runtime topology (per PACS node)

```
┌─────────────────────────────────────────────────────────────────┐
│  Windows 10/11 Pro or Server 2019+ (x64, NTFS/ReFS data vol)   │
├─────────────────────────────────────────────────────────────────┤
│  C:\Program Files\ePACS\                                        │
│    current\ ──junction──> releases\3.2.1\                       │
│    releases\3.2.1\                                              │
│    releases\3.2.0\ (retained until hypercare sign-off)          │
│    tools\  (support bundle, backup CLI, smoke test)             │
├─────────────────────────────────────────────────────────────────┤
│  D:\ePACSData\  (or NTFS mount folder on single-drive hosts)    │
│    mysql\data\          mysql\logs\                              │
│    cache\               eventing\data\    eventing\logs\        │
│    attachments\         keys\             backups\              │
│    logs\<service>\      config\           sync\                 │
│    temp\  (installer staging area)                              │
├─────────────────────────────────────────────────────────────────┤
│  Windows Services (start order / stop order is inverse):        │
│    10. ePACSMySQL        (MySQL 8.4 LTS)                        │
│    20. ePACSCache        (Garnet)                               │
│    30. ePACSEventing     (Kafka KRaft single-node)              │
│    40. ePACS-Loans, ePACS-Fas, ePACS-Membership, ...           │
│    50. ePACSWeb          (Kestrel, HTTPS on PACS LAN)           │
│    60. ePACSSync         (outbox → NLDR)                        │
│    70. ePACSInstallerAgent (always-on health/ops worker)        │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 Installer Agent responsibilities (G21 — fully specified)

The `ePACSInstallerAgent` is a lightweight .NET worker service that runs continuously and owns operational automation:

| Responsibility | Frequency | Action on failure |
|---|---|---|
| Service health polling (all ePACS services) | Every 60s | Log warning; after 3 consecutive failures, attempt restart; after 5, generate support bundle |
| Configuration drift detection (G22) | Every 60 min | Log warning + flag in health dashboard; include diff in support bundle |
| Scheduled backup orchestration | Per policy (daily/weekly) | Retry once after 15 min; if still fails, log error + alert in dashboard |
| Disk space monitoring (G24) | Every 15 min | Yellow/Red/Critical thresholds with escalating actions |
| Certificate expiry monitoring | Every 6 hours | 60/30/7-day warnings in health dashboard |
| Log rotation enforcement (G23) | Daily at 02:00 local | Delete logs beyond retention; compress logs older than 7 days |
| AV exclusion verification | Daily at startup | Warn if expected exclusion paths are not in Windows Defender config |
| Clock drift detection (G16) | Every 30 min | Warn > 30s; block sync > 5 min; log to audit trail |
| Support bundle generation | On demand + on critical failure | Auto-generate on service crash or upgrade failure |
| Smoke test execution (G43) | After install/upgrade + daily | Log results; flag failures in health dashboard |

### 3.3 Solution / repository layout

```
/src
  /Installer.BootstrapperApp      # C# Managed BA for WiX Burn
  /Installer.Core                 # manifest model, signature/hash verify, state machine, lock manager
  /Installer.Core.SchemaMigration # enhanced DbUp runner, DDL classifier, pt-osc wrapper, schema fingerprinter
  /Installer.Core.Idempotency     # invariants enforcer (Appendix L)
  /Installer.Actions              # pre-check, data-root, ACL, service orchestration, migration, backup
  /Installer.Actions.Uninstall    # uninstall workflow with governance token verification
  /Installer.Actions.Repair       # repair workflow: hash verify, binary replace, config regen
  /Installer.Actions.Hotfix       # emergency hotfix fast-path
  /Installer.Actions.SelfUpdate   # installer-only patch / self-update (G64)
  /Installer.UI                   # WPF operator UI (i18n via .resx)
  /Installer.CLI                  # silent/unattended mode CLI entry point
  /Installer.Agent                # ePACSInstallerAgent Windows service (Section 3.2)
  /Installer.Agent.DriftDetector  # config drift detection module
  /Installer.Agent.DiskMonitor    # disk space monitoring module
  /Installer.Agent.LogRotator     # log rotation enforcement module
  /Installer.Agent.SmokeTest      # post-install/daily smoke test runner
  /Installer.Agent.Scheduler      # global scheduler with priority + lease (G65)
  /Installer.Agent.FleetManifest  # fleet health manifest composer (Section 17)
  /Installer.Agent.AOFGuard       # Garnet AOF integrity + snapshot (G91)
  /Installer.Diagnostics.Ping     # epacs-ping CLI tool (G80)
  /Installer.Security.ATA         # ATA allow-list verification + refresh (G68)
  /Installer.Security.RootCA      # root-CA bundle apply + staleness (G71)
  /Installer.Security.ServiceAuth # local CA + per-service mTLS + credential cache (G69)
  /Installer.Security.SBOMCheck   # install-time SBOM cross-check (G86)
  /Installer.Security.Locale      # locale-safe parsing analyzer + corpus tests (G73)
  /Installer.Security.GPOCheck    # gpresult parsing + compatibility check (G72)
  /Installer.Security.Time        # NTP fallback + audit-anchor monotonicity (G70)
  /Installer.Provenance           # in-toto attestation reader/verifier (G67)
  /BackupRestore                  # backup/restore workflows, manifest signing, target validation
  /BackupRestore.XtraBackup       # Percona XtraBackup integration (incremental/differential physical backup)
  /BackupRestore.Chain            # backup chain attestation (G87)
  /BackupRestore.Retention        # adaptive eviction policy (G84)
  /BackupRestore.CrossProfile     # cross-profile restore (G85)
  /ManifestVerifier               # signed release manifest parsing and verification
  /SupportBundle                  # collector with secret redaction + network diagnostics
  /Sync.Abstractions              # ISyncTransport interface
  /Sync.Agent                     # Sync Agent worker service
  /Sync.Agent.Backpressure        # outbox ceilings + overflow (G66)
  /Sync.Agent.ConflictUI          # operator conflict-resolution backend (G83)
  /Sync.Transport.Http            # NLDR HTTP transport (pluggable)
  /Outbox.Relay                   # MySQL → Kafka relay with graceful Kafka-down handling
  /SharedKernel                   # health contracts, logging, config injection, ICache abstraction
/services
  /ePacs.Web                      # Kestrel host + health dashboard + sync dashboard
  /ePacs.Loans /ePacs.Fas /ePacs.Membership /...
/migrations
  /V###__*.sql                    # versioned forward migrations (with DDL classification headers)
  /R__*.sql                       # repeatable migrations (views, procs)
  /pre-checks/                    # MySQL Upgrade Checker scripts per version transition
  /charset-remediation/           # utf8mb3→utf8mb4 conversion scripts
  /schema-baselines/              # expected schema fingerprints per version
/packaging
  /wix                            # .wxs, Bundle.wxs, payload manifest
  /payloads                       # mysql.zip, garnet.zip, kafka.tgz, jre.zip, vc-redist, xtrabackup.zip, percona-toolkit.zip, strawberry-perl-portable.zip
  /config-templates               # appsettings.template.json, my.ini.template, kafka.properties.template
  /scripts                        # precheck.ps1, av-exclusions.ps1, mount-point.ps1, firewall.ps1
/tests
  /Installer.UnitTests            # manifest parser, signature verifier, state machine, drift detector
  /Installer.MutationTests        # Stryker.NET mutation testing (G74)
  /Installer.FuzzTests            # SharpFuzz on .epcfg, manifests, bootstrapper (G74)
  /Installer.IdempotencyTests     # run-install-twice-and-diff-state (G75)
  /Installer.ConcurrencyTests     # advisory-lock + mutex (G32, G76)
  /Installer.LocaleTests          # 6-locale matrix × flows (G73)
  /Installer.TimeSkipTests        # virtual-clock fast-forward tests (G74)
  /Installer.IntegrationTests     # Pester + Hyper-V clean-VM harness
  /Installer.ChaosTests           # power-cut, disk-full, AV-interference, clock-skew, network-partition
  /Installer.GPOTests             # domain-joined VM with various GPO scenarios (G72)
  /Installer.SkipVersionTests     # N-2 → N upgrade paths (G90)
  /Installer.CrossProfileTests    # restore across hardware profiles (G85)
  /Installer.SBOMTests            # SBOM cross-check + tamper detection (G86)
  /Installer.ReproducibleBuildTests # bit-identical build verification (G67)
  /Sync.ContractTests             # NLDR contract tests
  /Sync.IntegrationTests          # offline/reconnect/conflict matrix
  /Sync.BackpressureTests         # outbox ceiling + overflow (G66)
  /BackupRestore.Tests            # backup/restore/encryption/large-dataset tests
  /BackupRestore.ChainTests       # backup chain attestation (G87)
  /SmokeTests                     # post-install API smoke tests
  /TamperTests                    # tamper-negative suite (Appendix N.3)
/samples
  /release-manifest.yaml
  /service-map.yaml
  /site-config-pack.epcfg
  /hotfix-manifest.yaml
  /override-token.jwt.sample
/docs
  operator-quick-start.md
  rollback-runbook.md
  security-baseline.md
  AV-exclusions-policy.md
  DR-rehearsal-runbook.md
  silent-install-guide.md
  hotfix-procedure.md
  troubleshooting-decision-tree.md
  GPO-compatibility-matrix.md     # Appendix J reference doc for state federation IT
  STRIDE-threat-model.md          # Appendix I reference doc
  SLA-SLO-matrix.md               # Appendix K reference doc
  signing-key-compromise-runbook.md # Appendix M reference doc
  reproducible-build-procedure.md # Section 15 reference doc
  fleet-management-guide.md       # Section 17 HQ-side guide
  conflict-resolution-guide.md    # Section 16/G83 operator guide
  diagnostic-ping-guide.md        # G80 operator guide
  /adr                            # one file per locked decision (0001-0020+)
  /training                       # video scripts, quick-reference cards, WhatsApp guides
```

### 3.4 Installer state machine (complete)

```
                    ┌──────────┐
                    │   LOAD   │ parse CLI args, detect mode
                    └────┬─────┘
                         │
                    ┌────▼─────┐
                    │  VERIFY  │ Authenticode + manifest sig + payload SHA-256
                    └────┬─────┘
                         │
                    ┌────▼──────┐
                    │ PRECHECK  │ OS, disk, RAM, ports, AV, pending reboot,
                    └────┬──────┘ existing install, .epcfg validation
                         │
          ┌──────────────┼──────────────┬──────────────┬──────────────┬──────────────┐
          ▼              ▼              ▼              ▼              ▼              ▼
    ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────┐
    │  INSTALL  │ │  UPGRADE  │ │  REPAIR   │ │  BACKUP   │ │  RESTORE  │ │ UNINSTALL │
    └─────┬─────┘ └─────┬─────┘ └─────┬─────┘ └─────┬─────┘ └─────┬─────┘ └─────┬─────┘
          │              │              │              │              │              │
          │         ┌────▼─────┐        │              │              │              │
          │         │PRE_BACKUP│        │              │              │              │
          │         └────┬─────┘        │              │              │              │
          │         ┌────▼─────┐        │              │              │              │
          │         │ MIGRATE  │        │              │              │              │
          │         └────┬─────┘        │              │              │              │
          │         ┌────▼─────┐        │              │              │              │
          │         │  COMMIT  │        │              │              │              │
          │         └────┬─────┘        │              │              │              │
          │              │              │              │              │              │
          └──────────────┼──────────────┴──────────────┼──────────────┘              │
                         │                             │                             │
                    ┌────▼─────┐                       │                             │
                    │  HEALTH  │ ◄─────────────────────┘                             │
                    └────┬─────┘                                                     │
                         │                                                           │
                    ┌────▼─────┐                                                     │
                    │  SMOKE   │ (post-install API smoke test)                       │
                    └────┬─────┘                                                     │
                         │                                                           │
                    ┌────▼──────┐                                              ┌─────▼─────┐
                    │  SUCCESS  │                                              │  CLEANED   │
                    └───────────┘                                              └───────────┘
                         │
                    On any failure:
                    ┌────▼──────────┐
                    │ SUPPORT_BUNDLE│ → auto-generate
                    └────┬──────────┘
                    ┌────▼──────┐
                    │ ROLLBACK  │ → if applicable (upgrade/migrate)
                    └────┬──────┘
                    ┌────▼──────┐
                    │  FAILED   │ → operator-friendly error + exit code
                    └───────────┘
```

### 3.5 Key data contracts

- **`release-manifest.yaml`** — per BRD 7.1, plus `packaging.compression`, `installer.tool_version`, `min_os_build`, `hotfix_base_version` (null for full releases).
- **`hotfix-manifest.yaml`** — subset of release manifest; lists only changed payloads; `hotfix_for` references base release; no schema migration allowed.
- **`site-config-pack.epcfg`** — signed JSON: pacs_id, state_code, language, data_root, nldr_endpoint, nldr_client_cert_thumbprint, backup_targets[], backup_schedule, log_retention_days, attachment_quota_gb, optional override_token.
- **`installation_registry`** and **`schema_version_registry`** per BRD 7.3 and 9.2, plus `config_hashes` table for drift detection.
- **`sync_outbox`** / **`sync_inbox`** per BRD 12.3–12.4, with added `schema_version` and `sync_protocol_version` columns for forward compatibility.
- **`override_token`** — JWT: `iss=NLPSV-Release-CA`, `sub=<pacs_id>`, `exp=<TTL>`, `jti=<nonce>`, `action=<purge|force-upgrade|...>`.

---

## 4. Phased Delivery

Durations assume a 6-person core team (1 installer eng, 1 DevOps, 1 DB, 1 sync/API, 2 service devs) plus shared QA/security. Each phase has explicit exit criteria, dependency gates, and deliverable checklists.

### Phase 0 — Architecture Finalization (2 weeks)

**Objective**: Lock all technical decisions, procure long-lead items, establish test infrastructure.

**Deliverables**:
1. Confirm and document pinned versions: Garnet, Kafka 3.7.x, Temurin 17.0.x, .NET 8.0.x, MySQL 8.4.x, WiX 4.x.
2. Ratify Site Config Pack (`.epcfg`) JSON Schema with sample.
3. Ratify Override Token JWT schema with sample.
4. Define stack_version numbering policy and 2-year upgrade-path matrix.
5. Agree AV-exclusion baseline with security team.
6. Confirm hardware/OS baselines per BRD 19.1 (addresses G11).
7. Write ADRs 0001–0020 under `/docs/adr/`:
   - 0001: WiX v4 Burn
   - 0002: Microsoft Garnet
   - 0003: Kafka KRaft (no ZooKeeper)
   - 0004: Kestrel (no IIS)
   - 0005: DbUp migration tool
   - 0006: Sync abstraction (ISyncTransport)
   - 0007: Self-contained .NET publish
   - 0008: Certificate-wrapped key escrow
   - 0009: Expand-migrate-contract schema pattern
   - 0010: Silent install via .epcfg
   - 0011: SLSA L3 + reproducible builds (G67)
   - 0012: ATA allow-list for code-signing-key compromise response (G68)
   - 0013: Service-to-service authentication via local mTLS + per-service credentials (G69)
   - 0014: NTP fallback hierarchy and audit-anchor monotonicity (G70)
   - 0015: Bundled root-CA snapshot policy (G71)
   - 0016: Installer self-update via signed installer-only patches (G64)
   - 0017: Outbox ceilings and overflow strategy (G66)
   - 0018: Per-site `.epcfg` encryption + nonce TTL (G92)
   - 0019: Locale support matrix and parsing safety analyzer (G73)
   - 0020: Fleet Health Manifest model and privacy minimization (Section 17)
8. Initiate signing cert procurement (EV Authenticode + HSM/Key Vault).
9. Stand up test infrastructure: 2 clean Windows VMs (Win 10 22H2, Win Server 2022), Hyper-V snapshots.
10. Create git repository with Section 3.3 layout + README.
11. Draft `samples/release-manifest.yaml` JSON Schema + CI validator.
12. Define release cadence: monthly patch, quarterly minor, yearly major.
13. Define hotfix fast-path process (G30).
14. Book Phase 0 decision review with CxO delegate + Security + DB + DevOps leads.

**Exit criteria**:
- [ ] All Section 2 decisions signed off by architecture board.
- [ ] Signing cert procurement initiated with estimated delivery date.
- [ ] Test VM images ready and snapshotted.
- [ ] `.epcfg` schema and Override Token schema ratified.
- [ ] ADRs 0001–0020 written and reviewed.
- [ ] Git repository created with full directory structure.
- [ ] D11–D20 decisions ratified (SLSA, ATA, NTP, root-CA, self-update, outbox ceilings, `.epcfg` encryption, locale support, fleet manifest).
- [ ] Packer-built reference build VM image specification approved.
- [ ] Two-person sign-off pipeline design approved by Security Lead.
- [ ] STRIDE threat model (Appendix I) reviewed by Security Lead.
- [ ] GPO compatibility matrix (Appendix J) approved with state federation IT representative input.
- [ ] SLA/SLO matrix (Appendix K) signed off by Engineering, Security, and Field SRE leads.
- [ ] Locale test corpus (Section 19.1) seeded with 1,000 entries covering Hindi, Marathi, Telugu, Tamil.

---

### Phase 1 — Installer MVP: Fresh Install + Uninstall (6 weeks)

**Objective**: A field operator can install the full ePACS stack on a clean offline Windows machine from a single package, and cleanly uninstall it.

**Deliverables**:

**1.1 WiX v4 Bundle (Week 1–2)**
- Bundle chain: VC Redist → MySQL ZIP → Garnet ZIP → Kafka+JRE → ePACS services ZIP.
- Managed BootstrapperApplication (C#) with WPF UI.
- USB media integrity verification (G33): SHA-256 of payload archive before extraction.
- Concurrent execution guard (G32): named mutex `Global\ePACSInstaller`.

**1.2 Installer Core (Week 1–3)**
- `Installer.Core`: manifest parse/validate, Authenticode verify (`SignedCms`), SHA-256 verify.
- State machine: LOAD → VERIFY → PRECHECK → INSTALL → HEALTH → SMOKE → SUCCESS (see Section 3.4).
- Error code mapping: E001–E099 for prechecks, E100–E199 for install, E200–E299 for service, E300–E399 for health.
- Operator-friendly error messages (plain language, no stack traces in UI).

**1.3 Installer Actions (Week 2–4)**
- Data-root creation + ACL application per BRD 8.3 / 15.5.
- Windows service registration with recovery actions (G35): restart 60s/120s/300s + support bundle.
- Config generation from templates (appsettings, my.ini, kafka.properties, garnet.conf).
- Service start in dependency order (BRD 14.2).
- Precheck suite: OS version, disk space (C: and data volume), RAM, port conflicts, admin rights, pending reboot (G34), AV exclusions, .NET conflicts (G42).
- Temp staging relocation for small C: drives (G18).

**1.4 Installer UI (Week 3–5)**
- WPF screens: Welcome → Site Config (load `.epcfg` or manual entry) → Data Path → Progress → Result.
- Plain language throughout (BRD 21.2).
- Green/yellow/red precheck status display.
- i18n framework via `.resx` (English + Hindi + 1 state language).

**1.5 Silent/Unattended Mode (Week 3–5) — G20**
- `Installer.CLI`: `/quiet /config:<path-to-epcfg>` entry point.
- All wizard inputs sourced from `.epcfg`.
- Exit codes: 0=success, 1=precheck-fail, 2=install-fail, 3=health-fail, 99=unknown.
- Log to `D:\ePACSData\logs\installer\` only (no UI).

**1.6 Uninstall Mode (Week 4–5) — G19**
- Stop all ePACS services in reverse order.
- Deregister Windows services.
- Remove binaries under `C:\Program Files\ePACS\`.
- Preserve `D:\ePACSData\` by default.
- Purge data only with signed Override Token (G14) + typed confirmation "PURGE <pacs_id>".
- Generate final support bundle before removal.

**1.7 Health and Diagnostics (Week 4–6)**
- All services expose `/health/live`, `/health/ready`, `/health/version` per BRD 17.2.
- `SupportBundle` v1: logs, service status, versions, OS info, disk info, AV config, no secrets.
- Post-install smoke test (G43): create/verify/delete test record via API.

**1.8 Installer Agent v1 (Week 5–6)**
- Service health polling (60s interval).
- Basic disk space monitoring (G24).
- Log rotation enforcement (G23).
- Support bundle generation on critical failure.

**1.9 Per-Service Authentication & Local mTLS (Week 4–6) — G69**
- Generate per-PACS local CA at install (no external dependency).
- Issue per-service client certs (MySQL, Garnet, Kafka, Web, Loans, Fas, Membership, Sync, Agent).
- Generate per-service MySQL users with column-level grants (drop default `root` from network usage).
- Set Garnet AUTH password per service.
- Configure Kafka SASL/PLAIN with per-service principals.
- Encrypt all service credentials with DPAPI + cert; cache file under `D:\ePACSData\keys\` (DACL: service principal only).

**1.10 Diagnostic Ping Tool (Week 5–6) — G80**
- `epacs-ping.exe` as standalone CLI.
- Runs ~10 checks (services, DB write, disk free, last backup, sync state, cert expiry, schema fingerprint, smoke test, Traceability, partitions).
- Outputs one-page summary with localized labels.
- Saves output to `D:\ePACSData\diagnostics\ping-<ts>.txt`.
- Desktop shortcut + Start-menu link.

**1.11 SBOM Cross-Check at Install Time (Week 5–6) — G86**
- Bundle `release-sbom.json` (signed) in payload.
- After extraction, recompute SBOM of installed/staged files.
- Compare every file SHA-256 against expected; mismatch → ABORT install (E E022).
- Optional: compare against bundled offline NVD snapshot; warn on Critical CVEs.

**1.12 Idempotency Invariants (Week 5–6) — G75**
- All file writes via write-then-rename atomic pattern.
- All ACLs set absolute (SDDL).
- Service operations use `sc.exe config` to enforce attributes.
- DB creation uses `IF NOT EXISTS` patterns.
- Integration test "run-install-twice-and-diff-state" added to CI.

**1.13 Root-CA Bundle Apply (Week 5–6) — G71**
- Bundle curated Mozilla NSS + Microsoft CTL snapshot.
- Verify signature; apply to LocalMachine root store.
- Record version in `installation_registry.root_ca_bundle_version`.

**Exit criteria**:
- [ ] AC-001: Clean offline install on Win 10 and Win Server 2019 VMs.
- [ ] AC-002: Installer refuses unsigned/tampered package.
- [ ] AC-003: Fresh install creates data root with correct ACLs.
- [ ] AC-004: All health endpoints report expected versions and schema.
- [ ] AC-007: Uninstall removes binaries/services, preserves data.
- [ ] AC-011: DB/cache/eventing ports localhost-only.
- [ ] AC-012: Support bundle contains diagnostics, no plaintext secrets.
- [ ] Silent install completes successfully from `.epcfg`.
- [ ] Fresh install < 15 min on reference hardware.
- [ ] Smoke test passes on fresh install.
- [ ] Per-service authentication and local mTLS working; no service uses MySQL `root` (G69).
- [ ] `epacs-ping.exe` produces correct localized output on a healthy install (G80).
- [ ] SBOM cross-check at install time blocks tampered binaries (G86).
- [ ] Idempotency invariants test "run-install-twice-and-diff-state" passes (G75).
- [ ] Root-CA bundle applied; staleness health check operational (G71).
- [ ] GPO compatibility precheck reports all relevant policy conflicts on test domain-joined VM (G72).

---

### Phase 2 — Upgrade, Backup, Restore, Repair (7 weeks)

**Objective**: Safe upgrade with mandatory backup, restore to same or new machine, repair mode, and emergency hotfix path.

**Dependency gate**: Phase 1 exit criteria met.

**Deliverables**:

**2.1 Backup Engine (Week 1–3)**
- Backup package layout per BRD 13.1.
- Backup types: pre-upgrade, daily incremental, weekly full, manual, pre-restore safety (BRD 13.3).
- MySQL export: `mysqldump` for baseline; `mysqlsh util.dumpInstance` for > 5 GB datasets.
- Attachment tar with per-file SHA-256.
- Config export with secret redaction/encryption.
- Keyring export via approved recovery policy (never plaintext private keys).
- Sync state export (outbox pending + checkpoints).
- AES-256-GCM encryption with certificate-wrapped symmetric key (G40).
- Signed backup manifest (`backup-manifest.yaml` + `.sig`).
- Post-backup validation: checksum verify, manifest signature, DB dump readability, package inventory.
- Backup target validation (G36): path exists, writable, sufficient space, not same volume (warn).
- Backup result written to audit log and `installation_registry`.

**2.2 Restore Engine (Week 2–4)**
- Verify backup package signature and manifest.
- Verify operator authorization and restore reason.
- Create pre-restore safety backup of current system.
- Stop services in controlled order.
- Restore MySQL to staging datadir first; validate schema version, row counts, checksums.
- Restore attachments and verify file hashes.
- Restore config and keys through approved decryption flow.
- Restore sync checkpoints; mark outbox for reconciliation if restore point is older than last sync.
- Start services and run health checks.
- Restore-to-new-machine with recovery cert (G5).
- Run reconciliation report; require sign-off for production use.

**2.3 Upgrade Engine (Week 3–6)**
- Side-by-side upgrade as default for ALL upgrades (not only major):
  1. Validate upgrade path (VR-001 compatibility gate).
  2. Run MySQL Upgrade Checker (G37): `mysqlcheck --check-upgrade` + `util.checkForServerUpgrade()`.
  3. Enforce sync safety policy (drain or checkpoint outbox before upgrade).
  4. Create pre-upgrade backup and verify it.
  5. Stage new binaries in `releases\<new>\`.
  6. Copy MySQL datadir to staging.
  7. Run schema prechecks on staging.
  8. Run migrations on staging via DbUp with checkpointing (G7).
  9. Start new services on validation ports for smoke test.
  10. If smoke passes: stop old services → flip `current` junction → start new services on production ports.
  11. If smoke fails: discard staging → keep old version running → generate support bundle.
- Expand-migrate-contract enforcement (G3): migration scripts tagged with phase (expand/migrate/contract); contract phase blocked if previous release's rollback window hasn't closed.
- Rollback on failure: ROLLBACK_BINARIES → RESTORE_PREUPGRADE_BACKUP → generate support bundle.

**2.4 Repair Mode (Week 4–5) — G31**
- Verify all payload hashes against manifest.
- Replace any mismatched binaries.
- Regenerate config from templates + current `installation_registry` values.
- Re-apply ACLs.
- Re-register services if missing.
- Restart all services.
- Run health checks + smoke test.
- Does NOT touch data or run migrations.

**2.5 Emergency Hotfix (Week 5–6) — G30**
- Hotfix = signed package with only changed binaries + updated manifest.
- No schema migration allowed in hotfix.
- Installer validates hotfix signature and `hotfix_for` base version match.
- Stop affected service(s) only (not full stack).
- Replace binaries, restart, health check.
- Total downtime target: < 5 min.
- Hotfix must be promotable to next full release.

**2.6 Installer Agent v2 (Week 5–7)**
- Configuration drift detection (G22): hourly hash comparison.
- Certificate expiry monitoring: 60/30/7-day warnings.
- Scheduled backup orchestration (daily/weekly per `.epcfg` policy).
- AV exclusion verification.
- Clock drift detection (G16).

**2.7 Percona Toolkit Integration (Week 5–6) — G52**
- Bundle Percona XtraBackup 8.4 + Percona Toolkit + Strawberry Perl (portable) in installer payload.
- Integrate XtraBackup into Installer Agent for daily incremental + weekly full physical backups.
- Integrate `pt-online-schema-change` into Enhanced Migration Runner for COPY-algorithm DDL on large tables.
- Integrate `pt-table-checksum` into post-migration validation.
- Test `pt-table-sync` for drift repair (operator-initiated tool).

**2.8 Schema Fingerprinting (Week 5–6) — G45**
- Implement schema fingerprint capture from `INFORMATION_SCHEMA` (Appendix F).
- Capture baseline fingerprint on fresh install.
- Implement drift detection algorithm: compare current vs expected fingerprint before upgrade.
- Classify drift as benign/compatible/breaking.
- Block upgrade on breaking drift; warn on compatible drift.

**2.9 Mixed Charset Remediation (Week 6) — G47**
- Migration script to convert 24 utf8mb3 tables to utf8mb4.
- Strategy: truncate Hangfire/job tables (transient data) → ALTER charset → restart services.
- Validate all 5 utf8mb4_unicode_ci columns converted to utf8mb4_0900_ai_ci.

**2.10 Large Dataset Testing (Week 6–7) — G26**
- Generate test datasets: 1 GB, 10 GB, 50 GB, 100 GB, 200 GB.
- Upgrade rehearsals on each dataset size.
- Performance benchmarks: backup duration, restore duration, migration duration.
- Compare: XtraBackup incremental vs mysqldump vs mysqlsh for each dataset size.
- Validate pt-online-schema-change on tables with > 1M rows.

**2.11 Migration Concurrency Lock (Week 5–6) — G76**
- Acquire MySQL advisory lock `GET_LOCK('epacs_migration', 0)` at runner start.
- Refuse to proceed if held; log lock owner (pid/host/started_at).
- Verified by integration test running two concurrent runners.

**2.12 Backup Chain Attestation (Week 6) — G87**
- Each backup manifest references previous backup's signature (Merkle-style chain).
- Chain signed by per-PACS attestation key.
- Restore engine verifies chain continuity (warns on break).

**2.13 Adaptive Backup Retention (Week 6) — G84**
- Eviction policy when free space < 20%: oldest weekly full first (after sync confirmation).
- Always retain: latest full + last 3 daily incrementals + latest pre-upgrade backup.
- Operator-overridable "do not evict" flag per backup; eviction events audit-logged.

**2.14 Cross-Profile Restore (Week 6–7) — G85**
- Restore engine reads source `installation_registry.hardware_profile`.
- Detects target machine class via WMI; prompts to apply target profile config.
- Regenerates `my.ini`, Garnet limits, Kestrel thread pool from target profile.
- Test matrix: small→hub, hub→small, same-class.

**2.15 Decimal/Numeric Drift Validator (Week 6–7) — G89**
- Schema fingerprint includes column precision/scale.
- Post-migration validator runs `SELECT SUM(...)` on key ledger columns and compares against pre-migration sum.
- Tolerance for expected new transactions documented.

**2.16 Skip-Version Upgrade Support (Week 6–7) — G90**
- DbUp detects version gaps; applies cumulative deltas from current schema version.
- Test matrix covers all supported skip paths (max 2-version skip).
- Documentation: upgrade beyond 2 versions requires staged upgrade.

**2.17 Garnet AOF Resilience (Week 6) — G91**
- Installer Agent verifies Garnet AOF on service start; quarantines corrupt AOF.
- Starts with empty cache (refilled from MySQL); audit-logs corruption event.
- Adds 6-hour Garnet snapshots to backup scope.

**Exit criteria**:
- [ ] AC-005: Upgrade creates verified backup before migration.
- [ ] AC-006: Upgrade failure triggers rollback or locked recovery state with support bundle.
- [ ] AC-008: Restore from encrypted backup succeeds on clean machine with recovery cert.
- [ ] 20 upgrade rehearsals on 1/10/50 GB datasets succeed.
- [ ] 5 upgrade rehearsals on 100 GB dataset complete within 2 hours.
- [ ] Restore-to-new-machine signed off by security.
- [ ] Repair mode restores healthy state from corrupted binaries.
- [ ] Hotfix applies in < 5 min with minimal service disruption.
- [ ] Configuration drift detected and reported within 60 min.
- [ ] DR rehearsal #1 completed: restore to clean machine + key recovery.
- [ ] Schema fingerprint captured and validated on fresh install + upgrade.
- [ ] Schema drift detection blocks upgrade when breaking drift is present.
- [ ] All 24 utf8mb3 tables converted to utf8mb4 without data loss.
- [ ] Percona XtraBackup incremental backup + restore cycle verified.
- [ ] pt-online-schema-change successfully alters table with > 1M rows.
- [ ] pt-table-checksum verifies data consistency post-migration.
- [ ] MySQL advisory lock prevents concurrent migrations (G76).
- [ ] Backup chain attestation verified across 7 consecutive backups (G87).
- [ ] Adaptive retention evicts oldest full when disk < 20% free (G84).
- [ ] Cross-profile restore test matrix passes (small↔hub) (G85).
- [ ] Decimal/numeric drift validator detects synthetic precision change (G89).
- [ ] Skip-version (N-2 → N) upgrade test passes (G90).
- [ ] Garnet AOF corruption recovery test passes (G91).

---

### Phase 3 — Offline Sync Hardening (6 weeks)

**Objective**: Reliable offline-first sync with NLDR, including conflict handling, reconciliation, and graceful degradation.

**Dependency gate**: Phase 2 exit criteria met.

**Deliverables**:

**3.1 Outbox Relay (Week 1–2)**
- `Outbox.Relay`: polls MySQL `sync_outbox` → publishes to Kafka `epacs.local.sync-ready`.
- Graceful Kafka-down handling (G39): relay retries with exponential backoff; business services unaffected.
- Kafka topic pre-creation (G38): `epacs.local.sync-ready`, `epacs.local.dead-letter`, `epacs.local.commands` with explicit partition count and retention.
- Idempotency key in every outbox record.

**3.2 Sync Agent (Week 2–4)**
- Consumes Kafka topic; posts to NLDR via `ISyncTransport`.
- Retry per BRD 12.5: exponential backoff + jitter; Polly circuit breaker.
- Dead-letter to `epacs.local.dead-letter` after retry exhaustion (BRD 12.5 exception queue).
- Durable checkpoint in MySQL (not Kafka consumer offsets alone).
- Contract version negotiation handshake on first NLDR connect (G4).
- `ISyncTransport.Disabled` mode for pilot; enabled via signed config pack.

**3.3 Inbox Processing (Week 3–5)**
- Consumes NLDR commands via `ISyncTransport`.
- Idempotent apply: duplicate detection via `event_id` / idempotency key.
- Conflict handling per BRD 12.6:
  - Duplicate event → ACK without applying.
  - Out-of-order → hold or reject; require reconciliation if financial order affected.
  - Central policy changed while offline → apply prospectively; flag old-policy transactions.
  - Same master data changed locally and centrally → authority rules (central wins for governed data; PACS wins for local transactions unless rejected).
  - Hash mismatch → reject, quarantine, raise tamper/corruption alert.

**3.4 Reconciliation (Week 4–5)**
- Nightly reconciliation job + on-demand trigger.
- Compare local outbox checkpoints with NLDR acknowledgments.
- Detect and report: unacknowledged events, duplicate ACKs, sequence gaps, hash mismatches.
- Reconciliation report stored in MySQL and available via health dashboard.

**3.5 Sync Health Dashboard (Week 5–6)**
- Per BRD 12.7: last sync time, pending outbound count, failed events by category, connectivity status, schema/protocol versions.
- Network diagnostics (G41): DNS resolution, TCP connectivity, TLS handshake, latency histogram.
- Export support bundle button.
- Sync failure beyond threshold → visible alert (never silent failure).

**3.6 30-Day Offline Drill (Week 5–6)**
- Simulate 30-day offline operation on test PACS.
- Accumulate transactions, then reconnect.
- Verify: no data loss, no duplicates, correct conflict resolution, sync backlog drains at ≥ 1000 events/min.
- Full conflict matrix coverage: duplicate, out-of-order, hash mismatch, policy skew.

**3.7 Outbox Backpressure (Week 4–5) — G66**
- Soft ceiling 500K events → health dashboard YELLOW.
- Hard ceiling 2M events → overflow to `sync_outbox_overflow` cold-storage table; financial events still in main outbox.
- Operator-visible "estimated days until ceiling" gauge based on event production rate.
- Test: simulate 60-day NLDR outage and verify financial events still preserved.

**3.8 Conflict-Resolution UI (Week 5–6) — G83**
- "Sync Conflicts" page in ePACS Web showing local vs central side-by-side.
- Default action: accept-central (per BRD 12.6 authority rules).
- Override-to-local requires Override Token + reason + maker-checker pair.
- All decisions logged to Traceability with operator/checker IDs.

**3.9 Fleet Health Manifest (Week 5–6) — Section 17**
- Compose minimized manifest (no PII, no business data) per Section 17.1.
- Encrypt with NLPSV public key; sign with PACS attestation key.
- Upload via sync window; opt-in per `.epcfg` (default OFF in pilot).

**Exit criteria**:
- [ ] AC-009: PACS processes local transactions while NLDR unreachable.
- [ ] AC-010: Pending transactions sync without duplicates after reconnect.
- [ ] 30-day offline drill completes without data loss or duplication.
- [ ] Full conflict matrix (6 conflict types) tested and documented.
- [ ] Sync health dashboard shows all BRD 12.7 requirements.
- [ ] Graceful degradation: business services operate normally when Kafka is down.
- [ ] Dead-letter queue correctly captures exhausted retries.

---

### Phase 4 — Security Hardening (5 weeks)

**Objective**: Production-grade security posture per BRD Section 15 and 20.

**Dependency gate**: Phase 3 exit criteria met. Signing cert delivered.

**Deliverables**:

**4.1 Signing Pipeline (Week 1–2)**
- Authenticode EV + RFC 3161 TSA integrated into CI.
- HSM or Azure Key Vault-backed signing key.
- All executables, DLLs, PowerShell scripts signed.
- Installer bundle signed.
- Release manifest signed.
- Backup manifests signed.
- Hotfix packages signed.

**4.2 Access Control (Week 1–3)**
- Full ACL baseline per BRD 8.3 / 15.5 on every folder.
- Least-privilege service accounts: `ePACSAppSvc`, `ePACSDbSvc`, `ePACSCacheSvc`, `ePACSEventSvc`, `ePACSSyncSvc`.
- Service account creation automated by installer.
- ACL verification in Installer Agent health checks.

**4.3 Encryption and Key Management (Week 2–3)**
- BitLocker enablement script for data volume (operator-optional with warning).
- Backup encryption with certificate-wrapped keys (G40).
- Certificate lifecycle: issuance, rotation, expiry monitoring, replacement via signed config update.
- Key escrow: dual-custody recovery cert at state federation + NLPSV HQ (G5).
- Quarterly key-recovery drill procedure documented (G27).

**4.4 Network Security (Week 2–3)**
- Firewall rules script: localhost-only 3306 / 6379 / 9092.
- Outbound 443 only to approved NLDR FQDN/IP.
- Firewall rules applied by installer; verified by Installer Agent.

**4.5 Audit and Tamper Detection (Week 3–4)**
- Audit-log hash chaining for: install, upgrade, backup, restore, DB-correction, config change, uninstall events.
- Override Token verification with nonce-based replay prevention (G14).
- Secret-scan CI gate; support-bundle redaction tested against golden list.
- Tamper negative tests: unsigned installer blocked, changed payload blocked, wrong cert blocked, plaintext secret scan, port scan.

**4.6 Security Testing (Week 4–5)**
- Penetration test of health endpoints and web UI.
- Service account privilege escalation test.
- Backup encryption strength verification.
- Certificate chain validation test.
- All BRD Section 20 checklist items verified.

**4.7 SLSA L3 Pipeline Hardening (Week 1–3) — G67**
- Packer-built ephemeral build VM image (signed); rebuilt monthly.
- Hermetic build: no DNS during compile/sign; NuGet via internal hash-pinned cache.
- in-toto provenance attestation per artifact (Section 15.3).
- Reproducible-build verification: independent rebuild on second VM, bit-identical comparison (Section 15.2).
- Two-person sign-off pipeline (Section 15.5); CxO delegate for major releases.
- Release readiness gate (Section 15.6); release-attestation.json signed by all reviewers.

**4.8 ATA (Anti-Tamper Allowlist) Distribution (Week 2–4) — G68**
- ATA issuance authority at NLPSV Release CA.
- Embedded fallback allow-list of trusted thumbprints in Installer Agent.
- ATA refresh on every sync window (small payload).
- Compromise runbook (Appendix M) tabletop-tested.
- Tamper test: signing cert thumbprint not in ATA → installer blocks (TN-04).

**4.9 STRIDE Walkthrough (Week 3–4) — G82**
- Formal STRIDE walkthrough with Security Lead, Engineering Lead, Sync Lead.
- Each threat in Appendix I mapped to mitigation + verifying test.
- Gaps escalated to engineering for Phase 4.5 hardening or Phase 5 backlog.

**4.10 GPO/Domain-Policy Compatibility (Week 3–4) — G72**
- Verify on test domain-joined VM with every policy in Appendix J.
- Document GPO exceptions for state federation IT.
- Precheck (E040–E055) integrated into installer.

**Exit criteria**:
- [ ] Security lead sign-off on BRD Section 20 checklist (all items green).
- [ ] All tamper negative tests pass and are automated in CI.
- [ ] Signing pipeline produces signed artifacts for every build.
- [ ] Key-recovery drill completed successfully.
- [ ] Firewall rules verified: no external access to DB/cache/eventing ports.
- [ ] Secret scan: zero plaintext secrets in logs, config, or support bundles.
- [ ] Override Token replay prevention verified.
- [ ] SLSA L3 attestation produced for release; reproducible-build verified (G67).
- [ ] ATA allow-list distribution end-to-end test passes; compromise tabletop completed (G68).
- [ ] STRIDE walkthrough completed; all threats mapped to mitigations (G82).
- [ ] GPO compatibility validated on domain-joined test VM (G72).
- [ ] Mutation test score ≥ 60% (G74); fuzz test 0 crashes in 1M iterations across 5 parsers (G74).
- [ ] Locale matrix (en-US, en-IN, hi-IN, mr-IN, te-IN, ta-IN) PASS for all flows (G73).
- [ ] Two-person sign-off pipeline operational (G67).

---

### Phase 5 — Pilot Rollout (5 weeks)

**Objective**: Validate the full system in real field conditions across 5–10 PACS in 2 states.

**Dependency gate**: Phase 4 exit criteria met. Pilot sites selected. Operator training materials ready.

**Deliverables**:

**5.1 Operator Training Materials (Week 1–2) — G28**
- 30-min video walkthrough per mode (install, upgrade, backup, restore, repair).
- Laminated A4 quick-reference card (both sides).
- Troubleshooting decision tree poster.
- WhatsApp/Signal-friendly short guides (< 5 screens each).
- All materials in English + Hindi + pilot state language.

**5.2 Field Installation (Week 2–3)**
- Fresh install on 5–10 pilot PACS across 2 states.
- Operator-led installation with engineering observation (not engineering-led).
- Site Config Packs (`.epcfg`) pre-prepared and distributed.
- Silent install tested on at least 2 sites.

**5.3 Operational Exercises (Week 3–4)**
- Per site: simulated upgrade, restore drill, offline-reconnect sync.
- DR rehearsal: restore to clean machine + key recovery at 2 sites.
- Hotfix exercise: deploy test hotfix to 2 sites.
- Configuration drift: intentionally modify config, verify detection.

**5.4 Hypercare (Week 3–5)**
- Daily support bundle collection from all pilot sites.
- Engineering analysis of bundles; patch cycle for issues found.
- Localization feedback from field operators.

**5.5 Pilot Evidence Pack (Week 5)**
- Compile: installation logs, health reports, sync reports, DR drill results, operator feedback, incident log.
- Publish for governance review.

**Exit criteria**:
- [ ] 100% pilot sites operational for 14 consecutive days.
- [ ] ≤ 1 P1 incident per site during hypercare.
- [ ] AC-001 through AC-012 observed in the field.
- [ ] Operator-led install successful without engineering intervention at ≥ 3 sites.
- [ ] DR drill successful at ≥ 2 sites.
- [ ] Pilot evidence pack published and reviewed.

---

### Phase 6 — Scaled Wave Rollout (ongoing)

**Objective**: Controlled expansion from pilot to full deployment.

**Waves**: 50 → 200 → 500 → remaining sites.

**Per-wave requirements**:
- Cumulative incident review from previous wave.
- Installer patch cycle for any issues found.
- Re-sign all artifacts after patches.
- Clean-VM re-verification of patched installer.
- Wave-specific Site Config Packs prepared.
- Support team briefed on new sites.
- Rollback plan documented for each wave.

**Wave exit criteria**:
- [ ] ≤ 2 P1 incidents per 100 sites in first 14 days.
- [ ] All sites reporting healthy via Installer Agent.
- [ ] Sync operational at all connected sites.
- [ ] Support bundle analysis shows no systemic issues.

---

## 5. Cross-Cutting Work Streams

### 5.1 Build and Release Engineering

- Pinned Windows build agent image: WiX v4, .NET 8 SDK, MySQL binaries, Kafka tgz, Garnet, Temurin JRE, CycloneDX tools.
- Pipeline gates (hard fail):
  - Unsigned artifact
  - Unpinned dependency (floating version)
  - Missing SBOM
  - Failed clean-VM fresh install
  - Failed upgrade test on previous version
  - Failed backup/restore test
  - Critical CVE in dependency (with exception register for accepted risks)
  - Plaintext secret detected in artifacts
- Reproducible builds: `packages.lock.json`, `--source-link`, `/p:ContinuousIntegrationBuild=true`.
- Artifact retention: permanent for signed installer + manifest + SBOM; 1 year for build logs and test evidence.
- Release media: USB-ready split ZIPs (4 GB FAT32-safe parts) + single self-extracting EXE.
- Hotfix pipeline: fast-path that skips full integration test suite but requires: signing, clean-VM hotfix-apply test, affected-service health test.
- Delta packages for patch upgrades: binaries-only payload (~200 MB vs 2.5 GB full).

### 5.2 Testing Strategy

**Unit tests** (run on every PR):
- Manifest parser, signature verifier, state machine transitions.
- Outbox relay, retry logic, circuit breaker.
- Config drift detector, disk monitor, log rotator.
- Backup manifest generator, encryption/decryption.
- Override Token verification, nonce validation.

**Integration tests** (run on every PR merge to develop):
- Pester + PowerShell on Hyper-V checkpointed VMs.
- Clean snapshot per scenario; snapshot restored between tests.
- Scenarios: fresh install, upgrade, repair, uninstall, backup, restore, hotfix.
- Silent install from `.epcfg`.

**E2E tests** (run on release candidates):
- Full install/upgrade/restore/offline-sync matrix per BRD 23.1.
- Large dataset tests: 1/10/50/100/200 GB.
- Multi-version upgrade path: N-2 → N-1 → N.

**Chaos tests** (run on release candidates) — G25:
- **Power-cut-during-migration**: Hyper-V force-stop during DbUp execution; verify resume or rollback on restart.
- **Disk-full**: Fill data volume to 100%; verify installer/agent handles gracefully; verify backup refuses to start.
- **AV-interference**: Quarantine random service binary; verify Installer Agent detects and reports.
- **Clock-skew**: Shift system time ±6 hours; verify audit log integrity; verify sync blocks.
- **Network-partition**: Firewall block NLDR endpoint; verify business operations continue; verify sync queues correctly.
- **Service-crash**: Kill MySQL process; verify Installer Agent restarts it; verify recovery actions fire.

**Tamper negative tests** (automated on every PR):
- Unsigned installer → blocked.
- Modified payload hash → blocked.
- Wrong signing certificate → blocked.
- Expired Override Token → blocked.
- Replayed Override Token (same nonce) → blocked.
- Signing-cert thumbprint not in ATA allow-list → blocked (G68).
- Forged `.epcfg` signature → blocked.
- `.epcfg` consumed twice (nonce replay) → second use blocked (G92).
- SBOM cross-check mismatch at install time → blocked (G86).

**Mutation tests** (G74) — run nightly:
- Stryker.NET on `Installer.Core`, `BackupRestore`, `Sync.*`, `ManifestVerifier`.
- Target ≥ 60% mutation score; PR-blocking floor 50%.
- Mutators: arithmetic, boolean, string, return-value, conditional-boundary.

**Fuzz tests** (G74) — run on every release candidate:
- SharpFuzz on `.epcfg` parser (1M iterations, 10-min wall budget).
- SharpFuzz on `release-manifest.yaml` parser.
- SharpFuzz on `backup-manifest.yaml` parser.
- AFL-on-Wine for the bootstrapper EXE entry-point parser.
- Crashes / hangs / OOMs are PR-blocking.

**Locale tests** (G73) — run on release candidates:
- Matrix: en-US, en-IN, hi-IN, mr-IN, te-IN, ta-IN Windows locales × {fresh-install, upgrade, backup, restore}.
- Indic test corpus: 1,000 names with conjuncts, matras, ZWJ/ZWNJ, vowel signs, virama; verify roundtrip across MySQL.
- Decimal/date/number formatting across locales (must always serialize ISO 8601 UTC).
- Path handling for non-ASCII pacs_id and operator names.

**Time-skip tests** (G74) — run on release candidates:
- Fast-forward simulated clock 90 days → verify partition rotation, cert expiry warnings, log rotation, backup retention work as expected.
- Fast-forward 1 year → verify 4 quarterly DR drill artifacts present; root-CA staleness alert fires.
- Fast-forward 3 years → verify regulatory partition retention boundary respected.
- DST transition test (March/November US DST changes) — verify scheduler fires correctly across the transition.

**Locale + concurrency stress** — run on release candidates:
- 50 concurrent business operations + simultaneous backup + simultaneous health probes for 30 minutes; assert no checksum mismatch, no audit-chain breakage, no OOM.

**Idempotency tests** (G75) — run on every PR:
- Install → checkpoint → install (same package) → diff system state → must be byte-identical except mutable mtimes/logs.
- Upgrade → upgrade (same target version) → no schema delta applied; binaries unchanged; configs unchanged.
- Repair on healthy system → no destructive change; smoke test passes.

**Skip-version upgrade tests** (G90) — run on release candidates:
- N-2 → N (skip N-1): verify all migrations applied in order; smoke test passes.
- All supported skip paths in matrix.

**Performance targets**:
| Metric | Target |
|---|---|
| Fresh install (reference hardware) | < 15 min |
| Patch upgrade (binaries only) | < 10 min |
| Minor upgrade (with migration, 10 GB DB) | < 30 min |
| Major upgrade (side-by-side, 10 GB DB) | < 45 min |
| Backup (10 GB DB + attachments) | < 10 min |
| Restore (10 GB DB + attachments) | < 15 min |
| Hotfix apply | < 5 min |
| Sync backlog drain | ≥ 1000 events/min |
| Health check cycle | < 5 sec |
| Support bundle generation | < 2 min |

### 5.3 Observability

- **Logging**: Structured JSON per BRD 17.3; daily rolling files under `D:\ePACSData\logs\<service>\`.
- **Log rotation** (G23): 30-day retention for app logs, 90-day for audit logs, 7-day for MySQL slow/error logs. Compressed after 7 days. Max 10% of data partition or 50 GB.
- **Health endpoints**: `/health/live`, `/health/ready`, `/health/version` on every service per BRD 17.2.
- **Local health dashboard** in ePACS Web:
  - Stack version, schema version, manifest ID.
  - Service status (green/yellow/red per service).
  - Disk usage (OS volume + data volume + attachment volume).
  - Last backup time + backup health.
  - Last sync time + sync lag + pending outbound count.
  - Certificate expiry countdown.
  - Configuration drift status.
  - Clock drift status.
  - Attachment storage usage vs quota (G44).
- **Remote telemetry**: stubbed `ITelemetrySink`; activated only after governance approval (G12). Payload: versions, last backup, sync lag, disk %, cert expiry, incident count.

### 5.4 Governance and Documentation

- **ADRs** under `/docs/adr/` — one per locked decision (0001–0010 in Phase 0; additional as needed).
- **Operator documentation** under `/docs/`:
  - `operator-quick-start.md` — install + first-day operations.
  - `rollback-runbook.md` — step-by-step rollback for each upgrade type.
  - `security-baseline.md` — what's locked down and why.
  - `AV-exclusions-policy.md` — paths to exclude, enterprise AV config guidance.
  - `DR-rehearsal-runbook.md` — quarterly DR drill procedure.
  - `silent-install-guide.md` — `.epcfg` schema reference + CLI switches.
  - `hotfix-procedure.md` — how to apply and verify a hotfix.
  - `troubleshooting-decision-tree.md` — flowchart for common issues.
- **Training materials** under `/docs/training/` (G28):
  - Video scripts for each mode.
  - Quick-reference card layout.
  - WhatsApp guide templates.
- **Release governance**: evidence template per BRD 16.2 filed with every release.
- **CVE monitoring**: monthly re-scan of last 3 supported stack releases; Critical CVE triggers emergency hotfix. CVE exception register maintained.

### 5.5 Disaster Recovery

- **DR rehearsal cadence** (G27):
  - Phase 2 onward: quarterly restore-to-clean-machine + key recovery drill.
  - Phase 5 onward: annual full-site DR simulation (wipe + reinstall + restore + sync resume).
- **DR documentation**: runbook, evidence template, sign-off checklist.
- **Recovery time objectives**:
  - Full restore from backup (10 GB): < 30 min.
  - Full restore from backup (100 GB): < 4 hours.
  - Key recovery from escrow: < 1 hour (assumes cert available).
  - Sync resume after restore: < 15 min (excluding backlog drain).

---

## 6. Top Risks for CxO Visibility

| # | Risk | Likelihood | Impact | Mitigation | Owner |
|---|---|---|---|---|---|
| R1 | NLDR API contract slips | High | High | Sync disabled default; `ISyncTransport` abstraction; installer usable at pilot without sync. | Sync/API Lead |
| R2 | Signing cert procurement delay | Medium | High | Parallel task from Phase 0 day 1; test-signing cert usable until EV arrives. | DevOps Lead |
| R3 | Field operator cannot supply configuration | High | Medium | Signed Site Config Pack `.epcfg` distributed out-of-band; silent install mode. | Product Owner |
| R4 | Garnet maturity on Windows Server | Medium | Medium | Lock to LTS version; `ICache` abstraction allows Redis OSS fallback; Garnet-specific monitoring (G29). | Installer Eng |
| R5 | Kafka KRaft single-node data loss on unclean shutdown | Medium | High | `flush.messages=1` + `flush.ms=1000`; pre-provisioned storage UUID; never auto-format existing datadir; include in pre-upgrade backup; graceful Kafka-down handling (G39). | Installer Eng |
| R6 | MySQL upgrade path breaks on large datasets | Medium | High | Upgrade Checker pre-gate (G37); side-by-side default; 20+ rehearsals on scaled data including 100 GB (G26). | DB Lead |
| R7 | Backup encryption key lost | Low | Catastrophic | Dual-custody recovery cert at state federation + NLPSV HQ; quarterly DR rehearsal (G27). | Security Lead |
| R8 | AV quarantines service binaries | High | Medium | Canonical exclusion script + documented enterprise AV policy + Authenticode signing + Installer Agent detection (G8). | Infrastructure Lead |
| R9 | Power failure during upgrade/migration | Medium | High | Checkpoint-per-script migration (G7); side-by-side staging; chaos test coverage (G25). | Installer Eng |
| R10 | Clock drift corrupts audit chain or sync ordering | Medium | Medium | Offline drift detection (G16); sync blocks on > 5 min drift; server-side timestamp anchoring. | Sync/API Lead |
| R11 | Configuration drift from manual edits | High | Medium | Drift detection (G22); health dashboard warning; support bundle includes diff. | Installer Eng |
| R12 | Disk space exhaustion at rural sites | High | High | Disk monitoring (G24); log rotation (G23); attachment quota (G44); escalating alerts. | Infrastructure Lead |
| R13 | Operator error during upgrade/restore | High | Medium | Silent install mode reduces human error; typed confirmation for destructive actions; training materials (G28). | Support Lead |
| R14 | DDL drift from manual field corrections blocks upgrade | High | High | Schema fingerprinting (G45) detects drift before upgrade; generates remediation scripts; benign drift auto-accepted. | DB Lead |
| R15 | AUTO_INCREMENT overflow on ln_productpurposemapping | Medium | Critical | Currently at 24% of BIGINT max; daily monitoring (G49); ID space expansion plan required before 50%. | DB Lead |
| R16 | Mixed charset causes silent data corruption during migration | Medium | High | One-time charset remediation in first upgrade (G47); truncate-and-convert strategy for Hangfire tables. | DB Lead |
| R17 | pt-online-schema-change fails on table with triggers | Low | Medium | ePACS schema has 0 triggers currently; if triggers are added, pt-osc v3.5+ supports trigger handling; fallback to maintenance-window COPY. | Installer Eng |
| R18 | Build-pipeline compromise produces forged signed installer | Low | Catastrophic | SLSA L3 (G67); two-person production sign-off; hermetic builds; reproducible-build verification on second VM image; ATA kill-switch (G68). | Security Lead |
| R19 | Code-signing key exfiltrated | Low | Catastrophic | HSM/Key Vault with HW protection; annual rotation; emergency rotation runbook (Appendix M); ATA (G68); compromise audit trail. | Security Lead |
| R20 | Outbox saturates disk during 30+ day NLDR outage | Medium | High | Soft/hard ceilings (G66); financial events prioritized; cold-storage overflow table; operator-visible "days to ceiling" gauge. | Sync/API Lead |
| R21 | Air-gapped site loses time accuracy → audit chain corruption | Medium | High | Multi-tier NTP fallback (G70); audit-anchor monotonicity check; weekly operator time-anchor drill; optional GPS USB. | Infrastructure Lead |
| R22 | Stale Windows root-CA store breaks NLDR mTLS handshake | Medium | High | Bundled curated root-CA snapshot per release (G71); staleness health check; auto-apply on install/upgrade. | Security Lead |
| R23 | GPO disables ePACS service after install | High | Medium | GPO compatibility matrix (Appendix J); precheck `gpresult /r`; documented exceptions; non-domain mode supported. | Infrastructure Lead |
| R24 | Concurrent migration runners corrupt schema_version_registry | Low | Catastrophic | Process mutex (G32) + MySQL advisory lock (G76) belt-and-suspenders; integration test verifies. | Installer Eng |
| R25 | XtraBackup target on USB/SMB causes corrupt backup | Medium | High | Filesystem capability detection (G77); precheck rejects unsuitable targets for physical backup; document logical-only path for USB. | Installer Eng |
| R26 | Garnet AOF corruption causes ePACSCache crash loop | Medium | Medium | Quarantine corrupt AOF; start empty (cache non-authoritative); 6-hour snapshots (G91); audit log corruption events. | Installer Eng |
| R27 | Skip-version upgrade (N-2 → N) misapplies migrations | Medium | High | Test matrix covers every supported skip path (G90); DbUp detects gaps and applies cumulative deltas; max-2-version-skip policy. | DB Lead |
| R28 | Restore on different hardware profile causes OOM or under-utilization | Medium | Medium | Restore engine regenerates configs from target hardware profile (G85); test matrix covers cross-profile restores. | Installer Eng |

---

## 7. Assumptions and Out-of-Scope

### Assumptions
- Each PACS receives media via USB / state-distributed courier once per release.
- A central Release CA exists at NLPSV controlling signing certificates.
- An ATA (Anti-Tamper Allowlist) authority exists at NLPSV that can issue revocations within 24h of confirmed compromise (G68).
- MySQL 8.4 LTS Community license fits cooperative-sector use (or Enterprise opted separately).
- Up to 5 concurrent users per PACS; up to 20 at DCCB hub.
- Regional languages limited to 2 at pilot (English + Hindi + 1 state language per pilot state).
- Field operators have local administrator access during installation only.
- Target machines have at least 8 GB RAM and 250 GB SSD (per BRD 19.1 small PACS profile).
- Power backup (UPS) is recommended but not guaranteed; installer must tolerate power loss.
- Windows Defender is the default AV; enterprise AV exclusion policy is the operator's responsibility with installer guidance.
- DCCB hubs are domain-joined; smaller PACS may be standalone — installer supports both (G72).
- State federation operates a portal capable of issuing per-site, time-bound, encrypted `.epcfg` packs (G92).
- Sites have at minimum stratum-3 NTP via DCCB hub or w32time pool when connectivity is available; fully air-gapped sites use audit-anchor monotonicity + optional GPS USB (G70).
- Reproducible builds and SLSA L3 are achievable on Azure DevOps with self-hosted Windows agents and Packer-built VM images (G67).

### Out of scope for v1
- Containerized deployment (BRD rejects for field).
- Kubernetes at PACS.
- Cloud-hosted PACS.
- Central NLDR-side platform build.
- Active-active HA at PACS level.
- Automatic telemetry upload (stubbed only).
- RTL language support.
- Multi-node Kafka cluster.
- Automated Windows Update management (beyond reboot suppression during install).
- Central management console for fleet-wide PACS monitoring (basic Fleet Health Manifest via Section 17 in scope; full HQ dashboard is v2).
- Fully automated installer self-update for major versions (only N→N+1 self-update in scope; major upgrades remain USB-delivered, G64).

---

## 8. Decision Points before Phase 0 Closes

| # | Decision | Recommended | Deadline |
|---|---|---|---|
| D1 | All 44 gap mitigations accepted as written (or amended) | Accept with amendments | Phase 0 Week 1 |
| D2 | Signing cert procurement path: HSM vs Azure Key Vault | Azure Key Vault (lower ops burden) | Phase 0 Week 1 |
| D3 | CI/CD platform | Azure DevOps (on-prem build agent control) | Phase 0 Week 1 |
| D4 | Pilot state(s) and PACS sites selected | 2 states, 5–10 sites | Phase 0 Week 2 |
| D5 | Release cadence | Monthly patch, quarterly minor, yearly major | Phase 0 Week 1 |
| D6 | MySQL licensing posture | Community (with Enterprise support option) | Phase 0 Week 1 |
| D7 | NLDR v1 API contract target date | Committed by central NLPSV team | Phase 0 Week 2 |
| D8 | Garnet vs Redis OSS fallback threshold | 3 P1 Garnet-specific incidents triggers fallback evaluation | Phase 0 Week 1 |
| D9 | Attachment archival policy | 2-year default; configurable per `.epcfg` | Phase 0 Week 2 |
| D10 | DR rehearsal cadence | Quarterly from Phase 2; annual full-site from Phase 5 | Phase 0 Week 1 |
| D11 | SLSA target level (G67) | SLSA L3 with reproducible-build verification | Phase 0 Week 1 |
| D12 | ATA authority and refresh cadence (G68) | NLPSV Release CA issues; refresh on every sync window; emergency-rotation SLA 24h | Phase 0 Week 2 |
| D13 | Outbox ceilings (G66) | Soft 500K, Hard 2M; financial events never deferred | Phase 0 Week 2 |
| D14 | NTP fallback hierarchy (G70) | w32time → DCCB hub stratum-2 → audit-anchor monotonicity (+ optional GPS USB at high-risk sites) | Phase 0 Week 1 |
| D15 | Root-CA bundle source and refresh (G71) | Mozilla NSS + Microsoft CTL snapshot; refreshed per release; staleness alerts at 18/24 months | Phase 0 Week 1 |
| D16 | Self-update policy (G64) | Installer-only patches via signed manifest; N→N+1 only; opt-in per `.epcfg` | Phase 0 Week 2 |
| D17 | Locale support matrix (G73) | en-US, en-IN, hi-IN, mr-IN, te-IN, ta-IN at v1 (RTL deferred) | Phase 0 Week 2 |
| D18 | Two-person sign-off scope (G67) | All production releases; CxO delegate also for major releases | Phase 0 Week 1 |
| D19 | Fleet Health Manifest opt-in default (Section 17) | OFF in pilot; ON post-governance review (Phase 5) | Phase 0 Week 2 |
| D20 | `.epcfg` distribution mechanism (G92) | State federation portal issues per-site encrypted packs with TTL=7d + nonce | Phase 0 Week 2 |

---

## 9. Immediate Next Actions (Phase 0, First Week)

| # | Action | Owner | Due |
|---|---|---|---|
| 1 | Create git repository with Section 3.3 layout + README | DevOps Lead | Day 1 |
| 2 | Write ADRs 0001–0010 | Engineering Lead | Day 5 |
| 3 | Draft `samples/release-manifest.yaml` JSON Schema + CI validator | Installer Eng | Day 3 |
| 4 | Draft `samples/hotfix-manifest.yaml` JSON Schema | Installer Eng | Day 3 |
| 5 | Stand up 2 clean Windows VMs (Win 10 22H2, Win Server 2022); snapshot | Infrastructure Lead | Day 3 |
| 6 | Begin signing-cert procurement (long-lead) | Security Lead | Day 1 |
| 7 | Pin exact versions: MySQL 8.4.x, Kafka 3.7.x, Temurin 17.0.x, Garnet, .NET 8.0.x, WiX 4.x | Engineering Lead | Day 2 |
| 8 | Draft Site Config Pack (`.epcfg`) JSON Schema + sign a sample | Installer Eng | Day 4 |
| 9 | Draft Override Token JWT schema + sign a sample | Security Lead | Day 4 |
| 10 | Define error code registry (E001–E399) | Installer Eng | Day 5 |
| 11 | Draft operator quick-start outline | Support Lead | Day 5 |
| 12 | Book Phase 0 decision review with CxO delegate + Security + DB + DevOps leads | Engineering Lead | Day 2 |
| 13 | Generate 1 GB / 10 GB test datasets for early integration testing | DB Lead | Day 5 |
| 14 | Document expand-migrate-contract rules for migration scripts | DB Lead | Day 5 |
| 15 | Specify Packer-built reference build VM image (SLSA L3) | DevOps Lead | Day 5 |
| 16 | Draft `release-attestation.json` schema (SLSA in-toto) | Engineering Lead | Day 5 |
| 17 | Draft ATA (Anti-Tamper Allowlist) JSON schema + sample | Security Lead | Day 5 |
| 18 | Procure 1,000-entry locale test corpus (Hindi/Marathi/Telugu/Tamil) | QA Lead | Day 5 |
| 19 | Draft GPO compatibility matrix (Appendix J) | Infrastructure Lead | Day 5 |
| 20 | Initial STRIDE walkthrough on existing architecture (Appendix I review) | Security Lead | Day 5 |
| 21 | Draft `epacs-ping.exe` CLI specification (G80) | Installer Eng | Day 5 |
| 22 | Draft `.epcfg` per-site encryption + nonce protocol (G92) | Security Lead | Day 5 |
| 23 | Draft fleet health manifest schema + privacy review (Section 17) | Product Owner + Security Lead | Day 5 |
| 24 | Draft signing-key compromise tabletop scenario (Appendix M dry-run) | Security Lead | Day 5 |

---

## 10. Traceability Matrix: BRD Acceptance Criteria → Plan Coverage

| BRD AC | Description | Plan Phase | Plan Section |
|---|---|---|---|
| AC-001 | Clean offline install from single package | Phase 1 | 1.1–1.7 |
| AC-002 | Refuse unsigned/tampered package | Phase 1 | 1.2 (state machine VERIFY) |
| AC-003 | Data root + correct ACLs | Phase 1 | 1.3 |
| AC-004 | Health endpoints report expected versions | Phase 1 | 1.7 |
| AC-005 | Upgrade creates verified backup before migration | Phase 2 | 2.3 |
| AC-006 | Upgrade failure → rollback or locked recovery + support bundle | Phase 2 | 2.3 |
| AC-007 | Uninstall preserves data by default | Phase 1 | 1.6 |
| AC-008 | Restore from encrypted backup on clean machine | Phase 2 | 2.2 |
| AC-009 | Local transactions while NLDR unreachable | Phase 3 | 3.1, 3.2 (G39) |
| AC-010 | Sync without duplicates after reconnect | Phase 3 | 3.2, 3.3, 3.6 |
| AC-011 | DB/cache/eventing ports localhost-only | Phase 1 | 1.3 (firewall) |
| AC-012 | Support bundle: diagnostics, no secrets | Phase 1 | 1.7 |

---

## 11. Traceability Matrix: BRD Functional Requirements → Plan Coverage

| BRD FR | Description | Plan Coverage |
|---|---|---|
| FR-01 | Single launcher for complete workflow | Phase 1: WiX Burn bundle |
| FR-02 | Detect install/upgrade/repair/restore mode | Phase 1: state machine (Section 3.4) |
| FR-03 | Fully offline execution | Phase 1: all payloads bundled; no internet dependency |
| FR-04 | Pre-install checks | Phase 1: precheck suite (1.3) |
| FR-05 | Understandable messages for non-technical users | Phase 1: UI (1.4), error code mapping (1.2) |
| FR-06 | Silent/unattended execution | Phase 1: silent mode (1.5, G20) |
| FR-07 | Fresh DB initialization | Phase 1: install actions (1.3) |
| FR-08 | Secure backup before upgrade | Phase 2: backup engine (2.1) |
| FR-09 | Export includes schema, data, config, migration metadata | Phase 2: backup package layout per BRD 13.1 |
| FR-10 | Import into new installation | Phase 2: restore engine (2.2) |
| FR-11 | Version-aware migration with audit history | Phase 2: DbUp with checkpointing (2.3) |
| FR-12 | Migration failure → stop cutover, preserve logs, rollback guidance | Phase 2: upgrade engine rollback (2.3) |
| FR-13 | Side-by-side upgrade | Phase 2: default for all upgrades (2.3) |
| FR-14 | Scheduled/operator-triggered backup | Phase 2: Installer Agent v2 (2.6) |

---

## 12. DDL Drift and Differential Migration Architecture

This section addresses the critical challenge of upgrading a 1,057-table MySQL schema across hundreds of offline PACS nodes where each node may have accumulated DDL drift, where full database dumps are impractical for large datasets, and where the NLDR sync channel needs efficient delta-only data transfer.

### 12.1 Schema Profile Summary (from AP_DDL.sql analysis)

| Metric | Value | Implication |
|---|---|---|
| Total tables | 1,057 | Large schema; migration ordering matters |
| Foreign keys | 81 | Creates dependency graph for DDL ordering |
| Views | 42 | Must be dropped/recreated around table migrations |
| Non-unique indexes | 2,235 | Index rebuilds dominate migration time |
| Unique indexes | 12 | Low; most uniqueness via PK only |
| LOB columns (longtext/longblob) | 4,556 | Inflates backup size; prevents INSTANT DDL |
| Tables with `SaltValue longblob` | 681 | Sync-related; present on most business tables |
| Tables with `idgeneratorforpacs` | 664 | PACS-local ID generation; sync-aware |
| Tables with `SerialNumberOfPacs` | 681 | Sync tracking column |
| Tables with `SourceId tinyint` | 646 | Origin tracking (local vs central) |
| Tables with `DataEntryWorkingDate` | 194 | Insert timestamp only; not useful for delta |
| Tables with `updated_at` (auto-update) | 9 | Only 9 tables have reliable modification tracking |
| Character set: utf8mb4 | 1,033 tables | Target standard |
| Character set: utf8mb3 | 24 tables | Must be converted (Hangfire, job queue tables) |
| Collation: utf8mb4_0900_ai_ci | 1,073 columns | Target standard |
| Collation: utf8mb3_general_ci | 68 columns | Must be converted |
| Collation: utf8mb4_unicode_ci | 5 columns | Minor; convert to _0900_ai_ci |
| Storage engine | 100% InnoDB | Good; enables online DDL and XtraBackup |
| Partitioned tables | 0 | No partition management needed |
| Stored procedures/triggers | 0 | Simplifies migration (no procedural objects) |
| Module prefixes | cm(152), ln(126), fa(98), tr(77), pds(58), cus(46), audit(33), hrms(29), trm(28), de(27), ast(27), agc(25), sca(23), str(21) + others | Natural migration ordering by module |
| Largest AUTO_INCREMENT | `ln_productpurposemapping`: 2.2×10¹⁸ (24% of BIGINT max) | Monitor for overflow |

### 12.2 The Three Deltas: Schema, Data, and Sync

The upgrade and sync architecture must handle three distinct types of differential processing:

```
┌─────────────────────────────────────────────────────────────────┐
│                    DELTA ARCHITECTURE                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  1. SCHEMA DELTA (DDL changes between versions)                  │
│     ┌──────────┐    ┌──────────┐    ┌──────────┐               │
│     │ Baseline │───>│ DbUp     │───>│ Target   │               │
│     │ Schema   │    │ Scripts  │    │ Schema   │               │
│     │ v3.2.0   │    │ (delta)  │    │ v3.3.0   │               │
│     └──────────┘    └──────────┘    └──────────┘               │
│     Tooling: DbUp + pt-online-schema-change (large tables)      │
│                                                                  │
│  2. DATA DELTA (row-level changes for backup/restore)            │
│     ┌──────────┐    ┌──────────┐    ┌──────────┐               │
│     │ Last     │───>│ Percona  │───>│ Current  │               │
│     │ Full     │    │ XtraBack │    │ State    │               │
│     │ Backup   │    │ (incr)   │    │          │               │
│     └──────────┘    └──────────┘    └──────────┘               │
│     Tooling: Percona XtraBackup (physical incremental)           │
│              + mysqldump/mysqlsh (logical, for portability)      │
│                                                                  │
│  3. SYNC DELTA (business data changes for NLDR)                  │
│     ┌──────────┐    ┌──────────┐    ┌──────────┐               │
│     │ Business │───>│ Outbox   │───>│ NLDR     │               │
│     │ Txn      │    │ + CDC    │    │ Central  │               │
│     │ (local)  │    │ (delta)  │    │          │               │
│     └──────────┘    └──────────┘    └──────────┘               │
│     Tooling: Transactional outbox (existing)                     │
│              + updated_at columns (new, for master data)         │
│              + pt-table-checksum (verification)                  │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 12.3 Schema Delta: DDL Migration Strategy

#### 12.3.1 DDL Operation Classification

Every migration script must be classified before execution. The classification determines the execution strategy:

| DDL Operation | MySQL 8.4 Algorithm | Locking | Duration (1M rows) | Strategy |
|---|---|---|---|---|
| Add nullable column | INSTANT | None | Milliseconds | DbUp direct |
| Add column with default | INSTANT | None | Milliseconds | DbUp direct |
| Drop column | INSTANT (8.0.29+) | None | Milliseconds | DbUp direct |
| Rename column | INSTANT | None | Milliseconds | DbUp direct |
| Add index | INPLACE | Concurrent DML OK | Seconds–minutes | DbUp direct |
| Drop index | INPLACE | Concurrent DML OK | Milliseconds | DbUp direct |
| Add FULLTEXT index | INPLACE | Read-only lock | Minutes | DbUp with warning |
| Change column type | COPY | Table locked | Minutes–hours | **pt-online-schema-change** |
| Change charset (utf8mb3→utf8mb4) | COPY | Table locked | Minutes–hours | **pt-online-schema-change** |
| Add/drop FK constraint | INPLACE | Metadata lock | Seconds | DbUp direct |
| Rename table | INSTANT | Metadata lock | Milliseconds | DbUp direct |
| Convert ROW_FORMAT | COPY | Table locked | Minutes–hours | **pt-online-schema-change** |

**Rule**: Any DDL requiring `ALGORITHM=COPY` on a table with > 1 million rows MUST use `pt-online-schema-change` instead of direct `ALTER TABLE`.

#### 12.3.2 Migration Script Structure (Enhanced)

```sql
-- Migration: V025__add_updated_at_to_customer_tables.sql
-- Classification: INPLACE (add column with default)
-- Estimated duration: < 1 second per table (INSTANT)
-- Affected tables: cus_customerpersonaldetails, cus_customergeneral, ...
-- Rollback: V025__rollback__drop_updated_at.sql
-- Phase: EXPAND (expand-migrate-contract)

-- Pre-check: verify current schema version
SELECT schema_version FROM schema_version_registry
WHERE schema_version = 24;  -- must be exactly 24

-- Migration body
ALTER TABLE cus_customerpersonaldetails
  ADD COLUMN updated_at TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP
  ON UPDATE CURRENT_TIMESTAMP,
  ALGORITHM=INSTANT;

-- Checkpoint
INSERT INTO schema_version_registry (script_name, schema_version, applied_at, status)
VALUES ('V025__add_updated_at_to_customer_tables.sql', 25, NOW(), 'APPLIED');
```

For COPY-algorithm operations on large tables:

```sql
-- Migration: V030__convert_hangfire_charset.sql
-- Classification: COPY (charset conversion)
-- Estimated duration: 5-15 minutes (Hangfire tables are small)
-- Execution: pt-online-schema-change
-- Rollback: Not needed (charset upgrade is forward-only)
-- Phase: EXPAND

-- This script is executed by the migration runner via pt-osc wrapper
-- DO NOT execute directly via mysql client

-- pt-osc command (generated by migration runner):
-- pt-online-schema-change --alter "CONVERT TO CHARACTER SET utf8mb4
--   COLLATE utf8mb4_0900_ai_ci"
--   --execute --no-drop-old-table
--   D=MHCluster3,t=HangfireJob
```

#### 12.3.3 Schema Fingerprinting (G45)

Before any upgrade, the installer must verify the on-disk schema matches expectations:

```
Schema Fingerprint = {
  tables: [
    {
      name: "cus_customerpersonaldetails",
      columns: [
        { name: "PerPkey", type: "bigint", nullable: false, default: null, extra: "auto_increment" },
        ...
      ],
      indexes: [
        { name: "PRIMARY", columns: ["PerPkey"], unique: true },
        ...
      ],
      foreign_keys: [
        { name: "Addresspkey", columns: ["AddressPkey"], ref_table: "cm_memberaddress", ref_columns: ["AddPkey"] },
        ...
      ],
      charset: "utf8mb4",
      collation: "utf8mb4_0900_ai_ci",
      engine: "InnoDB",
      auto_increment: 12345678
    },
    ...
  ],
  views: [...],
  fingerprint_hash: "SHA-256 of canonical JSON representation"
}
```

**Workflow**:
1. On fresh install: capture fingerprint → store as `baseline_fingerprint_v{version}`.
2. Before upgrade: capture current fingerprint → compare against expected baseline for installed version.
3. If drift detected:
   - Generate drift report (added columns, missing indexes, changed types, etc.).
   - Classify drift as: (a) benign (extra indexes), (b) compatible (additive columns), (c) breaking (missing columns, changed types).
   - Benign/compatible → warn + proceed with upgrade.
   - Breaking → block upgrade + generate remediation script + require operator review.
4. After upgrade: capture new fingerprint → store as new baseline.

#### 12.3.4 Migration Ordering Strategy (G50, G53)

```
Phase 1: Pre-migration safety
  ├── Disable FK checks: SET FOREIGN_KEY_CHECKS=0
  ├── Drop all 42 views (stored in /migrations/R__*.sql for recreation)
  └── Capture pre-migration schema fingerprint

Phase 2: Infrastructure tables (non-business)
  ├── Hangfire tables (12) — charset conversion utf8mb3→utf8mb4
  ├── Counter/Lock/Hash/Job tables — charset conversion
  └── Migration tracking tables (schema_version_registry, etc.)

Phase 3: Master/reference tables (cm_* prefix, 152 tables)
  ├── State, district, village masters
  ├── Member type, caste, community masters
  ├── Product masters, policy masters
  └── These are FK parents — must be migrated before children

Phase 4: Customer tables (cus_* prefix, 46 tables)
  ├── cus_customerpersonaldetails (FK parent for many ln_* tables)
  ├── cus_customergeneral, cus_landdetails, etc.
  └── Add updated_at columns for delta sync

Phase 5: Business transaction tables (ln_*, fa_*, sca_*, trm_*, etc.)
  ├── ln_applicationmain (AUTO_INCREMENT=2.1×10¹³, FK child)
  ├── fa_ledger (AUTO_INCREMENT=1.3×10¹⁴, 13 indexes)
  ├── ln_productpurposemapping (AUTO_INCREMENT=2.2×10¹⁸ — MONITOR)
  └── Use pt-online-schema-change for any COPY DDL on these

Phase 6: Trading/PDS/Asset tables (tr_*, pds_*, ast_*)
  └── Lower priority; smaller datasets typically

Phase 7: Audit/reporting tables (audit_*, mis_*, dashboard_*)
  └── Append-only; rarely need DDL changes

Phase 8: Post-migration
  ├── Re-enable FK checks: SET FOREIGN_KEY_CHECKS=1
  ├── Recreate all 42 views from /migrations/R__*.sql
  ├── Run pt-table-checksum on critical tables
  ├── Capture post-migration schema fingerprint
  └── Validate all FKs: SELECT * FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
      WHERE CONSTRAINT_TYPE='FOREIGN KEY' — verify none are broken
```

### 12.4 Data Delta: Differential Backup Strategy

#### 12.4.1 Backup Tiers

| Tier | Tool | Type | When | Size (10 GB DB) | Duration | Restore Time |
|---|---|---|---|---|---|---|
| **Tier 1: Physical Full** | Percona XtraBackup 8.4 | Full physical | Weekly | ~10 GB (compressed ~3 GB) | ~5 min | ~10 min |
| **Tier 2: Physical Incremental** | Percona XtraBackup 8.4 | Incremental (since last full/incr) | Daily | ~500 MB–2 GB | ~1–3 min | ~15 min (apply chain) |
| **Tier 3: Logical Full** | `mysqlsh util.dumpInstance` | Full logical | Pre-upgrade + monthly | ~8 GB (compressed ~2.5 GB) | ~15 min | ~30 min |
| **Tier 4: Logical Table-level** | `mysqlsh util.dumpTables` | Specific tables | On-demand | Varies | Varies | Varies |

**Why Percona XtraBackup over MySQL Enterprise Backup?**
- MySQL Enterprise Backup requires a commercial license — incompatible with MySQL Community.
- Percona XtraBackup 8.4 is 100% OSS (GPL), supports MySQL 8.4, and provides identical incremental/differential capability.
- XtraBackup operates at the InnoDB page level — it copies only changed pages since the last backup, making it genuinely differential.
- XtraBackup supports encryption, compression, and streaming — all required by the BRD.

#### 12.4.2 Percona XtraBackup Integration

```
Backup flow (daily incremental):
  1. Installer Agent triggers at scheduled time
  2. xtrabackup --backup --target-dir=/backup/incr/$(date +%Y%m%d)
       --incremental-basedir=/backup/full/latest
       --compress --encrypt=AES256 --encrypt-key-file=<cert-wrapped-key>
  3. Verify: xtrabackup --validate-backup
  4. Record in backup_manifest: type=incremental, base=<full_id>, lsn=<from>-<to>
  5. Rotate: keep 7 daily incrementals + 4 weekly fulls

Restore flow (from incremental chain):
  1. Prepare full backup: xtrabackup --prepare --apply-log-only --target-dir=/backup/full/latest
  2. Apply each incremental in order:
     xtrabackup --prepare --apply-log-only --target-dir=/backup/full/latest
       --incremental-dir=/backup/incr/20260501
     xtrabackup --prepare --apply-log-only --target-dir=/backup/full/latest
       --incremental-dir=/backup/incr/20260502
     ... (last one without --apply-log-only)
  3. xtrabackup --prepare --target-dir=/backup/full/latest  (final prepare)
  4. Stop MySQL, replace datadir, start MySQL
  5. Run health checks + schema fingerprint validation
```

#### 12.4.3 Logical vs Physical Backup Decision Matrix

| Scenario | Recommended Tool | Reason |
|---|---|---|
| Daily backup (< 50 GB) | Percona XtraBackup incremental | Fast, minimal I/O, no table locks |
| Daily backup (> 50 GB) | Percona XtraBackup incremental | Only viable option at this scale |
| Pre-upgrade backup | Logical (`mysqlsh util.dumpInstance`) | Portable; can restore to different MySQL version |
| Restore to same machine | Percona XtraBackup | Fastest; direct datadir replacement |
| Restore to new machine | Logical (`mysqlsh util.loadDump`) | Version-independent; works across MySQL builds |
| Cross-version upgrade (8.0→8.4) | Logical dump + load | Physical backup not portable across major versions |
| Table-level recovery | `mysqlsh util.dumpTables` + `util.loadDump` | Surgical; doesn't require full restore |
| Backup verification | `pt-table-checksum` on restored copy | Proves backup integrity at row level |

### 12.5 Sync Delta: NLDR Data Synchronization

#### 12.5.1 The Delta Tracking Problem

The current schema has a fundamental gap for delta sync:
- Only **9 tables** have `updated_at` with `ON UPDATE CURRENT_TIMESTAMP` (reliable modification tracking).
- **194 tables** have `DataEntryWorkingDate` but it's set only on INSERT (not updated on modification).
- **664 tables** have `idgeneratorforpacs` (PACS-local ID) but no modification timestamp.

This means the system can track *new* records via the outbox pattern, but cannot efficiently detect *modified* records for master data sync.

#### 12.5.2 Delta Tracking Remediation Plan

**Phase A (Migration — expand step)**: Add `updated_at` to all sync-eligible tables.

```sql
-- Identify sync-eligible tables: those with SourceId, SaltValue, idgeneratorforpacs
-- These are the tables that participate in PACS↔NLDR sync
-- Total: ~646 tables (those with SourceId column)

-- For each sync-eligible table:
ALTER TABLE <table_name>
  ADD COLUMN updated_at TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP
  ON UPDATE CURRENT_TIMESTAMP
  AFTER DataEntryWorkingDate,
  ALGORITHM=INSTANT;  -- INSTANT because adding nullable column with default

-- This is safe because:
-- 1. INSTANT algorithm = no table rebuild, no locking
-- 2. Existing rows get NULL (not backfilled — intentional)
-- 3. Future updates automatically set the timestamp
-- 4. NULL updated_at means "never modified since migration" — treat as original
```

**Phase B (Application change)**: Modify business services to include `updated_at` in outbox writes for master data changes.

```
Business transaction flow (existing — no change):
  BEGIN TRANSACTION
    INSERT/UPDATE business record
    INSERT sync_outbox (event_type='TRANSACTION', ...)
  COMMIT

Master data change flow (new):
  BEGIN TRANSACTION
    UPDATE cm_villagemaster SET ... WHERE ...
    -- updated_at auto-updates via ON UPDATE CURRENT_TIMESTAMP
    INSERT sync_outbox (
      event_type='MASTER_DATA_CHANGE',
      table_name='cm_villagemaster',
      record_id=<pkey>,
      change_type='UPDATE',
      payload_hash=SHA2(JSON_OBJECT(...), 256)
    )
  COMMIT
```

**Phase C (Sync Agent enhancement)**: Delta query for master data sync.

```sql
-- Sync Agent queries for unsynchronized master data changes:
SELECT * FROM cm_villagemaster
WHERE updated_at > (
  SELECT last_sync_timestamp
  FROM sync_checkpoints
  WHERE table_name = 'cm_villagemaster'
)
ORDER BY updated_at ASC
LIMIT 1000;

-- After successful sync:
UPDATE sync_checkpoints
SET last_sync_timestamp = <max_updated_at_from_batch>
WHERE table_name = 'cm_villagemaster';
```

#### 12.5.3 Percona pt-table-checksum for Sync Verification

After a restore or after extended offline periods, use `pt-table-checksum` to verify data consistency between the PACS node and a reference copy:

```
-- On the PACS node (after restore):
pt-table-checksum \
  --databases=MHCluster3 \
  --tables=cus_customerpersonaldetails,ln_applicationmain,fa_ledger \
  --chunk-size=5000 \
  --no-check-replication-filters \
  --replicate=epacs_meta.checksums

-- Compare checksums against known-good values from pre-backup:
SELECT db, tbl, chunk, this_crc, master_crc,
       IF(this_crc = master_crc, 'OK', 'DRIFT') AS status
FROM epacs_meta.checksums
WHERE this_crc != master_crc;

-- If drift detected, use pt-table-sync to repair:
pt-table-sync \
  --databases=MHCluster3 \
  --tables=<drifted_table> \
  --sync-to-master \
  --print  -- dry-run first, then --execute
```

### 12.6 Mixed Charset Remediation Plan (G47)

The 24 utf8mb3 tables must be converted to utf8mb4 before any other migration. This is a one-time operation during the first upgrade that introduces the new migration framework.

**Tables requiring conversion** (all Hangfire/job-queue infrastructure):
- `AggregatedCounter`, `Counter`, `DistributedLock`, `Hash`, `Job`, `JobParameter`, `JobQueue`, `JobState`, `List`, `Server`, `Set`, `State`
- `HangfireAggregatedCounter`, `HangfireCounter`, `HangfireDistributedLock`, `HangfireHash`, `HangfireJob`, `HangfireJobParameter`, `HangfireJobQueue`, `HangfireJobState`, `HangfireList`, `HangfireServer`, `HangfireSet`, `HangfireState`

**Strategy**: These are all job-queue tables with transient data. The safest approach:
1. Stop all ePACS services (Hangfire jobs stop).
2. Truncate all Hangfire/job tables (transient data; no business value).
3. `ALTER TABLE ... CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci` on each.
4. Restart services (Hangfire rebuilds its state).

This avoids the expensive `ALGORITHM=COPY` on potentially large job history tables.

### 12.7 AUTO_INCREMENT Overflow Monitoring (G49)

```
Installer Agent check (daily):
  For each table with AUTO_INCREMENT:
    current_ai = SELECT AUTO_INCREMENT FROM INFORMATION_SCHEMA.TABLES
                 WHERE TABLE_NAME = '<table>'
    max_value = CASE column_type
                  WHEN 'int'    THEN 2147483647
                  WHEN 'bigint' THEN 9223372036854775807
                END
    usage_pct = current_ai / max_value * 100

    IF usage_pct > 50%:
      log CRITICAL "AUTO_INCREMENT for <table> at {usage_pct}% of maximum"
      flag in health dashboard
      include in support bundle

    IF usage_pct > 75%:
      block upgrades until resolved
      generate remediation plan (ID space expansion or re-keying)

Current high-risk tables:
  ln_productpurposemapping: 2.2×10¹⁸ / 9.2×10¹⁸ = 24% — WATCH
  fa_ledger:                1.3×10¹⁴ / 9.2×10¹⁸ = 0.001% — OK
  ln_applicationmain:       2.1×10¹³ / 9.2×10¹⁸ = 0.0002% — OK
```

### 12.8 Percona Toolkit Integration Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Percona Toolkit Components (bundled in installer payload)       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  pt-online-schema-change                                         │
│    Used by: Migration Runner (DbUp wrapper)                      │
│    When: COPY-algorithm DDL on tables > 1M rows                  │
│    How: Creates shadow table, copies in chunks, uses triggers    │
│         to capture concurrent DML, atomic rename at end          │
│    Config: --chunk-size=1000, --max-lag=1s, --check-interval=1   │
│                                                                  │
│  Percona XtraBackup 8.4                                          │
│    Used by: Installer Agent (scheduled backups)                  │
│    When: Daily incremental, weekly full, pre-upgrade             │
│    How: Physical InnoDB page-level copy; no table locks          │
│    Config: --compress, --encrypt=AES256, --parallel=4            │
│                                                                  │
│  pt-table-checksum                                               │
│    Used by: Post-migration validator, Sync Agent                 │
│    When: After upgrade, after restore, periodic verification     │
│    How: Chunk-based CRC32 checksumming across all rows           │
│    Config: --chunk-size=5000, --replicate=epacs_meta.checksums   │
│                                                                  │
│  pt-table-sync                                                   │
│    Used by: Drift repair tool (operator-initiated)               │
│    When: After pt-table-checksum detects drift                   │
│    How: Generates REPLACE/DELETE statements to fix differences   │
│    Config: --print first (dry-run), then --execute               │
│                                                                  │
│  pt-show-grants                                                  │
│    Used by: Backup engine                                        │
│    When: During backup to capture user/privilege state            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘

Packaging:
  - Percona Toolkit is a Perl-based toolset; bundle Strawberry Perl
    (portable) + pt-* scripts in installer payload
  - Percona XtraBackup 8.4 is a native Windows binary; bundle in payload
  - Total additional payload size: ~50 MB compressed
  - All tools are GPL-licensed; no licensing cost
```

### 12.9 Enhanced DbUp Migration Runner

The standard DbUp runner must be wrapped to support the differential migration architecture:

```csharp
// Pseudocode for enhanced migration runner
class EnhancedMigrationRunner
{
    void ExecuteMigration(MigrationScript script)
    {
        // 1. Classify the DDL operation
        var classification = DdlClassifier.Classify(script);

        // 2. Check if pt-online-schema-change is needed
        if (classification.Algorithm == DdlAlgorithm.Copy
            && GetTableRowCount(classification.TableName) > 1_000_000)
        {
            // Use pt-online-schema-change
            ExecuteViaPtOsc(script, classification);
        }
        else
        {
            // Use standard DbUp execution
            ExecuteViaDbUp(script);
        }

        // 3. Checkpoint
        RecordCheckpoint(script.Name, script.Version);

        // 4. Verify (for critical tables)
        if (classification.IsCriticalTable)
        {
            RunPtTableChecksum(classification.TableName);
        }
    }

    void ExecuteViaPtOsc(MigrationScript script, DdlClassification classification)
    {
        // Build pt-osc command
        var cmd = $"pt-online-schema-change " +
                  $"--alter \"{classification.AlterClause}\" " +
                  $"--execute " +
                  $"--chunk-size=1000 " +
                  $"--max-lag=1s " +
                  $"--no-drop-old-table " +  // keep old table as safety net
                  $"D={database},t={classification.TableName}";

        var result = ProcessRunner.Execute(cmd);

        if (!result.Success)
        {
            // pt-osc failed — old table is untouched
            throw new MigrationException(
                $"pt-online-schema-change failed for {classification.TableName}",
                result.Error);
        }

        // Old table is renamed to _<table>_old — keep until rollback window closes
        // Drop in next release's CONTRACTION phase
    }
}
```

### 12.10 Percona vs MySQL: Tool Selection Rationale

| Capability | MySQL Community | MySQL Enterprise | Percona Toolkit | Recommendation |
|---|---|---|---|---|
| Logical backup | `mysqldump`, `mysqlsh` | Same | Same | `mysqlsh` (parallel, compressed) |
| Physical backup (full) | Not available | MySQL Enterprise Backup | XtraBackup 8.4 | **Percona XtraBackup** (OSS, no license cost) |
| Physical backup (incremental) | Not available | MySQL Enterprise Backup | XtraBackup 8.4 | **Percona XtraBackup** (only OSS option) |
| Online DDL (large tables) | Native `ALTER TABLE` (may lock) | Same | `pt-online-schema-change` | **pt-osc** for COPY-algorithm on large tables |
| Data consistency check | Manual queries | Not available | `pt-table-checksum` | **pt-table-checksum** (chunk-based, efficient) |
| Data drift repair | Manual SQL | Not available | `pt-table-sync` | **pt-table-sync** (automated, safe) |
| Schema comparison | `INFORMATION_SCHEMA` queries | Not available | Not available | **Custom fingerprinter** (Section 12.3.3) |
| Backup encryption | Not native | Enterprise Backup | XtraBackup `--encrypt` | **Percona XtraBackup** (built-in AES-256) |

**Key decision**: We do NOT recommend replacing MySQL Community with Percona Server for MySQL. The Percona *Toolkit* and *XtraBackup* are standalone tools that work with standard MySQL Community. This avoids introducing a MySQL fork while gaining the critical differential backup and safe DDL capabilities.

---

## Appendix A: Precheck Error Code Registry (Draft)

| Code | Category | Check | Severity | Message |
|---|---|---|---|---|
| E001 | OS | Windows version < 10.0.17763 | Block | "Windows 10 version 1809 or later is required." |
| E002 | OS | Not x64 architecture | Block | "64-bit Windows is required." |
| E003 | Disk | C: drive < 10 GB free | Warn | "Low space on C: drive. Installer will use data volume for staging." |
| E004 | Disk | Data volume < 100 GB free | Block | "Data volume needs at least 100 GB free space." |
| E005 | RAM | < 8 GB physical RAM | Block | "At least 8 GB RAM is required." |
| E006 | RAM | < 16 GB physical RAM | Warn | "16 GB RAM is recommended for optimal performance." |
| E007 | Port | 3306 in use by non-ePACS process | Block | "Port 3306 is in use. Please free it or configure alternate port." |
| E008 | Port | 6379 in use by non-ePACS process | Block | "Port 6379 is in use." |
| E009 | Port | 9092 in use by non-ePACS process | Block | "Port 9092 is in use." |
| E010 | Admin | Not running as administrator | Block | "Administrator privileges are required for installation." |
| E011 | Reboot | Pending Windows reboot detected | Warn | "A Windows reboot is pending. Recommended to reboot before installing." |
| E012 | AV | Windows Defender exclusions not configured | Warn | "AV exclusions not detected. Performance may be affected." |
| E013 | Existing | ePACS already installed (fresh install mode) | Block | "ePACS is already installed. Use upgrade or repair mode." |
| E014 | Existing | ePACS not installed (upgrade mode) | Block | "No existing ePACS installation found." |
| E015 | Config | `.epcfg` signature invalid | Block | "Site configuration pack signature is invalid." |
| E016 | Config | `.epcfg` pacs_id mismatch (upgrade) | Block | "Site configuration does not match this PACS node." |
| E017 | Env | Conflicting DOTNET_ROOT detected | Warn | "Conflicting .NET environment variable detected." |
| E018 | Lock | Another installer instance running | Block | "Another installer is already running." |
| E019 | Media | Payload archive checksum mismatch | Block | "Installation media may be corrupted. Verify USB copy." |
| E020 | Upgrade | MySQL Upgrade Checker found critical issues | Block | "Database compatibility issues detected. See report." |
| E021 | Migration | Concurrent migration runner detected (advisory lock held) | Block | "Another migration is in progress. Please wait and retry." |
| E022 | SBOM | Install-time SBOM cross-check failed (file hash mismatch) | Block | "Installation media has been tampered with. Contact support." |
| E023 | ATA | Signing-cert thumbprint not in Anti-Tamper Allowlist | Block | "This installer is not authorized for use. Update your installer or contact support." |
| E024 | Schema | Breaking schema drift detected before upgrade | Block | "Database schema does not match expected baseline. Manual review required." |
| E025 | Backup | Pre-upgrade backup verification failed | Block | "Pre-upgrade backup could not be verified. Upgrade aborted." |
| E026 | Outbox | Outbox at hard ceiling (2M events) | Warn | "Sync backlog is critical. Reconnect to NLDR or contact support." |
| E027 | Cert | Root-CA bundle staleness > 24 months | Warn | "Trust certificates are out of date. Update via next release." |
| E028 | Time | Clock drift > 5 minutes; sync blocked | Block | "System clock is incorrect. Adjust time and retry." |
| E029 | XtraBackup | XtraBackup target on incompatible filesystem | Block | "Backup target must be on local NTFS/ReFS." |
| E030 | Locale | System locale not in supported matrix | Warn | "System locale is not validated. Some features may not work as expected." |
| E031 | `.epcfg` | `.epcfg` nonce already consumed (replay) | Block | "Site configuration has already been used. Request a new pack." |
| E032 | `.epcfg` | `.epcfg` decryption failed (wrong PACS site) | Block | "Site configuration is for a different PACS site." |
| E033 | Self-update | Self-update version skip violation (only N→N+1 supported) | Block | "Major-version self-update is not supported. Use USB media." |
| E034 | Reproducibility | Reproducible-build verification failed | Block | "Release artifact reproducibility check failed. Build is not trusted." |
| E040 | GPO | AppLocker policy blocks ePACS executables | Block | "Group policy prevents ePACS from running. Contact your IT administrator." |
| E041 | GPO | Service start-up type forced to Disabled by GPO | Block | "Group policy disables ePACS services. Contact your IT administrator." |
| E042 | GPO | Defender exclusions missing | Warn | "Antivirus exclusions are missing. Performance and reliability may be affected." |
| E043 | GPO | Outbound 443 blocked by firewall policy | Warn | "Outbound network access is blocked. Sync will fail." |
| E044 | GPO | System locale unsupported by GPO | Block | "System locale is forced to an unsupported value." |
| E055 | Hardware | Cross-profile restore detected | Warn | "Restoring to a different hardware class. Configurations will be regenerated." |

---

## Appendix B: Service Recovery Configuration

```
Service: ePACSMySQL
  First failure:   Restart after 60 seconds
  Second failure:  Restart after 120 seconds
  Subsequent:      Restart after 300 seconds + run D:\ePACSData\tools\collect-support-bundle.ps1
  Reset fail count: 86400 seconds (24 hours)

Service: ePACSCache
  First failure:   Restart after 30 seconds
  Second failure:  Restart after 60 seconds
  Subsequent:      Restart after 120 seconds + run support bundle
  Reset fail count: 86400 seconds

Service: ePACSEventing
  First failure:   Restart after 60 seconds
  Second failure:  Restart after 120 seconds
  Subsequent:      Restart after 300 seconds + run support bundle
  Reset fail count: 86400 seconds

Service: ePACSWeb, ePACS-Loans, ePACS-Fas, ePACS-Membership, etc.
  First failure:   Restart after 30 seconds
  Second failure:  Restart after 60 seconds
  Subsequent:      Restart after 120 seconds + run support bundle
  Reset fail count: 86400 seconds

Service: ePACSSync
  First failure:   Restart after 60 seconds
  Second failure:  Restart after 120 seconds
  Subsequent:      Restart after 300 seconds + run support bundle
  Reset fail count: 86400 seconds

Service: ePACSInstallerAgent
  First failure:   Restart after 10 seconds
  Second failure:  Restart after 30 seconds
  Subsequent:      Restart after 60 seconds
  Reset fail count: 86400 seconds
  Note: This is the watchdog — fastest recovery priority.
```

---

## Appendix C: Configuration Drift Detection Design (G22)

```
On install/upgrade:
  For each generated config file (appsettings.json, my.ini, kafka.properties, garnet.conf, ...):
    hash = SHA-256(file_contents)
    INSERT INTO config_hashes (file_path, expected_hash, computed_at, source_event)
    VALUES (path, hash, NOW(), 'install|upgrade')

Every 60 minutes (Installer Agent):
  For each row in config_hashes:
    current_hash = SHA-256(read_file(file_path))
    IF current_hash != expected_hash:
      log WARNING "Configuration drift detected: {file_path}"
      store diff in drift_events table
      flag in health dashboard
      include in next support bundle
    // Does NOT auto-remediate — operator must decide
    // Repair mode can reset configs if operator chooses
```

---

## Appendix D: Disk Space Monitoring Thresholds (G24)

```
Every 15 minutes (Installer Agent):
  free_pct = free_space(data_volume) / total_space(data_volume) * 100

  IF free_pct < 5%:   // CRITICAL
    log CRITICAL "Data volume critically low: {free_pct}%"
    block non-essential writes (new attachments, non-critical logs)
    generate emergency support bundle
    health dashboard: RED with action required

  ELSE IF free_pct < 10%:  // RED
    log ERROR "Data volume low: {free_pct}%"
    block new backup creation (prevent filling remaining space)
    health dashboard: RED

  ELSE IF free_pct < 20%:  // YELLOW
    log WARNING "Data volume below threshold: {free_pct}%"
    health dashboard: YELLOW

  ELSE:
    health dashboard: GREEN
```

---

---

## 15. Build Provenance & Supply Chain Security

This section addresses **G67, G68, G86, G88** — making the build pipeline itself a hardened, auditable, and tamper-evident asset. EV signing alone is necessary but insufficient: recent supply-chain attacks (XZ Utils, SolarWinds Orion, 3CX) have shown that a trusted signed binary can be malicious if the build pipeline is compromised.

### 15.1 SLSA Level 3 Compliance Targets

| SLSA Requirement | Implementation |
|---|---|
| Source — version controlled | All code in NLPSV-controlled git; required reviewers; signed commits via GPG/SSH |
| Source — verified history | Branch protection: no force-push, no history rewrite, signed-commit required |
| Build — scripted | `Installer.Pipeline.yaml` in repo; no manual build steps |
| Build — build service | Azure DevOps Pipelines with self-hosted agents in NLPSV-controlled VLAN |
| Build — ephemeral environment | Each build runs in fresh VM image (Packer-built, signed); discarded after build |
| Build — isolated | No network during compile/sign; only NuGet via internal proxy with hash-pinned cache |
| Build — parameterless | All inputs from git ref + locked package versions; no dispatch parameters affect output |
| Build — hermetic | All dependencies pre-fetched and verified; no DNS during build |
| Provenance — available | in-toto attestation per artifact, stored alongside the release |
| Provenance — authenticated | Provenance signed by build-service identity (different key than artifact signing) |
| Provenance — service generated | Provenance generated by Azure Pipelines, not by code under build |
| Provenance — non-falsifiable | Build-service signing key is HSM-protected, separate from artifact key |
| Common — security | Two-person review for any pipeline IaC change; no admin access to runners |
| Common — access | All access via SSO + MFA + audit logged |

### 15.2 Reproducible-Build Verification

```
For every release candidate:

  1. Build A on primary build VM image v1
     → produces unsigned-artifact-A.zip + sbom-A.json
  2. Build B on independent VM image v2 (different OS patch level, different Pipeline runner)
     → produces unsigned-artifact-B.zip + sbom-B.json
  3. Compare:
     - SHA-256 of every file in zip after deterministic ordering
     - .NET assembly metadata after MVID stripping (deterministic builds use /p:Deterministic=true)
     - SBOMs identical (modulo timestamps)
  4. Any byte-level difference (excluding signed timestamps) → FAIL release
  5. Cross-check: third independent reviewer hashes signed artifacts vs unsigned
```

### 15.3 In-Toto Provenance Attestation

```yaml
# release-attestation.json (signed by build-service identity)
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [
    { "name": "epacs-installer-3.2.1.exe",
      "digest": { "sha256": "<hash>" } },
    { "name": "release-manifest.yaml",
      "digest": { "sha256": "<hash>" } },
    { "name": "release-sbom.json",
      "digest": { "sha256": "<hash>" } }
  ],
  "predicateType": "https://slsa.dev/provenance/v1",
  "predicate": {
    "buildDefinition": {
      "buildType": "https://slsa.dev/azure-pipelines/v1",
      "externalParameters": {
        "git_ref": "refs/tags/v3.2.1",
        "git_commit": "<commit-sha>"
      },
      "internalParameters": {
        "vm_image": "epacs-build-2026-05-04",
        "agent_pool": "nlpsv-prod-agents"
      }
    },
    "runDetails": {
      "builder": { "id": "https://dev.azure.com/NLPSV/_apis/agents/<agent-id>" },
      "metadata": {
        "invocationId": "<build-id>",
        "startedOn": "2026-05-04T10:00:00Z",
        "finishedOn": "2026-05-04T11:30:00Z"
      },
      "byproducts": [
        { "name": "build-log.txt", "digest": { "sha256": "<hash>" } },
        { "name": "test-results.xml", "digest": { "sha256": "<hash>" } }
      ]
    }
  }
}
```

### 15.4 Install-Time SBOM Cross-Check (G86)

```
On install/upgrade after extraction:
  1. Read bundled release-sbom.json
  2. Walk D:\ePACSData\temp\staging\ and C:\Program Files\ePACS\releases\<v>\
  3. For each .dll, .exe, .jar, native binary:
     - Compute SHA-256
     - Look up in SBOM expected-hashes
     - Mismatch → ABORT install + alert + dump support bundle
  4. Verify SBOM signature against Release CA
  5. Optional: cross-check against bundled offline NVD snapshot
     - List Critical CVEs in installed components
     - Block install if any unaccepted Critical CVE present
     - Accepted CVE register stored in `cve-exceptions.signed.json`
```

### 15.5 Two-Person Production Sign-Off

```
Production signing pipeline gates:

  1. CI build completes; produces unsigned artifacts + provenance
  2. Reviewer A (Engineering Lead or delegate):
     - Inspect provenance, SBOM, test results
     - Approve via signed Azure DevOps approval
  3. Reviewer B (Security Lead or delegate):
     - Independently verify reproducible-build match
     - Approve via signed Azure DevOps approval
  4. Pipeline retrieves signing key from HSM/Key Vault (RBAC: pipeline only)
  5. Sign artifacts; release-attestation.json updated with signing event
  6. Final approval gate (CxO delegate) for major releases only
  7. Artifacts moved to "release" container — read-only thereafter
```

### 15.6 Release Readiness Gate (G88)

```
release-readiness.signed.json (generated by CI before USB media is minted):

  required_checks:
    - reproducible_build_match: PASS
    - sbom_present_and_signed: PASS
    - signing_two_person_approved: PASS
    - clean_vm_fresh_install: PASS
    - clean_vm_upgrade_from_n_minus_1: PASS
    - clean_vm_upgrade_from_n_minus_2_skip: PASS
    - clean_vm_restore_from_backup: PASS
    - clean_vm_uninstall: PASS
    - clean_vm_repair: PASS
    - clean_vm_hotfix: PASS
    - chaos_suite: PASS
    - tamper_negative_tests: PASS
    - mutation_score: 62%  (>= 60% target)
    - locale_matrix: PASS
    - time_skip_tests: PASS

  signed_by:
    - engineering_lead: <signature>
    - security_lead: <signature>
    - cxo_delegate: <signature>  (major releases)

  minted_at: <timestamp>
  artifact_digests: [...]
```

---

## 16. Threat Model and Security Architecture (STRIDE)

This section formalizes **G69, G82** — providing a STRIDE-based analysis of threats and mapping each to the implemented mitigation. The full per-asset threat matrix is in Appendix I.

### 16.1 Trust Boundaries

```
┌─────────────────────────────────────────────────────────────────┐
│  TRUST BOUNDARIES                                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  TB-1: Internet ↔ PACS LAN                                       │
│    - Only outbound 443 to NLDR FQDN                              │
│    - Inbound: blocked at Windows Firewall                        │
│                                                                  │
│  TB-2: PACS LAN ↔ PACS host                                      │
│    - Only HTTPS 443 (Kestrel) from internal LAN                  │
│    - mTLS optional for DCCB hub usage                            │
│                                                                  │
│  TB-3: Operator OS session ↔ ePACS services                      │
│    - Operator runs admin during install only                     │
│    - Day-to-day: operator uses ePACS Web (HTTPS) → no host login │
│                                                                  │
│  TB-4: Service-to-service (within host) ─── NEW (G69)            │
│    - Per-service MySQL/Garnet/Kafka credentials                  │
│    - Per-service local mTLS via installer-generated CA           │
│    - DPAPI + cert-wrapped credential cache                       │
│                                                                  │
│  TB-5: ePACS DB ↔ Traceability DB                                │
│    - Same MySQL instance, separate users with separate grants    │
│    - Traceability writes are read-only from main app perspective │
│                                                                  │
│  TB-6: USB media ↔ Installer                                     │
│    - SHA-256 verified, manifest-signed                            │
│    - SBOM cross-check at install time                            │
│                                                                  │
│  TB-7: NLDR ↔ Sync Agent                                         │
│    - mTLS with per-PACS client cert                              │
│    - Idempotency via outbox event_id                             │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 16.2 STRIDE Summary (full table in Appendix I)

| STRIDE Category | Top Threats | Primary Mitigations |
|---|---|---|
| **S**poofing | Forged installer, forged `.epcfg`, forged Override Token, forged sync packets, forged ATA | EV signing + ATA allow-list (G68); `.epcfg` per-site encryption (G92); JWT signed Override Token; mTLS to NLDR; signed ATA |
| **T**ampering | Modified binaries post-install, modified config, modified backup, modified audit log, modified schema | Authenticode + payload SHA-256; config drift detection (G22); backup signature + chain (G87); audit-log hash chain; schema fingerprinting (G45) |
| **R**epudiation | Operator denies upgrade, sync ACK forgery, who modified config | Audit log with hash chain; Override Token signed + nonce + reason; backup chain attestation (G87); Traceability captures all DB corrections |
| **I**nformation Disclosure | Secrets in support bundle, attachments leak, plaintext keys in dumps, sniffed sync payload, leaked `.epcfg` | Support bundle redaction + golden list; encrypted keyring (DPAPI + cert); mTLS sync; `.epcfg` per-site encryption + nonce (G92) |
| **D**enial of Service | Disk fill, DB overload, backup loop, AV interference, Garnet AOF corruption, outbox flood | Disk monitoring tiers (G24); health endpoint cache + rate-limit (G79); AV exclusions; Garnet AOF quarantine + snapshot (G91); outbox ceilings (G66) |
| **E**levation of Privilege | Service-to-service hop, override-token replay, key-store extraction, GPO bypass | Per-service auth + mTLS (G69); nonce-based replay prevention; HSM/Key Vault + DPAPI; GPO compatibility precheck (G72) |

### 16.3 Defense-in-Depth Layers

```
Layer 1: Physical / Premises
  ├── Site preparation checklist (locked room recommended)
  └── Out of scope for installer; operator/site responsibility

Layer 2: OS Hardening
  ├── Windows Defender + ePACS exclusions (G8)
  ├── Pending-reboot enforcement (G34)
  ├── GPO compatibility (G72)
  └── BitLocker (operator-optional; recommended)

Layer 3: Network
  ├── Firewall rules (localhost-only DB/cache/eventing)
  ├── Outbound 443 only to approved NLDR
  └── No inbound from internet

Layer 4: Service Authentication (NEW — G69)
  ├── Per-service MySQL users with column-level grants
  ├── Garnet AUTH password per service
  ├── Kafka SASL/PLAIN per principal
  └── Local mTLS via installer-generated CA

Layer 5: Application
  ├── Authenticode + manifest verification at install
  ├── ATA allow-list at runtime (G68)
  ├── Override Token verification + nonce
  └── Audit-log hash chain

Layer 6: Data
  ├── Encrypted backups (cert-wrapped key)
  ├── Encrypted keyring (DPAPI + cert escrow)
  ├── Decimal/numeric integrity (G89)
  └── Schema fingerprint (G45)

Layer 7: Operational
  ├── Drift detection (G22)
  ├── Disk monitoring (G24)
  ├── Health endpoints with rate-limit (G79)
  ├── Diagnostic ping (G80)
  └── Support bundle with redaction
```

### 16.4 Security Operations Runbooks

| Event | Runbook |
|---|---|
| Suspected installer forgery | Appendix M (Code-Signing Key Compromise Runbook) |
| `.epcfg` exposure | Revoke `.epcfg` nonce at state federation; re-issue; rotate per-site key |
| Audit-log tamper detected | Quarantine PACS from sync; preserve evidence; central forensic team mobilized |
| Sync replay attack | event_id idempotency rejects; alert on repeat attempts; consider sync circuit-break |
| AV quarantines binary | Restore from quarantine OR re-install from media; verify Authenticode + ATA |
| Override Token replay | Nonce store rejects; alert SecOps; investigate operator account |

---

## 17. Fleet Management & HQ-Side Visibility

This section addresses the gap left by **G64, G87, and pilot-evidence gathering** — when 500+ PACS are in production, NLPSV needs centralized visibility into installer fleet state, even though PACS are offline-first.

### 17.1 Fleet State Model

The Sync Agent uploads a small **Fleet Health Manifest** alongside business sync (opt-in per `.epcfg`):

```yaml
# fleet-health.yaml (uploaded to NLDR with each sync window)
pacs_id: PACS-MH-3-1234
state_code: MH
district: Pune
hardware_profile: SmallPACS
stack_version: 3.2.1
schema_fingerprint: <sha256>
last_install: 2026-04-15T10:00:00Z
last_upgrade: 2026-05-01T03:30:00Z
last_backup: 2026-05-04T02:15:00Z
last_backup_verified: true
last_dr_drill: 2026-04-01T...
disk_free_pct_data: 42
disk_free_pct_os: 78
attachments_used_gb: 12.4
attachments_quota_gb: 50
sync_lag_minutes: 8
outbox_pending: 124
outbox_overflow: 0
config_drift_count_30d: 0
av_exclusions_ok: true
clock_drift_seconds: 1.2
cert_expiry_days: 287
root_ca_age_days: 45
mysql_binlog_size_gb: 3.1
audit_partition_health: OK
anomaly_flags_open_30d: 2
incidents_p1_30d: 0
incidents_p2_30d: 1
```

### 17.2 NLPSV HQ Dashboard (built on top of fleet manifests)

| View | Content |
|---|---|
| Fleet inventory | All PACS by state/district; stack version distribution; install age histogram |
| Health roll-up | % green/yellow/red; top 10 sites with active issues |
| Backup posture | Last successful backup age across fleet; sites missing backup > 48h |
| Sync posture | Sync lag distribution; sites with backlog > 1h; outbox-overflow events |
| Schema posture | Sites still on N-2; sites with detected drift; upgrade campaigns |
| Cert posture | Sites with cert expiry < 60d; root-CA staleness alerts |
| Incident heat map | P1/P2 by district; trending patterns |
| DR posture | Sites with DR drill > 90 days old (G27 violations) |
| Anomaly posture | Open Traceability flags by rule and severity |

### 17.3 Update Campaigns

```
Upgrade campaign (e.g., roll out v3.2.1 to all MH sites):

  1. NLPSV publishes signed campaign manifest:
     {
       "campaign_id": "C2026-05-MH-3.2.1",
       "target_version": "3.2.1",
       "scope": { "states": ["MH"], "wave": 2 },
       "media": "USB",
       "effective_from": "2026-06-01",
       "deadline": "2026-07-15",
       "rollback_window_days": 30
     }
  2. Campaign manifest delivered via sync to in-scope PACS
  3. Health dashboard at PACS shows: "Upgrade scheduled — USB pending"
  4. Operator receives USB via state federation courier
  5. Installer verifies campaign_id matches its assigned campaign
  6. Post-upgrade telemetry confirms via sync; HQ dashboard tracks rollout %

Hotfix campaign (G30) — same model with:
  - Smaller signed package
  - No schema migrations
  - Self-update applicable for installer-only patches (G64)
```

### 17.4 Privacy and Data Minimization

```
Fleet manifest (above) is intentionally minimized:
  - NO business data
  - NO PII
  - NO secrets
  - NO file paths (only counts/sizes)
  - Identifiers are pacs_id (already known to NLPSV) only

Encryption:
  - Fleet manifest encrypted with NLPSV public key
  - Signed with PACS attestation key
  - Replay-protected via timestamp + nonce

Opt-in:
  - Fleet manifest sync is OPT-IN per .epcfg
  - Default OFF in pilot; ON after governance approval
```

---

## 18. Operational SLA / SLO Matrix

This section addresses **G81** — formalizing what the installer team commits to and what the field commits to in return. Detailed table in Appendix K.

### 18.1 Top-Level SLOs

| Metric | Target | Measurement |
|---|---|---|
| Installer crash rate | < 0.5% of executions | Auto-uploaded crash reports / total invocations |
| Fresh install success rate | > 99% | Successful AC-001 completions / attempts |
| Upgrade success rate | > 98% | Successful upgrades / attempts |
| Rollback success rate (when upgrade fails) | 100% | No successful upgrade-fail-then-data-loss observed |
| Backup verification success rate | > 99.9% | Verified backups / total backups, fleet-wide |
| Restore success rate (DR drill) | > 99% | Drills completing within RTO / drills |
| Sync recovery success rate (post-reconnect) | > 99% within 1h | Sync drains within target / reconnections |
| P1 hotfix turnaround | ≤ 24h from confirmed P1 | Confirmed P1 → signed hotfix media ready |
| Support bundle triage | ≤ 4 business hours | Bundle uploaded → engineering response posted |
| DR drill cadence compliance | 100% per quarter | Sites with completed drill / sites in scope |

### 18.2 Per-PACS Operational SLAs

| Metric | Target |
|---|---|
| Per-PACS uptime (excluding scheduled maintenance) | 99.0% |
| Daily backup completion (within 24h window) | 100% |
| Health endpoint P95 latency | < 500ms |
| Sync lag steady-state | < 10 minutes |
| Cert expiry warning lead time | ≥ 30 days |

### 18.3 Field-Side Commitments (operator/state federation)

| Commitment | Threshold |
|---|---|
| AV exclusions applied | Within 48h of install |
| Backup target available | Daily |
| Power backup (UPS) | Recommended; not blocking |
| NTP/internet for time sync | When available |
| Quarterly DR drill participation | 100% |
| Operator training completion | Before site go-live |
| Site cleaning (dust/insects) | Quarterly |

---

## 19. Internationalization & Locale Integrity

This section addresses **G73** — ensuring Indic scripts, multi-byte characters, and locale-sensitive parsing don't silently corrupt data.

### 19.1 Locale Test Corpus

A canonical 1,000-entry test corpus is bundled in `/tests/i18n/test-corpus.json`:

```json
{
  "names": [
    { "lang": "hi", "value": "रामेश्वर सिंह कुशवाहा", "issue_class": "matras+conjuncts" },
    { "lang": "mr", "value": "ज्ञानेश्वर महाराज", "issue_class": "ज्ञ-conjunct" },
    { "lang": "te", "value": "శ్రీనివాస రెడ్డి", "issue_class": "subscripts" },
    { "lang": "ta", "value": "க்ருஷ்ணமூர்த்தி", "issue_class": "pulli+vowels" },
    ...
  ],
  "addresses": [...],
  "amounts": [...],
  "dates": [...]
}
```

### 19.2 Validation Pipeline

```
For each name in corpus:
  1. INSERT into cus_customerpersonaldetails with utf8mb4_0900_ai_ci
  2. SELECT and assert byte-equal to original
  3. SELECT WHERE name = '<original>' returns the row (collation match)
  4. Sort 1,000 names; verify against expected lexicographic order
  5. Export via mysqldump; reimport; assert byte-equal
  6. Backup → restore → assert byte-equal
  7. Sync round-trip via JSON → assert byte-equal (NFC normalization)
```

### 19.3 Filename Normalization

```
Operator uploads attachment "साक्ष्य.pdf" from Windows (NFC) and macOS (NFD).
Without normalization: two distinct filenames, two storage entries, dupe risk.

Mitigation:
  - All attachment filenames NFC-normalized at upload (Web layer)
  - Stored filename is sanitized SHA-256 hash; original filename in DB column
  - Display always uses DB-stored original
```

### 19.4 BOM and Encoding Quirks

```
.epcfg (JSON):
  - Parser strips UTF-8 BOM (EF BB BF) if present
  - Canonicalize JSON before signature verification (RFC 8785 JCS)
  - Reject any non-UTF-8 encoding

backup-manifest.yaml:
  - Same BOM handling
  - YAML 1.2 strict mode; no implicit type coercion (no Norway-problem with "no" → false)

Logs:
  - Always UTF-8, no BOM
  - JSON-line format; one event per line
```

### 19.5 Locale-Sensitive Parsing Hazards (banned)

```
BANNED in installer code:
  - DateTime.Parse(s)        — locale-dependent
  - decimal.Parse(s)         — locale-dependent (1,000 vs 1.000)
  - ToUpper() / ToLower()    — locale-dependent (Turkish I problem)

REQUIRED:
  - DateTime.Parse(s, CultureInfo.InvariantCulture, ...)
  - decimal.Parse(s, CultureInfo.InvariantCulture)
  - ToUpperInvariant() / ToLowerInvariant()
  - Path comparisons via OrdinalIgnoreCase, never CurrentCulture
  - All file I/O explicitly UTF-8 (no locale dependency)

Roslyn analyzer enforces this via custom rule + CI gate.
```

---

## 20. Expanded Validation Test Matrix

This section consolidates **G74, G90, G93, G94** test coverage. Detailed cells in Appendix N.

### 20.1 Coverage Pillars

```
Functional × Non-Functional × Operating Conditions

Functional:
  fresh-install, upgrade-N1→N, upgrade-N2→N (skip), repair, uninstall,
  backup, restore-same-host, restore-new-host, restore-cross-profile,
  hotfix-apply, sync-online, sync-offline, sync-reconnect,
  conflict-resolution, drift-detection-and-repair

Non-Functional:
  power-cut, disk-full, AV-interference, clock-skew, network-partition,
  USB-corruption, thermal-throttle, concurrent-installer, GPO-override,
  service-crash, OOM, FS-fill, locale-non-english, DST-transition,
  mutation, fuzz, idempotency, time-skip, large-dataset (1/10/50/100/200 GB)

Operating Conditions:
  Win10-22H2, Win-Server-2019, Win-Server-2022, x64, ReFS-data, NTFS-data,
  domain-joined, standalone, Hyper-V-Gen2, bare-metal,
  small-PACS-profile (8GB), DCCB-hub-profile (32GB),
  locale-en-US, locale-en-IN, locale-hi-IN, locale-mr-IN
```

### 20.2 Test Pyramid Targets

| Level | Tests | Where | Frequency |
|---|---|---|---|
| Unit | ≥ 1,500 | Per project | Every PR |
| Integration | ≥ 200 | Pester + Hyper-V | Every PR merge |
| E2E | ~ 60 (matrix) | Hyper-V VMs | Release candidate |
| Chaos | ~ 20 | Hyper-V + fault injectors | Release candidate |
| Mutation | (60% score) | Stryker.NET | Nightly |
| Fuzz | ~ 5 entry points | SharpFuzz | Release candidate |
| Locale | 6 locales × 4 flows | Hyper-V locale images | Release candidate |
| Time-skip | ~ 8 scenarios | Virtual clock | Release candidate |
| Tamper negative | ~ 12 scenarios | Synthetic forgeries | Every PR |
| Idempotency | ~ 6 invariants | Hyper-V diff | Every PR |
| Concurrency | ~ 4 scenarios | Multi-process harness | Every PR merge |

### 20.3 Release Gating Logic

```
Release candidate PASSES when:
  ALL units PASS
  ALL integrations PASS
  ALL E2E PASS
  ALL chaos PASS
  Mutation score >= 60%
  Fuzz: 0 crashes in 1M iterations × 5 parsers
  Locale matrix: 0 failures
  Time-skip: 0 failures
  Tamper: 0 false-positives, 100% true-positive
  Idempotency: byte-identical state on rerun
  Concurrency: no lock-related corruption
  Reproducible-build: bit-identical (excluding signed timestamps)
  SBOM cross-check: 0 unaccounted files
  CVE scan: 0 unaccepted Critical CVEs
  Two-person sign-off: complete

ANY ONE failure → release blocked.
```

---

*End of enhanced plan v3.2. Ready for review.*


---

## Appendix E: DDL Operation Classification Matrix

This matrix is used by the Enhanced Migration Runner (Section 12.9) to determine execution strategy for each migration script.

| Operation | MySQL 8.4 Algorithm | Concurrent DML? | Table Rebuild? | Use pt-osc if > 1M rows? | Notes |
|---|---|---|---|---|---|
| `ADD COLUMN` (nullable, no default) | INSTANT | Yes | No | No | Metadata-only change |
| `ADD COLUMN` (nullable, with default) | INSTANT | Yes | No | No | Metadata-only change |
| `ADD COLUMN` (NOT NULL, with default) | INSTANT | Yes | No | No | MySQL 8.0.12+ |
| `DROP COLUMN` | INSTANT | Yes | No | No | MySQL 8.0.29+ |
| `RENAME COLUMN` | INSTANT | Yes | No | No | |
| `ADD INDEX` | INPLACE | Yes | No (builds in background) | No | May be slow on large tables but non-blocking |
| `ADD UNIQUE INDEX` | INPLACE | Yes | No | No | Fails if duplicates exist |
| `DROP INDEX` | INPLACE | Yes | No | No | |
| `ADD FOREIGN KEY` | INPLACE | Yes | No | No | Validates existing data |
| `DROP FOREIGN KEY` | INPLACE | Yes | No | No | |
| `CHANGE COLUMN TYPE` (compatible) | INPLACE | Yes | Yes (in-place rebuild) | **Yes** | e.g., VARCHAR(100)→VARCHAR(200) |
| `CHANGE COLUMN TYPE` (incompatible) | COPY | **No (locked)** | Yes (full copy) | **Yes** | e.g., INT→BIGINT, VARCHAR→TEXT |
| `CONVERT TO CHARACTER SET` | COPY | **No (locked)** | Yes (full copy) | **Yes** | Required for utf8mb3→utf8mb4 |
| `ADD COLUMN` (after specific column) | COPY | **No (locked)** | Yes (full copy) | **Yes** | Reorders physical layout |
| `CHANGE ROW_FORMAT` | COPY | **No (locked)** | Yes (full copy) | **Yes** | |
| `OPTIMIZE TABLE` | COPY | **No (locked)** | Yes (full copy) | **Yes** | Rebuilds table + indexes |
| `RENAME TABLE` | INSTANT | Yes | No | No | Metadata-only |
| `TRUNCATE TABLE` | N/A | N/A | Drops + recreates | No | Use for transient tables only |

**Migration script header format** (enforced by CI validation):

```sql
-- @migration: V025__description.sql
-- @classification: INSTANT | INPLACE | COPY
-- @tables: table1, table2
-- @estimated_rows: 1500000
-- @estimated_duration: 30s
-- @requires_pt_osc: true | false
-- @phase: EXPAND | MIGRATE | CONTRACT
-- @rollback: V025__rollback.sql | NONE
-- @fk_dependencies: parent_table1, parent_table2
```

---

## Appendix F: Schema Fingerprint Design

### Fingerprint Capture Query

```sql
-- Capture table fingerprint
SELECT
  t.TABLE_NAME,
  t.ENGINE,
  t.TABLE_COLLATION,
  t.AUTO_INCREMENT,
  t.TABLE_ROWS,
  GROUP_CONCAT(
    CONCAT(c.COLUMN_NAME, ':', c.COLUMN_TYPE, ':', c.IS_NULLABLE, ':',
           IFNULL(c.COLUMN_DEFAULT, 'NULL'), ':', IFNULL(c.EXTRA, ''))
    ORDER BY c.ORDINAL_POSITION
    SEPARATOR '|'
  ) AS column_fingerprint,
  (SELECT GROUP_CONCAT(
    CONCAT(s.INDEX_NAME, ':', s.COLUMN_NAME, ':', s.NON_UNIQUE)
    ORDER BY s.INDEX_NAME, s.SEQ_IN_INDEX
    SEPARATOR '|'
  ) FROM INFORMATION_SCHEMA.STATISTICS s
    WHERE s.TABLE_SCHEMA = t.TABLE_SCHEMA AND s.TABLE_NAME = t.TABLE_NAME
  ) AS index_fingerprint,
  (SELECT GROUP_CONCAT(
    CONCAT(k.CONSTRAINT_NAME, ':', k.COLUMN_NAME, ':',
           k.REFERENCED_TABLE_NAME, ':', k.REFERENCED_COLUMN_NAME)
    ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
    SEPARATOR '|'
  ) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
    WHERE k.TABLE_SCHEMA = t.TABLE_SCHEMA
      AND k.TABLE_NAME = t.TABLE_NAME
      AND k.REFERENCED_TABLE_NAME IS NOT NULL
  ) AS fk_fingerprint
FROM INFORMATION_SCHEMA.TABLES t
JOIN INFORMATION_SCHEMA.COLUMNS c
  ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
WHERE t.TABLE_SCHEMA = 'MHCluster3'
  AND t.TABLE_TYPE = 'BASE TABLE'
GROUP BY t.TABLE_NAME, t.ENGINE, t.TABLE_COLLATION, t.AUTO_INCREMENT, t.TABLE_ROWS
ORDER BY t.TABLE_NAME;
```

### Fingerprint Storage

```sql
CREATE TABLE epacs_meta.schema_fingerprint (
  id INT AUTO_INCREMENT PRIMARY KEY,
  stack_version VARCHAR(20) NOT NULL,
  captured_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  capture_event ENUM('INSTALL', 'UPGRADE', 'HEALTH_CHECK', 'MANUAL') NOT NULL,
  table_count INT NOT NULL,
  view_count INT NOT NULL,
  fk_count INT NOT NULL,
  index_count INT NOT NULL,
  fingerprint_hash CHAR(64) NOT NULL,  -- SHA-256
  fingerprint_json LONGTEXT NOT NULL,  -- Full JSON for diff
  INDEX idx_version (stack_version),
  INDEX idx_captured (captured_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```

### Drift Detection Algorithm

```
function DetectSchemaDrift(expected_version):
  expected = LoadFingerprint(expected_version)
  current = CaptureCurrentFingerprint()

  drift_report = {
    added_tables: [],      // tables in current but not expected
    missing_tables: [],    // tables in expected but not current
    modified_tables: [],   // tables with column/index/FK differences
    added_columns: [],
    missing_columns: [],
    type_changes: [],
    added_indexes: [],
    missing_indexes: [],
    fk_changes: [],
    charset_mismatches: []
  }

  for each table in UNION(expected.tables, current.tables):
    if table in current but not expected:
      drift_report.added_tables.append(table)  // BENIGN
    elif table in expected but not current:
      drift_report.missing_tables.append(table)  // BREAKING
    else:
      compare columns, indexes, FKs, charset
      classify each difference as BENIGN, COMPATIBLE, or BREAKING

  drift_report.severity = max(all difference severities)
  return drift_report
```

---

## Appendix G: Percona Toolkit Packaging for Offline Install

Since PACS nodes are offline, all Percona tools must be bundled in the installer payload:

| Component | Version | Size (compressed) | Platform | Notes |
|---|---|---|---|---|
| Percona XtraBackup 8.4 | 8.4.0-2+ | ~25 MB | Windows x64 native | Binary distribution |
| Percona Toolkit | Latest stable | ~5 MB | Perl scripts | Requires Perl runtime |
| Strawberry Perl (portable) | 5.38+ | ~20 MB | Windows x64 | Portable; no system install needed |
| **Total additional payload** | | **~50 MB** | | Negligible vs 2.5 GB total |

**Installation layout**:
```
C:\Program Files\ePACS\tools\
  percona\
    xtrabackup\
      xtrabackup.exe
      xbstream.exe
      xbcrypt.exe
    toolkit\
      pt-online-schema-change
      pt-table-checksum
      pt-table-sync
      pt-show-grants
    perl\
      perl.exe
      (portable Strawberry Perl distribution)
```

**PATH configuration**: Installer adds `C:\Program Files\ePACS\tools\percona\xtrabackup\` and `C:\Program Files\ePACS\tools\percona\perl\` to the system PATH for the ePACS service accounts only (not system-wide).

---

## 13. Rural Resilience Architecture

This section addresses the specific challenges of deploying and operating ePACS in Indian rural conditions: unreliable power, intermittent 4G connectivity, high temperatures, humidity, low-quality USB media, and operators with limited technical background.

### 13.1 Power-Cut Resilience (G59)

**Design principle**: Every operation must be resumable after a hard power loss at any point. No operation should leave the system in an unrecoverable state.

```
┌─────────────────────────────────────────────────────────────────┐
│  POWER-CUT RESILIENCE MATRIX                                     │
├──────────────────────┬──────────────────────────────────────────┤
│  Operation           │  Recovery behavior                        │
├──────────────────────┼──────────────────────────────────────────┤
│  Fresh install       │  Installer Agent detects incomplete       │
│  (payload extract)   │  install on boot → enters RECOVERY →     │
│                      │  resumes from last extracted payload      │
├──────────────────────┼──────────────────────────────────────────┤
│  Fresh install       │  MySQL datadir incomplete → installer    │
│  (DB init)           │  drops and re-initializes (no data yet)  │
├──────────────────────┼──────────────────────────────────────────┤
│  Upgrade             │  Junction still points to old version →  │
│  (binary staging)    │  old version starts normally → retry     │
│                      │  upgrade from staging step               │
├──────────────────────┼──────────────────────────────────────────┤
│  Upgrade             │  Checkpoint in schema_version_registry → │
│  (DB migration)      │  resume from last committed script →     │
│                      │  InnoDB crash recovery handles in-flight │
│                      │  transactions automatically              │
├──────────────────────┼──────────────────────────────────────────┤
│  Upgrade             │  Junction not yet flipped → old version  │
│  (junction flip)     │  still active → retry from flip step     │
├──────────────────────┼──────────────────────────────────────────┤
│  Backup              │  Incomplete backup detected by missing   │
│  (in progress)       │  manifest signature → discard incomplete │
│                      │  → previous valid backup retained        │
├──────────────────────┼──────────────────────────────────────────┤
│  Restore             │  Pre-restore safety backup exists →      │
│  (in progress)       │  revert to safety backup → retry restore │
├──────────────────────┼──────────────────────────────────────────┤
│  Business operation  │  InnoDB crash recovery → MySQL restarts  │
│  (normal use)        │  → services restart via Windows recovery │
│                      │  actions → Installer Agent verifies      │
│                      │  health within 5 min of boot             │
├──────────────────────┼──────────────────────────────────────────┤
│  Sync upload         │  Chunked upload → resume from last ACK'd │
│  (to NLDR)           │  chunk → no duplicate data sent          │
├──────────────────────┼──────────────────────────────────────────┤
│  Audit write         │  Transactional sink → InnoDB guarantees  │
│  (Traceability)      │  → deferred journal mode available as    │
│                      │  fallback (JSONL file, drained on next   │
│                      │  successful DB write)                    │
└──────────────────────┴──────────────────────────────────────────┘
```

**MySQL hardening for power-cut**:
```ini
# my.ini — enforced by installer, not operator-configurable
[mysqld]
innodb_flush_log_at_trx_commit = 1    # flush redo log on every commit
sync_binlog = 1                        # sync binlog on every commit
innodb_doublewrite = ON                # protect against torn pages
innodb_checksum_algorithm = crc32      # detect corruption
innodb_buffer_pool_dump_at_shutdown = ON
innodb_buffer_pool_load_at_startup = ON
```

**Kafka hardening for power-cut**:
```properties
# kafka.properties — enforced by installer
flush.messages=1
flush.ms=1000
log.flush.interval.messages=1
unclean.leader.election.enable=false
```

**Installer state checkpoint**:
```
On each state transition (e.g., PRECHECK → INSTALL → HEALTH):
  1. Write checkpoint to D:\ePACSData\installer\state.json:
     { "state": "INSTALL", "phase": "PAYLOAD_EXTRACT", "payload_index": 3,
       "timestamp": "2026-05-04T10:15:30Z", "version": "3.2.1" }
  2. fsync the file (NTFS guarantees after fsync)
  3. Proceed with next operation

On startup after power-cut:
  1. Read state.json
  2. If state != SUCCESS and state != FAILED:
     → Enter RECOVERY mode
     → Resume from recorded state/phase
  3. If state.json is corrupt (torn write):
     → Enter SAFE mode
     → Run health checks on all services
     → If healthy: mark SUCCESS
     → If unhealthy: generate support bundle + alert operator
```

### 13.2 Intermittent Connectivity Handling (G60)

```
┌─────────────────────────────────────────────────────────────────┐
│  CONNECTIVITY STATE MACHINE                                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────┐  probe OK   ┌──────────┐  5 failures  ┌────────┐ │
│  │ CONNECTED │◄───────────│HALF_OPEN  │◄────────────│  OPEN  │ │
│  │           │            │(1 probe)  │             │        │ │
│  └─────┬────┘            └──────────┘             └────┬───┘ │
│        │ failure                                        │      │
│        └──────────────────────────────────────────────►│      │
│                                                   5 min timer  │
│                                                   ┌────▼───┐   │
│                                                   │HALF_OPEN│  │
│                                                   └────────┘   │
│                                                                  │
│  In OPEN state:                                                  │
│    - Business operations: UNAFFECTED (MySQL is source of truth)  │
│    - Outbox writes: continue to MySQL (always)                   │
│    - Kafka relay: continues locally (Kafka is local)             │
│    - NLDR sync: PAUSED (queued in outbox)                        │
│    - Audit writes: continue to local erp_traceability DB         │
│    - Health dashboard: shows "NLDR: Disconnected (X hours)"      │
│    - Probe: HTTPS HEAD to NLDR every 60s                         │
│                                                                  │
│  Bandwidth detection (on reconnect):                             │
│    - Measure probe response time                                 │
│    - < 200ms → 4G → chunk size 1 MB                             │
│    - 200ms–1s → 3G → chunk size 256 KB                          │
│    - > 1s → 2G → chunk size 64 KB                               │
│    - Adjust dynamically during sync drain                        │
│                                                                  │
│  Sync drain priority (when reconnected):                         │
│    1. Financial transactions (loans, deposits, FAS)              │
│    2. Audit events (compliance-critical)                         │
│    3. Master data changes                                        │
│    4. Telemetry/health data                                      │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 13.3 USB Media Resilience (G61)

```
USB Verification Flow:
  1. Operator inserts USB with installer package
  2. Installer stub reads embedded manifest (last 4 KB of EXE)
  3. Compute SHA-256 of payload archive
  4. Compare against manifest hash
  5. If MATCH → proceed to extraction
  6. If MISMATCH:
     a. Display: "Installation media may be damaged."
     b. Display: "Error code: E019"
     c. Display: "Please request a new copy from your district office."
     d. Display: "If this is a split package, verify all parts are present."
     e. Offer: "Retry verification" button (in case of transient read error)
     f. Log: full hash comparison details to installer log

  Split-volume verification (FAT32 USB):
    Part 1: epacs-3.2.1.part1.zip → SHA-256 check
    Part 2: epacs-3.2.1.part2.zip → SHA-256 check
    ...
    If any part fails: identify which part is corrupt
    Display: "Part 2 of 3 is damaged. Only this part needs to be re-copied."

  Extraction resilience:
    - Extract to D:\ePACSData\temp\staging\
    - Track extracted files in staging-manifest.json
    - On power-cut during extraction: resume from last extracted file
    - On completion: verify all extracted files against manifest
    - Only then proceed to install
```

### 13.4 Environmental Stress (G62)

| Condition | Risk | Mitigation |
|---|---|---|
| Temperature 35–45°C | Thermal throttling, SSD wear | Monitor via WMI; pause long operations at 85°C; reduce parallelism on small hardware |
| Humidity 60–90% | Condensation on cold-start, connector corrosion | Not software-mitigable; document in operator guide: allow 15 min warm-up after power restore in monsoon |
| Dust | Fan clogging, overheating | Not software-mitigable; document quarterly cleaning in operator guide |
| Voltage fluctuation | Dirty shutdown, component damage | MySQL doublewrite + InnoDB crash recovery; recommend voltage stabilizer in operator guide |
| Insects/rodents | Cable damage, short circuits | Not software-mitigable; document in site preparation checklist |

### 13.5 Operator Error Prevention

| Scenario | Prevention |
|---|---|
| Operator runs upgrade without backup | Installer BLOCKS upgrade until backup is verified (mandatory, not optional) |
| Operator unplugs USB during install | Extraction to staging area; install from staging; USB can be removed after extraction |
| Operator types wrong PACS ID | `.epcfg` contains signed PACS ID; manual entry validated against state registry format |
| Operator runs installer twice | Named mutex (G32); clear error message |
| Operator force-kills installer | State checkpoint (Section 13.1); RECOVERY mode on next run |
| Operator manually edits config files | Drift detection (G22); health dashboard warning; repair mode restores from templates |
| Operator runs `DELETE` on MySQL | Correction tool audit trail (BRD 16.1); Traceability module logs all DB corrections |
| Operator ignores health warnings | Escalating alerts: Yellow → Red → Critical; Critical blocks new business operations after 7 days |

---

## 14. Traceability Module Integration

The `Intellect.Erp.Traceability` module is a compliance-grade audit system with 11 MySQL tables, Kafka outbox integration, geo-tagging, 8 anomaly detection rules, and 4 retention classes. It must be fully integrated into the installer lifecycle.

### 14.1 Architecture Fit

```
┌─────────────────────────────────────────────────────────────────┐
│  ePACS Node                                                      │
│                                                                  │
│  ┌──────────────────────┐    ┌──────────────────────┐           │
│  │  Main DB              │    │  erp_traceability DB  │           │
│  │  (MHCluster3)         │    │  (11 tables)          │           │
│  │  1,057 tables         │    │  4 partitioned        │           │
│  │  Business data        │    │  ULID PKs             │           │
│  │  sync_outbox          │    │  AuditOutbox          │           │
│  └──────────┬───────────┘    └──────────┬───────────┘           │
│             │                           │                        │
│  ┌──────────▼───────────────────────────▼───────────┐           │
│  │  MySQL 8.4 (single instance, two databases)       │           │
│  │  Shared my.ini, shared service account             │           │
│  └──────────────────────┬───────────────────────────┘           │
│                         │                                        │
│  ┌──────────────────────▼───────────────────────────┐           │
│  │  Kafka (local)                                     │           │
│  │  Business topics: epacs.local.sync-ready, ...      │           │
│  │  Audit topics: traceability.activity-recorded,     │           │
│  │    traceability.flag-lifecycle, ...  (5 topics)    │           │
│  └──────────────────────────────────────────────────┘           │
│                                                                  │
│  Backup scope: BOTH databases + Kafka state                      │
│  Migration scope: Main DB (DbUp) + Traceability DB (EF Core)    │
│  Sync scope: Main outbox → NLDR + Audit outbox → NLDR           │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 14.2 Installer Responsibilities for Traceability

| Phase | Action |
|---|---|
| **Fresh install** | Create `erp_traceability` database; create `erp_trace_app` user with least-privilege grants; run EF Core migrations (creates 11 tables + partitions); pre-create 3 months of future partitions; seed `ActionCatalogue` and `RuleCatalogue`; configure `appsettings.Traceability` section from `.epcfg` |
| **Upgrade** | Run EF Core migrations for Traceability (may add columns, new tables, new rules); verify partition health; add new Kafka topics if needed |
| **Backup** | Include `erp_traceability` database in backup package (separate dump file); include partition metadata; include Traceability config |
| **Restore** | Restore `erp_traceability` alongside main DB; verify partition integrity; re-seed catalogues if needed |
| **Health check** | Verify Traceability health endpoint (`/health/traceability`): DB reachability, outbox backlog, rule engine heartbeat |
| **Installer Agent** | Monthly partition rotation: add next month's partition, archive expired partitions per retention class |

### 14.3 Partition Rotation (G57)

```
Installer Agent — Monthly Partition Maintenance (runs on 1st of each month):

  1. For each partitioned table (AuditActivity, AuditChangeSet,
     AuditContextSnapshot, RuleEvaluation):

     a. ADD PARTITION for month+3 (always 3 months ahead):
        ALTER TABLE AuditActivity ADD PARTITION (
          PARTITION p202609 VALUES LESS THAN (TO_DAYS('2026-10-01'))
        );

     b. Check retention policy for oldest partition:
        - SHORT_90D: archive partitions older than 90 days
        - STANDARD_3Y: archive partitions older than 3 years
        - EXTENDED_7Y: archive partitions older than 7 years
        - REGULATORY_10Y: archive partitions older than 10 years

     c. Archive expired partition (if no legal hold):
        - Export partition data to backup target:
          SELECT * FROM AuditActivity PARTITION (p202401) INTO OUTFILE ...
        - Verify export integrity
        - DROP PARTITION (only after verified export):
          ALTER TABLE AuditActivity DROP PARTITION p202401;

     d. If legal hold exists on any record in partition:
        - Skip archival
        - Log: "Partition p202401 retained due to legal hold LH-xxx"

  2. Verify partition health:
     SELECT PARTITION_NAME, TABLE_ROWS, DATA_LENGTH
     FROM INFORMATION_SCHEMA.PARTITIONS
     WHERE TABLE_SCHEMA = 'erp_traceability';

  3. If partition creation fails (e.g., disk full):
     - Log ERROR
     - Alert in health dashboard
     - Audit writes continue to current partition (no data loss)
     - Retry on next daily health check
```

### 14.4 Geo-Tag Strategy for Rural India (G58)

```
Geo-tag resolution order (Traceability module):

  1. Device GPS (if available and accurate < 100m)
     → LocationSource = GPS
     → Latitude/Longitude from device
     → GeoAccuracyMeters from device

  2. Cell tower triangulation (if GPS unavailable)
     → LocationSource = CellTower
     → Latitude/Longitude from network
     → GeoAccuracyMeters typically 500m–5km

  3. Site coordinates from .epcfg (fallback)
     → LocationSource = SiteConfig
     → Latitude/Longitude from site_coordinates in .epcfg
     → GeoAccuracyMeters = 0 (exact site location)

  4. Manual entry (operator-provided during install)
     → LocationSource = Manual
     → Latitude/Longitude entered by operator
     → GeoAccuracyMeters = operator-estimated

  5. Unavailable (all sources failed)
     → LocationSource = Unavailable
     → Latitude/Longitude = NULL
     → GeoAccuracyMeters = NULL
     → Business operation PROCEEDS (never blocked for missing geo)

  .epcfg addition:
    "site_coordinates": {
      "latitude": 16.5062,
      "longitude": 80.6480,
      "accuracy_meters": 50,
      "source": "GPS survey during site registration"
    }
```

### 14.5 Anomaly Rule Configuration for Offline PACS (G63)

| Rule | Online Default | Offline PACS Override | Rationale |
|---|---|---|---|
| AN-01: Impossible Travel | Active, 500 km/h | **Disabled** or radius = site boundary (1 km) | Single-site PACS; all logins from same location |
| AN-02: High Reversal | Active, threshold 3σ | Active, threshold 3σ | Relevant for loan reversals; keep as-is |
| AN-03: Fail-Then-Success | Shadow | Shadow | Useful for detecting brute-force; keep as-is |
| AN-04: Maker-Checker | Active, strict | **Active, strict** | Critical for loan approvals; most important rule |
| AN-05: Out-of-Hours | Active, 9am–6pm | **Active, configurable hours from `.epcfg`** | Rural PACS may operate 7am–8pm; configure per site |
| AN-06: Saga Inconsistency | Shadow | Shadow | Relevant for multi-step loan workflows |
| AN-07: Volume Spike | Active, EWMA baseline | **Active, seed after 30-day pilot baseline** | Needs local calibration; EWMA baseline meaningless without history |
| AN-08: Repeated DLT | Shadow | Shadow | Relevant for sync dead-letter monitoring |

Configuration generated from `.epcfg`:
```json
{
  "Traceability": {
    "Anomaly": {
      "Engine": { "Enabled": true },
      "Rules": {
        "AN-01": { "Mode": "Disabled" },
        "AN-04": { "Mode": "Active", "Severity": "Critical" },
        "AN-05": { "Mode": "Active", "BusinessHoursStart": "07:00", "BusinessHoursEnd": "20:00" },
        "AN-07": { "Mode": "Active", "BaselineSeedDays": 30, "SpikeThresholdSigma": 3.0 }
      }
    }
  }
}
```

### 14.6 Traceability Sync to NLDR

Audit events must flow to NLDR alongside business data. The Traceability module has its own `AuditOutbox` table with the same outbox pattern:

```
Sync priority order (when connectivity available):
  1. Financial transactions (from main sync_outbox)
  2. Audit events for financial transactions (from AuditOutbox, EventType contains "Loans" or "FAS")
  3. Anomaly flags (from AuditOutbox, EventType = "FlagLifecycle")
  4. Remaining audit events
  5. Master data changes
  6. Telemetry/health data

Kafka topic mapping:
  Main outbox → epacs.local.sync-ready → Sync Agent → NLDR
  AuditOutbox → traceability.activity-recorded → Sync Agent → NLDR (audit channel)

Both outboxes drained by the same Sync Agent but on separate Kafka topics
to allow independent backpressure and priority.
```

---

## Appendix H: Traceability Integration Checklist

| # | Item | Phase | Owner |
|---|---|---|---|
| H1 | Add `erp_traceability` database creation to installer fresh-install flow | Phase 1 | Installer Eng |
| H2 | Add `erp_trace_app` user creation with least-privilege grants | Phase 1 | DB Lead |
| H3 | Integrate EF Core migration runner for Traceability alongside DbUp for main DB | Phase 2 | Installer Eng |
| H4 | Add Traceability DB to backup package layout (separate dump file) | Phase 2 | Installer Eng |
| H5 | Add Traceability DB to restore workflow | Phase 2 | Installer Eng |
| H6 | Implement partition rotation in Installer Agent (monthly) | Phase 2 | Installer Eng |
| H7 | Add 5 Traceability Kafka topics to pre-creation list | Phase 1 | Installer Eng |
| H8 | Add `/health/traceability` to overall health dashboard | Phase 1 | Service Dev |
| H9 | Add site_coordinates to `.epcfg` schema | Phase 0 | Installer Eng |
| H10 | Configure anomaly rules per-site from `.epcfg` | Phase 1 | Service Dev |
| H11 | Add AuditOutbox drain to Sync Agent (separate Kafka topic) | Phase 3 | Sync/API Lead |
| H12 | Add Traceability schema to schema fingerprint baseline | Phase 2 | DB Lead |
| H13 | Test partition rotation across 12-month simulation | Phase 2 | QA Lead |
| H14 | Test audit write resilience during power-cut (deferred journal fallback) | Phase 2 | QA Lead |
| H15 | Verify geo-tag fallback chain (GPS → CellTower → SiteConfig → Unavailable) | Phase 5 (pilot) | QA Lead |
| H16 | Calibrate AN-07 (Volume Spike) baseline after 30-day pilot | Phase 5 | Security Lead |

---

## Appendix I: STRIDE Threat Model (Detailed)

| ID | Asset | STRIDE | Threat | Likelihood | Impact | Mitigation | Verifying Test |
|---|---|---|---|---|---|---|---|
| TI-01 | Installer EXE | S | Forged installer signed with stolen cert | Low | Catastrophic | EV signing + ATA allow-list (G68) + reproducible build (G67) | Tamper-Negative-01 |
| TI-02 | Release manifest | S | Forged manifest with malicious payloads | Low | High | Manifest signed by Release CA; Authenticode verified before any payload trust | Tamper-Negative-02 |
| TI-03 | `.epcfg` | S | Forged site config | Low | High | Per-site encryption (G92) + signature + nonce | Tamper-Negative-03 |
| TI-04 | Override Token | S | Forged JWT | Low | High | JWS RS256 verification against Release CA pubkey + nonce store | Tamper-Negative-04 |
| TI-05 | Sync packet | S | Forged inbound NLDR command | Low | High | mTLS to NLDR + payload signature + idempotency | Sync.ContractTests |
| TI-06 | ATA allow-list | S | Forged ATA bypassing revocation | Low | Catastrophic | ATA signed by Release CA; embedded fallback allow-list | Tamper-Negative-05 |
| TT-01 | Installed binaries | T | Post-install binary tampering | Medium | High | Drift detection (G22) + Authenticode at startup + ACL | Drift-Test-01 |
| TT-02 | Generated config | T | Manual edit of `appsettings.json` | High | Medium | Drift detection (G22) + repair mode | Drift-Test-02 |
| TT-03 | MySQL datadir | T | Direct DB tampering bypassing app | Medium | Critical | Audit-log hash chain + Traceability + DB user isolation (G69) | Audit-Chain-Test-01 |
| TT-04 | Backup package | T | Modified backup contents | Low | Catastrophic | AES-256-GCM with cert-wrapped key + signed manifest + chain (G87) | Restore-Tamper-Test |
| TT-05 | Schema | T | Manual DDL changes | High | High | Schema fingerprint (G45) + drift report at upgrade | Drift-Test-03 |
| TT-06 | Audit log | T | Hash chain break | Low | Catastrophic | Continuous chain verification + monotonicity check (G70) | Audit-Chain-Test-02 |
| TR-01 | Operator action | R | Operator denies running upgrade | Medium | Medium | Override Token signed + Traceability captures all phases | Audit-Trace-Test |
| TR-02 | Sync ACK | R | NLDR claims ACK never received | Low | Medium | Two-phase ACK + chain (G87) + signed receipts | Sync-Receipt-Test |
| TI-IDS-01 | Support bundle | I | Plaintext secrets exfiltrated | High | High | Redaction + golden list test + secret scanner CI gate | Bundle-Redaction-Test |
| TI-IDS-02 | Backup at rest | I | Backup decrypted by attacker | Low | Catastrophic | AES-256-GCM + cert-wrapped key + escrow | Backup-Encryption-Test |
| TI-IDS-03 | Keys at rest | I | DPAPI key extracted via memory dump | Low | High | Cert-wrapped key + escrow on different host | Key-Extraction-Test |
| TI-IDS-04 | `.epcfg` on operator laptop | I | Laptop theft → `.epcfg` exposure | Medium | Medium | Per-site encryption + nonce TTL + revocation (G92) | epcfg-Replay-Test |
| TI-IDS-05 | Sync payload | I | Sniffing on rural last-mile | Low | High | mTLS 1.3; cert pinning to NLDR | Sync-MITM-Test |
| TI-DOS-01 | Disk | D | Logs/backups fill disk | High | High | Tiered alerts (G24); log rotation (G23); backup eviction (G84) | DiskFull-Chaos-Test |
| TI-DOS-02 | MySQL | D | Health-probe storm | Medium | Medium | Cache + rate-limit (G79) | Probe-Storm-Test |
| TI-DOS-03 | Outbox | D | NLDR outage → outbox flood | High | High | Soft/hard ceilings + overflow table (G66) | Outbox-Overflow-Test |
| TI-DOS-04 | Garnet | D | AOF corruption crash loop | Medium | Medium | Quarantine + 6h snapshot (G91) | Garnet-AOF-Test |
| TI-DOS-05 | Backup | D | Infinite backup loop | Low | Medium | Mutex + rate-limit per type | Backup-Loop-Test |
| TI-EOP-01 | Service principals | E | Compromised service hops to peer | Medium | High | Per-service auth + mTLS (G69) | ServiceAuth-Test |
| TI-EOP-02 | Override Token | E | Replay across PACS | Low | High | Nonce store + pacs_id binding | Token-Replay-Test |
| TI-EOP-03 | Operator account | E | Operator account abused for purge | Medium | Catastrophic | Purge requires Override Token + typed confirmation + audit | Purge-Test |
| TI-EOP-04 | GPO | E | GPO disables ePACS to bypass controls | Medium | High | Health detects service down; alert; GPO compatibility doc (G72) | GPO-Test |

---

## Appendix J: GPO / Domain-Policy Compatibility Matrix

| Policy Area | Setting | Required Value (or compatible range) | Reason |
|---|---|---|---|
| Services | Service start-up types for ePACS* services | Auto/Auto-Delayed; not Disabled | ePACS services must start at boot |
| Services | "Allow logon as a service" | Include `ePACSAppSvc`, `ePACSDbSvc`, `ePACSCacheSvc`, `ePACSEventSvc`, `ePACSSyncSvc` | Custom service principals |
| Windows Defender | Real-time protection exclusions | Include `D:\ePACSData\mysql\data\`, `D:\ePACSData\eventing\`, `D:\ePACSData\cache\`, `C:\Program Files\ePACS\` | Avoid datadir corruption |
| Network | Outbound connections | Allow 443 to NLDR FQDN/IP | Sync requires NLDR access |
| Network | Inbound connections | Allow 443 from PACS LAN; block from internet | Web UI access; localhost-only DB/cache/eventing |
| Windows Update | Automatic reboot during install/upgrade | Disabled while installer runs (G34) | Avoid mid-install reboot |
| Windows Update | Reboot policy | Allow operator-deferred reboot | Operator decides timing |
| BitLocker | Drive encryption | Allowed (recommended) on data volume | Defense in depth |
| User Rights | "Replace process-level token" | Include service principals | Service start-up |
| User Rights | "Adjust memory quotas for a process" | Include service principals | MySQL buffer pool |
| Audit | Object access auditing | Permitted but kept off by default for ePACS data folders | Avoid audit-log explosion |
| Software Restriction | AppLocker rules | Whitelist signed `*.exe` from `C:\Program Files\ePACS\` and signing-cert thumbprint | Permits ePACS to run |
| Power | Power plan | High Performance during install/upgrade | Avoid CPU throttling |
| Power | Disk spin-down | Disabled or > 4h | Avoid disk-stall during long ops |
| Time | NTP server | DCCB hub or w32time pool; required for sync | Clock drift (G16, G70) |
| Locale | System locale | en-US, en-IN, hi-IN, mr-IN, te-IN, ta-IN supported | Tested locales |
| Group Policy refresh | Background refresh | Allowed; ePACS Agent monitors for service-disable events | Detect GPO interference |
| RDP | RDP enabled (DCCB hubs only) | Optional; not required at PACS sites | Remote support |

**Precheck logic** (E-codes E040–E055 reserved for GPO conflicts):
```
Run gpresult /r /scope:computer
Parse for known-conflicting policies
For each conflict:
  E040: AppLocker blocks ePACS — BLOCK install
  E041: Service start-up type Disabled for ePACS service — BLOCK install
  E042: Defender exclusions missing — WARN
  E043: Outbound 443 blocked — WARN (sync will fail)
  E044: System locale unsupported — BLOCK install
```

---

## Appendix K: Operator / Engineering SLA / SLO Matrix

| # | SLO | Target | Measured by | Owner | Cadence |
|---|---|---|---|---|---|
| K1 | Installer crash rate | < 0.5% | crash reports / invocations (telemetry) | Engineering Lead | Monthly |
| K2 | Fresh-install success | > 99% | AC-001 success / attempts | Installer Eng | Monthly |
| K3 | Upgrade success | > 98% | upgrade success / attempts | Installer Eng | Monthly |
| K4 | Rollback effectiveness | 100% | data-loss incidents from failed upgrades | Installer Eng | Per incident |
| K5 | Backup verification | > 99.9% | verified backups / total backups | DB Lead | Daily roll-up |
| K6 | DR drill quarterly | 100% sites in scope | drills completed / sites | DR Lead | Quarterly |
| K7 | Sync recovery | > 99% within 1h | drains within target / reconnections | Sync/API Lead | Monthly |
| K8 | Hotfix turnaround | ≤ 24h | confirmed P1 → media ready | Engineering Lead | Per P1 |
| K9 | Support bundle triage | ≤ 4 business hours | bundle uploaded → response posted | Support Lead | Per bundle |
| K10 | Release readiness gate | 100% checks pass | release-readiness.signed.json | Engineering Lead | Per release |
| K11 | Mutation score | ≥ 60% | Stryker.NET reports | QA Lead | Nightly |
| K12 | Code coverage (unit + integration) | ≥ 80% line, ≥ 70% branch | dotnet-coverage | QA Lead | Per PR |
| K13 | Per-PACS uptime | 99.0% | service-up time / total time | Field SRE | Monthly |
| K14 | Daily backup completion | 100% within 24h | sites with current backup | Field SRE | Daily |
| K15 | Health endpoint P95 latency | < 500ms | k6 measurement | Service Dev | Per release |
| K16 | Sync lag steady state | < 10 min | sync_lag_minutes from fleet manifest | Sync/API Lead | Continuous |
| K17 | Cert expiry warning lead | ≥ 30 days | days warned before expiry | Security Lead | Monthly |
| K18 | Time to acknowledge P1 | ≤ 1 business hour | first response timestamp | Support Lead | Per P1 |
| K19 | Time to resolve P1 | ≤ 24h (with hotfix) or ≤ 7d (with patch) | resolution timestamp | Engineering Lead | Per P1 |
| K20 | Localization parity | 100% strings translated for supported languages | resx coverage | Product Owner | Per release |

---

## Appendix L: Installer Idempotency Invariants

For every installer mode (INSTALL, UPGRADE, REPAIR, BACKUP, RESTORE, UNINSTALL, HOTFIX), running the same operation twice with the same inputs must produce equivalent state.

### L.1 File-System Invariants

| Invariant | Implementation |
|---|---|
| File create is atomic | Write to `<file>.tmp` → fsync → rename (atomic on NTFS) |
| Directory create is idempotent | `Directory.CreateDirectory` (no-op if exists) |
| Junction create is idempotent | Drop + recreate; final target equal |
| File ownership/ACL set | SDDL set absolute (not delta) |
| Symbolic content (configs) | Content hash compared to expected; rewrite only if different |
| Logs append | Always; never truncate as part of install |

### L.2 Service Invariants

| Invariant | Implementation |
|---|---|
| Service registration | `sc.exe create` followed by `sc.exe config` to enforce all attributes; tolerates "already exists" |
| Service start-up type | Always set to expected value (Auto-Delayed for some, Auto for others) |
| Service principal | Always reset to expected; password reset every install |
| Service recovery actions | `sc.exe failure` always re-applied |
| Service description | `sc.exe description` always re-applied |
| Service state | Started after install; left in running state |

### L.3 Database Invariants

| Invariant | Implementation |
|---|---|
| Database create | `CREATE DATABASE IF NOT EXISTS MHCluster3 ...` |
| User create | `CREATE USER IF NOT EXISTS 'epacs_app'@'localhost' ...`; password reset every install |
| Grants | `GRANT ... ON ... TO ...` is idempotent |
| Schema migration | DbUp checkpoint table prevents re-execution |
| Schema fingerprint | Capture at end; compare against expected; mismatch → fail-fast |
| Seed data | All seeds use INSERT IGNORE or UPSERT semantics |

### L.4 Registry Invariants

| Invariant | Implementation |
|---|---|
| `installation_registry` row | Single row keyed by pacs_id; UPSERT pattern |
| `config_hashes` rows | Replace-all per file_path |
| `schema_version_registry` | Append-only; latest = current |
| `release_history` | Append-only |

### L.5 Test: "Run Install Twice and Diff State"

```
1. Snapshot VM
2. Run install with .epcfg-A
3. Snapshot system state (file hashes, service config, registry, DB schema, ACLs)
4. Run install again with same .epcfg-A
5. Snapshot system state again
6. Diff:
   - Allowed differences: log file mtimes, log file content (append), counters
   - Disallowed differences: any file content, ACL, service config, DB schema, registry value
7. Test passes only if all disallowed differences are absent
```

---

## Appendix M: Code-Signing Key Compromise Runbook

### M.1 Detection Triggers

| Signal | Action |
|---|---|
| HSM/Key Vault audit log shows unauthorized access | Trigger M.2 immediately |
| Unsigned-but-functioning installer reported in field | Trigger M.2 immediately |
| Installer signed with current cert produces unexpected behavior | Trigger M.3 first; possibly M.2 |
| Authenticode verification fails on artifact thought to be valid | Trigger M.3 first |
| Anonymous reports of forged installer media | Trigger M.2 immediately |

### M.2 Compromise Response (Severe)

```
T+0      [Security Lead] Convene incident response: SecLead, EngLead, DBLead, CxO delegate
T+30m    [Security Lead] Notify NLPSV Release CA — request immediate cert revocation
T+1h     [Security Lead] Issue signed ATA blacklisting compromised cert thumbprint
         [DevOps]        Push ATA via NLDR sync channel (priority delivery)
T+2h     [Security Lead] Initiate new signing-cert procurement (emergency SLA)
         [Engineering]   Identify all artifacts signed with compromised key
T+4h     [DevOps]        Pause all field installer media distribution
         [Field SRE]     Notify state federations: do not install pending media
T+8h     [Engineering]   Forensic analysis: which builds may have been forged?
         [Field SRE]     Inventory: which PACS may have installed forged artifacts?
T+24h    [Security Lead] New cert in HSM; new signing pipeline test
T+48h    [Engineering]   Re-sign all current artifacts with new cert
         [DevOps]        Mint new release media with new signatures + updated ATA
T+72h    [Field SRE]     Distribute new media to fleet; mandatory re-sign verification
T+1week  [Engineering]   Audit all PACS sync manifests for evidence of forged install
         [Security Lead] If forged install confirmed at any PACS:
                         - Quarantine PACS from sync
                         - Forensic image of disk
                         - Restore from last verified-good backup
                         - Re-install with new signed media
T+2weeks [Security Lead] Post-incident review: root cause, gaps, improvements
T+1month [Security Lead] Lessons-learned document published; runbook updated
```

### M.3 Soft Detection (Possible Issue)

```
T+0      [Engineering]   Capture all evidence (log files, hashes, suspect artifact)
T+1h     [Security Lead] Reproduce on isolated VM
T+4h     [Security Lead] Determine: real compromise vs. false alarm
         If real      → Escalate to M.2
         If false     → Document; close ticket
```

### M.4 Preventive Measures (continuous)

```
Quarterly:
  - Rotate signing-pipeline access (review who has approve rights)
  - Audit HSM/Key Vault access log
  - Verify reproducible-build for last release
  - Two-person rule audit

Annually:
  - Full key rotation (planned, non-emergency)
  - Tabletop exercise of M.2

Continuously:
  - Monitor for unsigned-but-functioning installer reports
  - Watch for anomalous Authenticode fail rates in fleet manifest
```

---

## Appendix N: Expanded Test Matrix (per Section 20)

### N.1 Functional × Operating-Condition Matrix

```
                        | Win10 | WinSvr2019 | WinSvr2022 | NTFS | ReFS | Domain | Standalone | Hyper-V | Bare | en-US | en-IN | hi-IN | mr-IN |
fresh-install           |   ✓   |     ✓      |     ✓      |  ✓   |  ✓   |   ✓    |     ✓      |   ✓     |  ✓   |   ✓   |   ✓   |   ✓   |   ✓   |
upgrade-N-1-to-N        |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  ✓   |   ✓   |   ✓   |   ✓   |   -   |
upgrade-N-2-to-N (skip) |   -   |     ✓      |     ✓      |  ✓   |  -   |   -    |     ✓      |   ✓     |  -   |   ✓   |   -   |   -   |   -   |
repair                  |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   ✓   |   -   |
uninstall               |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   -   |   -   |
backup                  |   ✓   |     ✓      |     ✓      |  ✓   |  ✓   |   ✓    |     ✓      |   ✓     |  ✓   |   ✓   |   ✓   |   ✓   |   ✓   |
restore-same            |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  ✓   |   ✓   |   ✓   |   ✓   |   -   |
restore-new             |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   -   |   -   |
restore-cross-profile   |   -   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   -   |   -   |   -   |
hotfix-apply            |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   -   |   -   |
sync-online             |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   ✓   |   ✓   |
sync-offline-30d        |   -   |     ✓      |     -      |  ✓   |  -   |   -    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   -   |   -   |
sync-reconnect          |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   -   |   -   |
conflict-resolution     |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   ✓   |   -   |
drift-detect-and-repair |   ✓   |     ✓      |     ✓      |  ✓   |  -   |   ✓    |     ✓      |   ✓     |  -   |   ✓   |   ✓   |   -   |   -   |
```

(✓ = required cell; - = covered by sibling cells via factor analysis)

### N.2 Chaos Test Matrix

```
                        | small-PACS | DCCB-hub | low-power | thermal | high-IO | low-RAM |
power-cut-mid-install   |     ✓      |    ✓     |     ✓     |   -     |   -     |   ✓     |
power-cut-mid-upgrade   |     ✓      |    ✓     |     ✓     |   -     |   ✓     |   ✓     |
power-cut-mid-backup    |     ✓      |    ✓     |     -     |   -     |   ✓     |   -     |
disk-full-during-mig    |     ✓      |    ✓     |     -     |   -     |   ✓     |   -     |
disk-full-during-bkp    |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
av-quarantine-binary    |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
clock-skew-+6h          |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
clock-skew--6h          |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
network-partition       |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
service-crash-mysql     |     ✓      |    ✓     |     -     |   -     |   -     |   ✓     |
service-crash-kafka     |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
service-crash-garnet    |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
oom-during-large-bkp    |     ✓      |    -     |     -     |   -     |   -     |   ✓     |
fs-fill-attachments     |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
concurrent-installer    |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
gpo-disable-service     |     -      |    ✓     |     -     |   -     |   -     |   -     |
thermal-throttle-85C    |     ✓      |    -     |     -     |   ✓     |   -     |   -     |
usb-corruption          |     ✓      |    -     |     -     |   -     |   -     |   -     |
xtrabackup-target-fs    |     ✓      |    ✓     |     -     |   -     |   ✓     |   -     |
garnet-aof-corrupt      |     ✓      |    ✓     |     -     |   -     |   -     |   -     |
```

### N.3 Tamper-Negative Suite

```
TN-01  Unsigned installer EXE                           → BLOCKED (E-coded)
TN-02  Modified payload (one byte flip in mysql.zip)    → BLOCKED
TN-03  Authenticode-signed by wrong CA                  → BLOCKED
TN-04  Authenticode-signed but thumbprint not in ATA    → BLOCKED (G68)
TN-05  Manifest signature mismatch                      → BLOCKED
TN-06  `.epcfg` signature mismatch                      → BLOCKED
TN-07  `.epcfg` nonce replay (same nonce twice)         → BLOCKED (G92)
TN-08  Override Token expired                           → BLOCKED
TN-09  Override Token replay (same nonce)               → BLOCKED
TN-10  SBOM mismatch (file hash differs from SBOM)      → BLOCKED (G86)
TN-11  Backup signature broken                          → restore BLOCKED
TN-12  Backup chain link broken                         → restore WARN + require operator override
TN-13  Audit-chain hash break                           → flagged in health; sync to NLDR for forensics
TN-14  Schema fingerprint breaking-drift before upgrade → upgrade BLOCKED
TN-15  Reproducible-build mismatch                      → release BLOCKED (gate)
```

### N.4 Performance Targets (extended)

| Operation | Small PACS (8GB) | DCCB Hub (32GB) |
|---|---|---|
| Fresh install | < 15 min | < 12 min |
| Patch upgrade (binaries only) | < 10 min | < 8 min |
| Minor upgrade + 10 GB DB | < 30 min | < 20 min |
| Major upgrade + 10 GB DB | < 45 min | < 30 min |
| Major upgrade + 100 GB DB | < 4 hours | < 2 hours |
| Backup 10 GB DB | < 10 min | < 5 min |
| Backup 100 GB DB | < 90 min | < 30 min |
| XtraBackup incr (after 5 GB delta) | < 5 min | < 2 min |
| Restore 10 GB DB | < 15 min | < 10 min |
| Restore 100 GB DB | < 4 hours | < 1.5 hours |
| Hotfix apply | < 5 min | < 5 min |
| Sync drain (10K events backlog) | ≥ 1000/min | ≥ 3000/min |
| Health probe latency P95 | < 500 ms | < 250 ms |
| Diagnostic ping (G80) | < 30 sec | < 15 sec |
| Support bundle generation | < 2 min | < 90 sec |

---

## 21. Observability & Error Handling Integration (`Intellect.Erp.Observability` + `Intellect.Erp.ErrorHandling`)

The ePACS platform has a standardized observability and error handling framework consisting of 10 NuGet packages. This section specifies how the installer, Installer Agent, Sync Agent, and all ePACS services integrate with this framework to produce consistent, correlated, redacted, and catalog-driven structured logs and error responses.

### 21.1 Package Inventory

| Package | Role in Installer Context | Used By |
|---|---|---|
| `Intellect.Erp.Observability.Abstractions` | Contracts: `IAppLogger<T>`, `IErrorFactory`, `IRedactionEngine`, `IAuditHook`, attributes | All services + Installer Agent |
| `Intellect.Erp.ErrorHandling` | Typed exception hierarchy (`AppException`), YAML error catalog loader, `IErrorFactory` impl | All services + Installer Agent |
| `Intellect.Erp.Observability.Core` | `AppLogger<T>` (Serilog-backed), enrichers, redaction engine, DI extensions | All services + Installer Agent |
| `Intellect.Erp.Observability.AspNetCore` | Middlewares: Correlation, GlobalException, ContextEnrichment, RequestLogging | ePACSWeb, business services |
| `Intellect.Erp.Observability.Propagation` | `CorrelationDelegatingHandler` (HTTP), `KafkaHeaders`, `TraceableBackgroundService` | Sync Agent, Outbox Relay, Installer Agent |
| `Intellect.Erp.Observability.AuditHooks` | `LogOnlyAuditHook`, `TraceabilityBridgeAuditHook`, `KafkaAuditHook` | All services (bridges to Traceability module) |
| `Intellect.Erp.Observability.Log4NetBridge` | `SerilogForwardingAppender` for legacy log4net modules | Legacy services during migration |
| `Intellect.Erp.Observability.Integrations.Traceability` | Adapter shim connecting Observability → Traceability | All services |
| `Intellect.Erp.Observability.Integrations.Messaging` | Kafka envelope enricher | Outbox Relay, Sync Agent |
| `Intellect.Erp.Observability.Testing` | `FakeTraceSink`, `InMemoryTraceabilityDb`, assertion helpers | Unit/integration tests |

### 21.2 Structured Log Schema (Canonical Fields)

All ePACS services (including Installer Agent) emit structured JSON logs conforming to the ELK Field Reference schema v1:

```json
{
  "@timestamp": "2026-05-04T10:15:30.123Z",
  "level": "Information",
  "messageTemplate": "Upgrade {UpgradeId} migration {ScriptName} completed in {DurationMs}ms",
  "message": "Upgrade UPG-001 migration V025__add_updated_at.sql completed in 1250ms",
  "log.schema": "v1",
  "app": "epacs-installer-agent",
  "env": "Production",
  "machine": "PACS-AP-XYZ-0001",
  "module": "Installer",
  "correlationId": "01J2X5PQ8WZ4A5C2M8YT4F3M6V",
  "tenantId": "AP-XYZ",
  "stateCode": "AP",
  "pacsId": "AP-XYZ-0001",
  "feature": "SchemaUpgrade",
  "operation": "Migrate",
  "checkpoint": "MigrationScriptCompleted",
  "UpgradeId": "UPG-001",
  "ScriptName": "V025__add_updated_at.sql",
  "DurationMs": 1250
}
```

**Key design decisions for offline PACS**:
- **No ELK dependency at PACS level** — logs write to local rolling JSON files under `D:\ePACSData\logs\<service>\`.
- **ELK is optional at DCCB/state hub level** — if Elasticsearch is available, Serilog's Elasticsearch sink can be enabled via `appsettings.json`.
- **Support bundle extracts** correlated log entries by `correlationId` for remote troubleshooting.
- **Redaction engine** ensures no PII (Aadhaar, mobile, account numbers) appears in log files — critical for support bundle safety.

### 21.3 Error Catalog for Installer

The installer and Installer Agent use a dedicated YAML error catalog (`config/error-catalog/installer.yaml`) following the same structure as the core catalog:

```yaml
errors:
  # Precheck errors (E001-E099 mapped to catalog codes)
  - code: "ERP-INST-PRE-0001"
    title: "Unsupported Windows version"
    userMessage: "Windows 10 version 1809 or later is required."
    supportMessage: "OS version check failed. Detected: {detected_version}."
    httpStatus: null  # Not HTTP — installer context
    severity: "Error"
    retryable: false
    category: "Validation"

  - code: "ERP-INST-PRE-0004"
    title: "Insufficient disk space"
    userMessage: "Data volume needs at least 100 GB free space."
    supportMessage: "Disk check failed. Volume: {volume}, Free: {free_gb} GB, Required: 100 GB."
    severity: "Error"
    retryable: false
    category: "Validation"

  - code: "ERP-INST-PRE-0019"
    title: "Installation media corrupted"
    userMessage: "Installation media may be damaged. Please request a new copy from your district office."
    supportMessage: "SHA-256 mismatch. Expected: {expected_hash}, Got: {actual_hash}."
    severity: "Critical"
    retryable: true
    category: "DataIntegrity"

  # Install errors (E100-E199)
  - code: "ERP-INST-INS-0001"
    title: "Service registration failed"
    userMessage: "Could not register the {service_name} service. Please contact support."
    supportMessage: "sc.exe create failed for {service_name}. Exit code: {exit_code}."
    severity: "Error"
    retryable: true
    category: "System"

  # Migration errors (E200-E299)
  - code: "ERP-INST-MIG-0001"
    title: "Schema migration failed"
    userMessage: "Database upgrade could not complete. Your data is safe — the previous version is still active."
    supportMessage: "DbUp script {script_name} failed at checkpoint {checkpoint}. Error: {error_detail}."
    severity: "Critical"
    retryable: false
    category: "DataIntegrity"

  - code: "ERP-INST-MIG-0002"
    title: "Schema drift detected"
    userMessage: "The database structure has been modified outside the installer. Please contact support before upgrading."
    supportMessage: "Schema fingerprint mismatch. Drift report: {drift_report_path}."
    severity: "Critical"
    retryable: false
    category: "DataIntegrity"

  # Backup/Restore errors (E300-E399)
  - code: "ERP-INST-BAK-0001"
    title: "Backup target unavailable"
    userMessage: "Cannot write backup to the configured location. Please check the backup drive."
    supportMessage: "Backup target {target_path} is not writable. Error: {error_detail}."
    severity: "Error"
    retryable: true
    category: "Dependency"

  # Sync errors (E400-E499)
  - code: "ERP-INST-SYN-0001"
    title: "NLDR connectivity lost"
    userMessage: "Connection to central server is unavailable. Local operations continue normally."
    supportMessage: "NLDR endpoint {endpoint} unreachable. Circuit breaker OPEN. Last success: {last_success}."
    severity: "Warning"
    retryable: true
    category: "Integration"

  # Health errors (E500-E599)
  - code: "ERP-INST-HLT-0001"
    title: "Service health check failed"
    userMessage: "The {service_name} service is not responding. Automatic restart in progress."
    supportMessage: "Health check failed for {service_name}. Consecutive failures: {failure_count}."
    severity: "Error"
    retryable: true
    category: "Dependency"
```

### 21.4 Typed Exception Hierarchy for Installer

The installer uses the same `AppException` hierarchy as business services:

```
AppException (abstract)
├── ValidationException        → Precheck failures (E001-E099)
├── DataIntegrityException     → Schema drift, migration failure, backup corruption
├── DependencyException        → MySQL/Garnet/Kafka service unavailable
├── IntegrationException       → NLDR sync failures (retryable=true)
├── ConflictException          → Concurrent installer execution, lock contention
├── SystemException            → Unclassified installer failures
└── ExternalSystemException    → AV interference, Windows Update conflicts
```

**Error factory usage in Installer Agent**:
```csharp
public class InstallerHealthMonitor : TraceableBackgroundService
{
    private readonly IErrorFactory _errorFactory;
    private readonly IAppLogger<InstallerHealthMonitor> _logger;

    protected override async Task ExecuteTracedAsync(CancellationToken ct)
    {
        var result = await CheckServiceHealth("ePACSMySQL");
        if (!result.Healthy)
        {
            _logger.Warning("Service {ServiceName} health check failed. Attempt {Attempt}/5",
                "ePACSMySQL", result.ConsecutiveFailures);

            if (result.ConsecutiveFailures >= 5)
            {
                // Throws typed exception → caught by TraceableBackgroundService
                // → logged with correlation ID → audit hook fires → support bundle generated
                throw _errorFactory.FromCatalog("ERP-INST-HLT-0001",
                    $"MySQL service unresponsive after {result.ConsecutiveFailures} attempts");
            }
        }
    }
}
```

### 21.5 Correlation Propagation Across Installer Operations

Every installer operation (install, upgrade, backup, restore, sync) gets a unique correlation ID that flows through all log entries, audit events, and error responses:

```
Installer operation starts:
  → CorrelationId = ULID.NewUlid().ToString()  // e.g., "01J2X5PQ8WZ4A5C2M8YT4F3M6V"
  → Pushed to Serilog LogContext via IAppLogger.BeginScope
  → All log entries within this operation carry the same correlationId
  → If operation triggers Kafka messages → correlationId in Kafka headers (KafkaHeaders helper)
  → If operation calls NLDR → correlationId in HTTP header (CorrelationDelegatingHandler)
  → If operation fails → correlationId in ErrorResponse + support bundle filename
  → Support bundle: epacs-support-01J2X5PQ8WZ4A5C2M8YT4F3M6V.zip
```

**Cross-service correlation** (e.g., upgrade triggers migration → triggers health check → triggers audit):
```
CorrelationId: 01J2X5PQ8WZ4A5C2M8YT4F3M6V (upgrade operation)
  ├── CausationId: 01J2X5PQ8WZ4A5C2M8YT4F3M6V (self — root cause)
  ├── Log: "Upgrade started for version 3.3.0"
  ├── Log: "Pre-upgrade backup initiated"
  ├── Log: "Backup completed: backup-id=BAK-20260504"
  ├── Log: "Migration V025 started"
  ├── Log: "Checkpoint: MigrationScriptCompleted (V025, 1250ms)"
  ├── Log: "Migration V026 started"
  ├── Log: "Health check: all services OK"
  ├── AuditEvent: {module=Installer, operation=Upgrade, outcome=Succeeded}
  └── Log: "Upgrade completed successfully"
```

### 21.6 PII Redaction in Logs and Support Bundles

The `IRedactionEngine` ensures sensitive data never appears in logs or support bundles:

| Data Type | Attribute | Redacted Output | Example |
|---|---|---|---|
| Aadhaar number | `[Sensitive(keepLast: 4)]` | `****-****-1234` | Member registration |
| Mobile number | `[Sensitive(keepLast: 4)]` | `******5678` | Customer details |
| Account number | `[Mask(@"\d{6,}")]` | `***456789` | Loan disbursement |
| Email | `[Sensitive(mode: SensitivityMode.EmailMask)]` | `j***@***.com` | User profile |
| Password/secret | `[DoNotLog]` | (field omitted entirely) | Service credentials |
| SQL query with data | Regex pattern | `SELECT ... WHERE id=***` | Correction tool logs |

**Support bundle redaction pipeline**:
```
1. Collect raw logs from D:\ePACSData\logs\
2. For each log file:
   a. Parse JSON lines
   b. Apply IRedactionEngine.RedactJson() to each entry
   c. Remove any field matching [DoNotLog] patterns
   d. Write redacted version to support bundle
3. Collect config files:
   a. Apply IRedactionEngine.RedactObject() to appsettings
   b. Mask connection strings, passwords, cert thumbprints
4. Package as encrypted ZIP
```

### 21.7 Audit Hook Configuration for Offline PACS

The `IAuditHook` bridges logging events to the Traceability module. Configuration per deployment mode:

| Mode | When | Behavior |
|---|---|---|
| `LogOnly` | Development, testing | Audit events written as structured log entries only |
| `TraceabilityBridge` | **Production (default for PACS)** | Audit events written to `erp_traceability` DB via `ITraceSink` |
| `Kafka` | DCCB hubs with Kafka available | Audit events published to Kafka topic for downstream consumers |

**Installer-generated configuration** (in `appsettings.json`):
```json
{
  "Observability": {
    "ApplicationName": "epacs-loans",
    "ModuleName": "Loans",
    "Environment": "Production",
    "Sinks": {
      "Console": { "Enabled": false },
      "File": {
        "Enabled": true,
        "Path": "D:\\ePACSData\\logs\\loans\\loans-.json",
        "RollingInterval": "Day",
        "RetainedFileCountLimit": 30,
        "FileSizeLimitBytes": 104857600
      },
      "Elasticsearch": { "Enabled": false }
    },
    "Masking": { "Enabled": true },
    "AuditHook": {
      "Mode": "TraceabilityBridge"
    }
  },
  "ErrorHandling": {
    "ReturnProblemDetails": true,
    "CatalogFiles": [
      "config/error-catalog/core.yaml",
      "config/error-catalog/loans.yaml",
      "config/error-catalog/installer.yaml"
    ],
    "ClientErrorUriBase": "https://errors.epacs.in/",
    "SuppressStackTraceInProduction": true
  }
}
```

### 21.8 Installer Agent as TraceableBackgroundService

The Installer Agent and Sync Agent both extend `TraceableBackgroundService` from the Propagation package:

```csharp
public class InstallerAgentService : TraceableBackgroundService
{
    protected override string ModuleName => "InstallerAgent";
    protected override string OperationName => "HealthMonitoring";

    protected override async Task ExecuteTracedAsync(CancellationToken ct)
    {
        // Correlation ID auto-generated per execution cycle
        // Module/Operation auto-pushed to LogContext
        // Exceptions auto-caught, logged with correlation, and service continues

        while (!ct.IsCancellationRequested)
        {
            await RunHealthChecks(ct);
            await RunDiskMonitoring(ct);
            await RunDriftDetection(ct);
            await RunLogRotation(ct);
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }
}
```

### 21.9 Log4Net Bridge for Legacy Services

If any ePACS services still use log4net (legacy modules), the `SerilogForwardingAppender` captures their output and routes it through the structured logging pipeline:

```xml
<!-- log4net.config for legacy service -->
<appender name="SerilogForwarder"
          type="Intellect.Erp.Observability.Log4NetBridge.SerilogForwardingAppender, Intellect.Erp.Observability.Log4NetBridge">
</appender>
<root>
  <appender-ref ref="SerilogForwarder" />
</root>
```

This ensures all services — new and legacy — produce consistent structured JSON logs with the same canonical field set, enabling unified support bundle analysis and correlation tracing.

### 21.10 Installer Integration Checklist

| # | Item | Phase | Owner |
|---|---|---|---|
| O1 | Add `Intellect.Erp.Observability.Core` + `ErrorHandling` to Installer.Agent project | Phase 1 | Installer Eng |
| O2 | Add `Intellect.Erp.Observability.Propagation` to Installer.Agent + Sync.Agent | Phase 1 | Installer Eng |
| O3 | Create `config/error-catalog/installer.yaml` with all E001–E599 codes | Phase 1 | Installer Eng |
| O4 | Implement Installer Agent as `TraceableBackgroundService` | Phase 1 | Installer Eng |
| O5 | Configure Serilog file sink in installer-generated `appsettings.json` | Phase 1 | Installer Eng |
| O6 | Integrate `IRedactionEngine` into support bundle collector | Phase 1 | Installer Eng |
| O7 | Add `Intellect.Erp.Observability.AspNetCore` to ePACSWeb | Phase 1 | Service Dev |
| O8 | Configure `AuditHook.Mode = TraceabilityBridge` for all production services | Phase 1 | Service Dev |
| O9 | Add `Intellect.Erp.Observability.Log4NetBridge` to any legacy services | Phase 1 | Service Dev |
| O10 | Add `Intellect.Erp.Observability.Integrations.Messaging` to Outbox.Relay | Phase 3 | Sync/API Lead |
| O11 | Verify correlation propagation across: HTTP → Kafka → Sync Agent → NLDR | Phase 3 | QA Lead |
| O12 | Verify PII redaction in support bundles against golden list | Phase 4 | Security Lead |
| O13 | Add `Intellect.Erp.Observability.Testing` to all test projects | Phase 1 | QA Lead |

---

## 22. File/Attachment Sync and PACS Heartbeat Architecture

This section addresses two critical capabilities missed in earlier revisions:
1. Bidirectional file/attachment synchronization (member photos, field verification uploads, generated reports)
2. PACS online/offline heartbeat to CoopsIndia Dashboard

### 22.1 File Attachment Profile

| Type | Naming Convention | Avg Size | Volume/PACS/Day | Direction |
|---|---|---|---|---|
| Member photos | `{state_id}/{dccb_id}/{branch_id}/{pacs_id}/photo.jpg` | 100–500 KB | ~2 MB total | PACS → NLDR |
| Field verification uploads | `{state_id}/{dccb_id}/{branch_id}/{pacs_id}/{doc_type}/{filename}` | 200 KB–2 MB | Included in 2 MB | PACS → NLDR |
| Generated reports (PDF) | `{state_id}/{dccb_id}/{branch_id}/{pacs_id}/reports/{report_type}/{date}.pdf` | 1–10 MB | On-demand | PACS → NLDR |
| Central policy documents | `{state_id}/policies/{filename}` | 1–5 MB | Rare | NLDR → PACS |
| Master data updates | `{state_id}/{dccb_id}/master/{filename}` | Variable | Rare | NLDR → PACS |

**Storage**: `D:\ePACSData\attachments\` with the hierarchical naming convention.

### 22.2 Differential File Sync Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  FILE SYNC — CONTENT-HASH BASED DEDUPLICATION                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  PACS Node                              NLDR / Central           │
│  ┌──────────────┐                       ┌──────────────┐        │
│  │ attachments/ │                       │ PACS mount   │        │
│  │  AP/         │    ──── SYNC ────►    │  AP/XYZ/0001/│        │
│  │   XYZ/       │    ◄─── SYNC ────    │              │        │
│  │    0001/     │                       │              │        │
│  └──────┬───────┘                       └──────────────┘        │
│         │                                                        │
│  ┌──────▼───────┐                                                │
│  │ file_sync_   │  Tracks: file_path, content_sha256,            │
│  │ registry     │  size_bytes, last_modified, sync_status,       │
│  │ (MySQL)      │  sync_direction, last_synced_at                │
│  └──────────────┘                                                │
│                                                                  │
│  Sync Algorithm:                                                 │
│  1. Scan attachments/ for new/modified files                     │
│  2. Compute SHA-256 of each file                                 │
│  3. Compare against file_sync_registry                           │
│  4. If hash differs or file is new → mark for upload             │
│  5. Upload via configured transport (SFTP or HTTPS multipart)    │
│  6. On ACK → update registry with synced status                  │
│  7. Inbound: poll NLDR for files targeting this PACS             │
│  8. Download, verify hash, place in correct path                 │
│                                                                  │
│  Deduplication:                                                  │
│  - Same content (same SHA-256) uploaded twice = sync once         │
│  - Registry stores content_hash as dedup key                     │
│  - If file renamed but content unchanged → no re-upload          │
│                                                                  │
│  Enable/Disable:                                                 │
│  - Feature flag in appsettings: FileSync.Enabled = true/false    │
│  - Controllable via ePACS application UI                         │
│  - When disabled: files accumulate locally, registry tracks them  │
│  - When enabled: backlog drains automatically                    │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 22.3 Transport Options (Configurable)

| Transport | When to Use | Configuration |
|---|---|---|
| **SFTP** | Stable connectivity, large files, resume support | Host, port, username, key/password, remote path |
| **HTTPS Multipart** | Firewall-friendly, API-based, chunked upload | Endpoint URL, auth token, chunk size |

Both transports are behind an `IFileSyncTransport` interface. The active transport is selected via configuration. Fallback: if primary transport fails, try secondary (if configured).

```json
{
  "FileSync": {
    "Enabled": false,
    "ScanIntervalSeconds": 300,
    "MaxUploadBatchSizeMb": 50,
    "Transport": {
      "Primary": "HTTPS",
      "Fallback": "SFTP",
      "Https": {
        "Endpoint": "https://nldr.epacs.gov.in/api/v1.0/files",
        "ChunkSizeBytes": 1048576,
        "TimeoutSeconds": 120
      },
      "Sftp": {
        "Host": "sftp.nldr.epacs.gov.in",
        "Port": 22,
        "Username": "${epcfg:pacs_id}",
        "KeyPath": "${DataRoot}\\keys\\sftp_key",
        "RemotePath": "/${epcfg:state_code}/${epcfg:district_code}/${epcfg:pacs_id}"
      }
    },
    "Deduplication": {
      "Algorithm": "SHA-256",
      "SkipIfHashUnchanged": true
    },
    "Inbound": {
      "Enabled": false,
      "PollIntervalSeconds": 600
    }
  }
}
```

### 22.4 File Sync Registry (MySQL Table)

```sql
CREATE TABLE file_sync_registry (
  id BIGINT AUTO_INCREMENT PRIMARY KEY,
  file_path VARCHAR(500) NOT NULL,
  content_sha256 CHAR(64) NOT NULL,
  size_bytes BIGINT NOT NULL,
  last_modified DATETIME NOT NULL,
  sync_direction ENUM('OUTBOUND', 'INBOUND') NOT NULL,
  sync_status ENUM('PENDING', 'IN_PROGRESS', 'SYNCED', 'FAILED', 'SKIPPED') NOT NULL DEFAULT 'PENDING',
  last_synced_at DATETIME NULL,
  attempt_count INT NOT NULL DEFAULT 0,
  last_error TEXT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  INDEX idx_status (sync_status),
  INDEX idx_direction_status (sync_direction, sync_status),
  INDEX idx_content_hash (content_sha256),
  UNIQUE INDEX idx_file_path (file_path)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
```

### 22.5 PACS Heartbeat to CoopsIndia Dashboard

When a PACS node comes online (connectivity detected), it proactively sends a heartbeat to the CoopsIndia Dashboard so that central operations know which PACS nodes are active and their sync status.

```
┌─────────────────────────────────────────────────────────────────┐
│  PACS HEARTBEAT ARCHITECTURE                                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  PACS Node                              CoopsIndia Dashboard     │
│  ┌──────────────┐                       ┌──────────────────┐    │
│  │ Installer    │  ── HTTPS POST ──►    │ /api/v1.0/       │    │
│  │ Agent        │     (every 5 min)     │   pacsStatus     │    │
│  │              │                       │                  │    │
│  │ OR           │  ── WebSocket ───►    │ /ws/v1.0/        │    │
│  │              │     (persistent)      │   pacsStatus     │    │
│  └──────────────┘                       └──────────────────┘    │
│                                                                  │
│  Heartbeat Payload:                                              │
│  {                                                               │
│    "pacs_id": "AP-XYZ-0001",                                     │
│    "state_id": "AP",                                             │
│    "dccb_id": "XYZ",                                             │
│    "branch_id": "001",                                           │
│    "online_since": "2026-05-04T10:00:00Z",                       │
│    "last_sync_timestamp": "2026-05-04T09:45:00Z",                │
│    "pending_outbox_count": 42,                                   │
│    "pending_files_count": 7,                                     │
│    "disk_usage_percent": 35,                                     │
│    "stack_version": "3.2.1",                                     │
│    "schema_version": 25,                                         │
│    "last_backup_at": "2026-05-04T02:00:00Z",                     │
│    "health_status": "Healthy",                                   │
│    "connectivity_mode": "4G",                                    │
│    "uptime_seconds": 3600                                        │
│  }                                                               │
│                                                                  │
│  Protocol Selection (configurable):                              │
│  - HTTPS POST: simple, stateless, firewall-friendly              │
│  - WebSocket: real-time, persistent, lower latency               │
│  - Both supported; selected via config                           │
│                                                                  │
│  Behavior:                                                       │
│  - On connectivity detected → send immediate heartbeat           │
│  - While online → send every N seconds (configurable, default 300)│
│  - On connectivity lost → stop sending (dashboard marks offline   │
│    after 2× heartbeat interval with no signal)                   │
│  - Heartbeat failure → retry with backoff, don't block operations│
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 22.6 Heartbeat Configuration

```json
{
  "Heartbeat": {
    "Enabled": true,
    "IntervalSeconds": 300,
    "Transport": "HTTPS",
    "Https": {
      "Endpoint": "https://dashboard.coopsindia.gov.in/api/v1.0/pacsStatus",
      "TimeoutSeconds": 10,
      "RetryCount": 3
    },
    "WebSocket": {
      "Endpoint": "wss://dashboard.coopsindia.gov.in/ws/v1.0/pacsStatus",
      "ReconnectIntervalSeconds": 30
    },
    "Payload": {
      "IncludeDiskUsage": true,
      "IncludeSyncStatus": true,
      "IncludeHealthStatus": true,
      "IncludeFilesSyncStatus": true
    }
  }
}
```

### 22.7 New Gaps (G93–G96)

| # | Gap / Risk | Severity | Mitigation |
|---|---|---|---|
| G93 | File sync can overwhelm 4G bandwidth if large backlog accumulates | High | Configurable `MaxUploadBatchSizeMb` per sync cycle; priority: small files first (photos), then reports; bandwidth detection adjusts chunk size |
| G94 | SFTP key management for hundreds of PACS nodes | Medium | Key pair generated during install; public key registered with NLDR via sync channel; key rotation via signed config update |
| G95 | Heartbeat endpoint unavailable should not affect PACS operations | High | Heartbeat is fire-and-forget; failure logged but never blocks business operations; circuit breaker after 5 failures |
| G96 | File content hash computation on large report PDFs is CPU-intensive | Medium | Hash computation runs on background thread with low priority; max 1 file hashed at a time; skip files > configurable threshold during peak hours |

---

*End of enhanced plan v3.3 — File Sync + Heartbeat. Ready for implementation.*


---

## 23. Deletion, Amendment, and Financial Data Sync Architecture

This section addresses the critical gap of hard deletes, in-place amendments, and their impact on NLDR synchronization. Based on analysis of `docs/deletionsenerio.md`.

### 23.1 Current State (Problems)

| Issue | Current Behavior | Risk |
|---|---|---|
| Voucher deletion | Hard DELETE from `fa_vouchermaintemp` after logging to `fa_voucherdeletionmain` | Deleted data cannot be synced retroactively; NLDR may have stale records |
| Amendments | Direct UPDATE on existing rows (no versioning) | NLDR has old version; no way to detect what changed |
| Approval workflow | None (changes applied directly) | No governance trail for financial corrections |
| Bulk deletion | Possible via Correction Tool | Irrecoverable without backup; NLDR inconsistent |
| Backdated corrections | Auditor can modify already-synced data | NLDR has stale financial data |

### 23.2 Solution Architecture: Sync-Aware Delete and Amendment

```
┌─────────────────────────────────────────────────────────────────┐
│  DELETION & AMENDMENT SYNC STRATEGY                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Principle: "Nothing is ever truly deleted from the sync         │
│  perspective. Deletions and amendments are EVENTS that must      │
│  be propagated to NLDR."                                         │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │  Business Transaction (DELETE or UPDATE)                  │    │
│  │                                                           │    │
│  │  BEGIN TRANSACTION                                        │    │
│  │    -- Original business logic (hard delete or update)     │    │
│  │    DELETE FROM fa_vouchermaintemp WHERE ...                │    │
│  │    -- OR: UPDATE ln_sanctions SET Amount=... WHERE ...     │    │
│  │                                                           │    │
│  │    -- NEW: Write sync event to outbox                     │    │
│  │    INSERT INTO sync_outbox (                              │    │
│  │      event_type, entity_type, entity_id,                  │    │
│  │      change_type, payload_json, payload_hash              │    │
│  │    ) VALUES (                                             │    │
│  │      'DATA_CHANGE', 'fa_voucher', <voucher_id>,           │    │
│  │      'DELETE',  -- or 'UPDATE' or 'AMENDMENT'             │    │
│  │      <before_state_json>,                                 │    │
│  │      SHA2(<before_state_json>, 256)                       │    │
│  │    )                                                      │    │
│  │  COMMIT                                                   │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
│  Change Types in sync_outbox:                                    │
│    INSERT    → new record created                                │
│    UPDATE    → record modified (payload = before + after state)  │
│    DELETE    → record removed (payload = before state)           │
│    AMENDMENT → auditor correction (payload = before + after +    │
│                reason + approver)                                 │
│                                                                  │
│  NLDR receives the event and:                                    │
│    INSERT → creates record                                       │
│    UPDATE → updates record                                       │
│    DELETE → marks record as deleted (soft-delete on NLDR side)   │
│    AMENDMENT → updates record + logs amendment trail              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 23.3 Sync Outbox Schema Enhancement

```sql
-- Enhanced sync_outbox to support deletion and amendment events
ALTER TABLE sync_outbox ADD COLUMN change_type ENUM('INSERT', 'UPDATE', 'DELETE', 'AMENDMENT') 
  NOT NULL DEFAULT 'INSERT' AFTER event_type;

ALTER TABLE sync_outbox ADD COLUMN before_state_json LONGTEXT NULL AFTER payload_json;

ALTER TABLE sync_outbox ADD COLUMN amendment_reason VARCHAR(500) NULL;
ALTER TABLE sync_outbox ADD COLUMN amendment_approver VARCHAR(100) NULL;
ALTER TABLE sync_outbox ADD COLUMN amendment_date DATETIME NULL;
```

### 23.4 Delete Event Capture (Database Trigger Approach)

Since the application performs hard DELETEs, we need to capture the deleted data BEFORE it's gone. Two approaches:

**Option A: Application-Level Capture (Recommended)**
- Modify the application's delete workflow to write to `sync_outbox` in the same transaction
- The existing deletion tables (`fa_voucherdeletionmain`, etc.) already capture the data — use them as the source for the sync event payload

**Option B: MySQL BEFORE DELETE Trigger (Fallback)**
```sql
-- For tables that don't go through the application's delete workflow
DELIMITER //
CREATE TRIGGER trg_fa_vouchermain_before_delete
BEFORE DELETE ON fa_vouchermain
FOR EACH ROW
BEGIN
  INSERT INTO sync_outbox (
    event_type, change_type, entity_type, entity_id,
    payload_json, payload_hash, idempotency_key, created_at
  ) VALUES (
    'DATA_CHANGE', 'DELETE', 'fa_vouchermain', OLD.VoucherNo,
    JSON_OBJECT(
      'VoucherNo', OLD.VoucherNo,
      'VoucherDate', OLD.VoucherDate,
      'Amount', OLD.Amount,
      'pacsid', OLD.pacsid
    ),
    SHA2(CONCAT(OLD.VoucherNo, OLD.VoucherDate, OLD.Amount), 256),
    CONCAT('DEL-fa_vouchermain-', OLD.VoucherNo, '-', UNIX_TIMESTAMP()),
    NOW()
  );
END //
DELIMITER ;
```

### 23.5 Amendment Event Capture

For in-place UPDATEs (amendments), capture before and after state:

**Application-Level Pattern:**
```csharp
// In the business service (e.g., LoanCorrectionService)
public async Task AmendLoanAsync(AmendLoanCommand command)
{
    // 1. Capture before state
    var beforeState = await _db.Loans.AsNoTracking()
        .Where(l => l.Id == command.LoanId)
        .Select(l => new { l.Amount, l.InterestRate, l.Status })
        .SingleAsync();

    // 2. Apply amendment
    var loan = await _db.Loans.SingleAsync(l => l.Id == command.LoanId);
    loan.Amount = command.NewAmount;
    loan.InterestRate = command.NewRate;

    // 3. Write sync event with before + after state
    _db.SyncOutbox.Add(new SyncOutboxEntry
    {
        EventType = "DATA_CHANGE",
        ChangeType = "AMENDMENT",
        EntityType = "ln_sanctions",
        EntityId = command.LoanId.ToString(),
        PayloadJson = JsonSerializer.Serialize(new { After = loan }),
        BeforeStateJson = JsonSerializer.Serialize(beforeState),
        AmendmentReason = command.Reason,
        AmendmentApprover = command.ApprovedBy,
        AmendmentDate = DateTime.UtcNow
    });

    // 4. Log to correction tool
    _db.CorrectionToolLog.Add(new CorrectionToolEntry { ... });

    // 5. Commit atomically
    await _db.SaveChangesAsync();
}
```

### 23.6 Worst-Case Recovery Strategies

| Scenario | Detection | Recovery |
|---|---|---|
| Record synced then deleted locally | Sync outbox captures DELETE event → NLDR receives it | NLDR soft-deletes its copy; reconciliation confirms |
| Backdated correction on synced data | AMENDMENT event in outbox with before/after state | NLDR applies amendment; flags for audit review |
| Accidental bulk deletion | Correction Tool log + sync outbox DELETE events | Restore from pre-operation backup (mandatory before bulk ops) |
| Offline amendment conflicts with NLDR | Not possible per current architecture (NLDR doesn't push amendments to PACS) | N/A |
| Deletion without outbox entry (bug) | Reconciliation detects missing record at NLDR | Manual investigation; restore from backup if needed |

### 23.7 Soft-Delete Recommendation (Future Enhancement)

For v2, recommend adding a `is_deleted` flag + `deleted_at` timestamp to critical financial tables:

```sql
-- Migration script (expand phase — non-breaking)
ALTER TABLE fa_vouchermain 
  ADD COLUMN is_deleted TINYINT(1) NOT NULL DEFAULT 0,
  ADD COLUMN deleted_at DATETIME NULL,
  ADD COLUMN deleted_by VARCHAR(100) NULL,
  ADD COLUMN deletion_reason VARCHAR(500) NULL,
  ALGORITHM=INSTANT;

-- Application change: replace DELETE with UPDATE
-- Old: DELETE FROM fa_vouchermain WHERE VoucherNo = ?
-- New: UPDATE fa_vouchermain SET is_deleted=1, deleted_at=NOW(), deleted_by=?, deletion_reason=? WHERE VoucherNo = ?
-- Add: WHERE is_deleted=0 to all SELECT queries
```

This is a **v2 enhancement** — for v1, the outbox-based event capture handles sync correctly without requiring schema changes to all 1,057 tables.

### 23.8 Configuration

```json
{
  "SyncDeletion": {
    "CaptureDeleteEvents": true,
    "CaptureAmendmentEvents": true,
    "UseTriggers": false,
    "UseApplicationCapture": true,
    "AmendmentRequiresReason": true,
    "AmendmentRequiresApprover": true,
    "BulkDeleteThreshold": 10,
    "BulkDeleteRequiresBackup": true,
    "RetainDeletedStateInOutboxDays": 365
  }
}
```

### 23.9 New Gaps (G97–G99)

| # | Gap / Risk | Severity | Mitigation |
|---|---|---|---|
| G97 | Hard deletes lose data permanently — NLDR becomes inconsistent | Critical | Outbox captures DELETE events with before-state; NLDR soft-deletes on its side; reconciliation verifies |
| G98 | In-place amendments on synced data create NLDR staleness | High | AMENDMENT events in outbox with before+after state; NLDR applies and flags for audit |
| G99 | Bulk deletion via Correction Tool without backup | Critical | Configurable threshold: bulk delete > N records requires mandatory backup first; Correction Tool enforces |

---

*End of enhanced plan v3.4 — Deletion & Amendment Sync. Ready for review.*


---

## 24. AUTO_INCREMENT Seed and ID Space Partitioning

### 24.1 ID Generation Scheme (Current)

The ePACS schema uses a **range-partitioned BIGINT ID space** to prevent collisions between PACS nodes and NLDR:

```
┌─────────────────────────────────────────────────────────────────┐
│  BIGINT ID SPACE (0 to 9.2 × 10¹⁸)                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  PACS 2115: [21150000000000 ─── 21159999999999]                  │
│  PACS 2116: [21160000000000 ─── 21169999999999]                  │
│  PACS 3042: [30420000000000 ─── 30429999999999]                  │
│  ...                                                             │
│  NLDR/Central: [1 ─── 9999999999] (reserved low range)           │
│                                                                  │
│  Formula: Seed = pacsid × RANGE_SIZE (10^10)                     │
│  Each PACS gets 10 billion IDs per table — effectively infinite  │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 24.2 Supporting Infrastructure

| Table | Purpose |
|-------|---------|
| `sequences` | Per-PACS, per-table sequence counters (`pacsid`, `sequencename`, `nextsequenceid`) |
| `sequencetables` | Registry of tables that use sequence-based ID generation |
| `pacsserialnumbers` | PACS-specific human-readable serial number registry |

### 24.3 Columns on Business Tables (~646 tables)

| Column | Type | Role |
|--------|------|------|
| PRIMARY KEY (SlNo, ApplicationNo, etc.) | BIGINT AUTO_INCREMENT | Globally unique — seeded per-PACS at install time |
| `idgeneratorforpacs` | BIGINT | PACS-local sequence counter (offline ID generation) |
| `SerialNumberOfPacs` | LONGTEXT | Human-readable serial (voucher/receipt numbers) |
| `SourceId` | TINYINT | Origin: 1=local PACS, 2=from NLDR, 3=legacy migration |

### 24.4 Installer Responsibilities

During fresh install, the installer MUST:

1. **Read `pacsid`** from `.epcfg` (numeric BIGINT, e.g., 2115)
2. **Compute seed** for each business table: `pacsid × 10000000000` (10^10)
3. **Set AUTO_INCREMENT** on all business tables:
   ```sql
   ALTER TABLE fa_vouchermain AUTO_INCREMENT = <seed>;
   ALTER TABLE ln_applicationmain AUTO_INCREMENT = <seed>;
   -- ... for all 646+ business tables
   ```
4. **Initialize `sequences` table** with correct `nextsequenceid` per table:
   ```sql
   INSERT INTO sequences (Id, pacsid, sequencename, nextsequenceid, SourceId)
   VALUES (1, <pacsid>, 'fa_vouchermain', <seed>, 1);
   -- ... for all sequenced tables
   ```
5. **Verify no overlap** with existing PACS in the same state/DCCB

### 24.5 Sync Collision Prevention

When syncing to NLDR:
- Records created at PACS have PKs in the PACS's range → no collision with NLDR or other PACS
- `SourceId = 1` marks them as PACS-originated
- NLDR stores them with the same PK (no re-keying needed)
- If NLDR pushes data to PACS, it uses `SourceId = 2` and IDs from the NLDR range (low numbers)

### 24.6 Configuration

```json
{
  "IdGeneration": {
    "RangeSize": 10000000000,
    "PacsIdSource": "epcfg",
    "NldrReservedRange": { "Min": 1, "Max": 9999999999 },
    "ValidateNoOverlap": true,
    "SequenceTablesPath": "config/sequence-tables.yaml"
  }
}
```

### 24.7 Risk: AUTO_INCREMENT Approaching Ceiling

| Table | Current AUTO_INCREMENT | BIGINT Max | Usage % | Risk |
|-------|----------------------|-----------|---------|------|
| `ln_productpurposemapping` | 2.2×10¹⁸ | 9.2×10¹⁸ | 24% | ⚠️ WATCH |
| `fa_ledger` | 1.3×10¹⁴ | 9.2×10¹⁸ | 0.001% | ✅ OK |
| `ln_applicationmain` | 2.1×10¹³ | 9.2×10¹⁸ | 0.0002% | ✅ OK |

The Installer Agent monitors AUTO_INCREMENT values daily (G49) and alerts at 50% usage.

---
