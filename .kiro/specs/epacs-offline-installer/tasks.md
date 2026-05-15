# Tasks: ePACS Offline Installer — Phase 0 + Phase 1

## Phase 0: Repository Scaffolding & Architecture Foundation

- [x] 1. Create solution structure and project scaffolding
  - [x] 1.1 Create .NET 8 solution with all projects (Installer.Core, Installer.Actions, Installer.Agent, Installer.CLI, SharedKernel, ManifestVerifier, SupportBundle, BackupRestore)
  - [x] 1.2 Create packaging directory structure (wix, payloads, config-templates, scripts, error-catalog)
  - [x] 1.3 Create tests directory structure (UnitTests, IntegrationTests)
  - [x] 1.4 Create samples directory with release-manifest.yaml, service-map.yaml, site-config-pack.epcfg
  - [x] 1.5 Create docs directory with ADR folder structure
  - [x] 1.6 Create AGENTS.md file for cross-platform AI agent guidance
  - [x] 1.7 Create README.md with build instructions and architecture overview

- [x] 2. Define configuration models and appsettings schema
  - [x] 2.1 Create InstallerOptions configuration model (DataRoot, BinaryRoot, TempRoot, StateFile paths)
  - [x] 2.2 Create PrecheckOptions configuration model (all thresholds configurable)
  - [x] 2.3 Create ServicesOptions configuration model (ports, data directories per service)
  - [x] 2.4 Create MonitoringOptions configuration model (intervals, thresholds, failure counts)
  - [x] 2.5 Create BackupOptions configuration model (targets, schedule, retention, encryption)
  - [x] 2.6 Create LogRotationOptions configuration model (retention days, compression, max volume)
  - [x] 2.7 Create appsettings.json with all defaults and appsettings.Production.json template
  - [x] 2.8 Create appsettings.template.json for installer-generated site-specific config

- [x] 3. Define core data contracts
  - [x] 3.1 Create ReleaseManifest model (manifest_id, stack_version, schema_version, payloads with SHA-256)
  - [x] 3.2 Create SiteConfigPack model (.epcfg schema with signature, pacs_id, all configurable fields)
  - [x] 3.3 Create ServiceMap model (service definitions with start/stop order, health checks, recovery)
  - [x] 3.4 Create InstallationState model (state machine checkpoint for power-cut recovery)
  - [x] 3.5 Create HealthCheckResult model (service name, status, consecutive failures, timestamp)

- [x] 4. Create error catalog and error handling infrastructure
  - [x] 4.1 Create config/error-catalog/installer.yaml with all ERP-INST-* error codes (PRE, INS, MIG, BAK, SYN, HLT)
  - [x] 4.2 Create config/error-catalog/core.yaml with ERP-CORE-* base error codes
  - [x] 4.3 Wire IErrorFactory and IErrorCatalog in DI registration
  - [x] 4.4 Create InstallerException subclasses (PrecheckException, InstallException, MigrationException, etc.)

## Phase 1: Installer Core — State Machine & Manifest Verification

- [x] 5. Implement ManifestVerifier
  - [x] 5.1 Implement release manifest YAML parser (deserialize to ReleaseManifest model)
  - [x] 5.2 Implement Authenticode signature verification (SignedCms validation)
  - [x] 5.3 Implement SHA-256 payload hash verification (per-file against manifest)
  - [x] 5.4 Implement USB media integrity check (full archive hash before extraction)
  - [x] 5.5 Write unit tests for manifest parsing, signature verification, and hash verification

- [x] 6. Implement Installer.Core state machine
  - [x] 6.1 Define state enum (Load, Verify, Precheck, Install, Upgrade, Repair, Backup, Restore, Uninstall, Health, Smoke, Success, Failed, Recovery)
  - [x] 6.2 Implement state machine with checkpoint persistence (write state.json with fsync on each transition)
  - [x] 6.3 Implement recovery mode (detect incomplete state on startup, resume from checkpoint)
  - [x] 6.4 Implement concurrent execution guard (named mutex Global\ePACSInstaller with stale PID detection)
  - [x] 6.5 Implement mode detection (fresh install vs upgrade vs repair based on existing installation)
  - [x] 6.6 Write unit tests for state transitions, checkpoint persistence, and recovery

- [x] 7. Implement Installer.Actions — Precheck Suite
  - [x] 7.1 Implement OS version check (min build from config)
  - [x] 7.2 Implement disk space check (system + data volume, thresholds from config)
  - [x] 7.3 Implement RAM check (min/recommended from config)
  - [x] 7.4 Implement port availability check (ports from config)
  - [x] 7.5 Implement admin rights check
  - [x] 7.6 Implement pending reboot detection
  - [x] 7.7 Implement AV exclusion detection (Windows Defender paths)
  - [x] 7.8 Implement existing installation detection
  - [x] 7.9 Implement .epcfg signature validation
  - [x] 7.10 Implement temp staging relocation (if C: < threshold, use data volume)
  - [x] 7.11 Write unit tests for each precheck with configurable thresholds

- [x] 8. Implement Installer.Actions — Fresh Install
  - [x] 8.1 Implement data root creation (D:\ePACSData\ with subdirectories from config)
  - [x] 8.2 Implement NTFS ACL application (per-service accounts, paths from config)
  - [x] 8.3 Implement payload extraction (from verified archive to staging, resumable)
  - [x] 8.4 Implement binary deployment (stage to releases\<ver>\, create current junction)
  - [x] 8.5 Implement config generation from templates (token replacement from .epcfg + appsettings)
  - [x] 8.6 Implement Windows service registration (from service-map.yaml, recovery actions)
  - [x] 8.7 Implement service start in dependency order (from service-map.yaml start_order)
  - [x] 8.8 Implement firewall rules (localhost-only for DB/cache/eventing ports from config)
  - [x] 8.9 Implement Windows Update reboot suppression during operation
  - [x] 8.10 Write integration tests for fresh install on clean environment

- [x] 9. Implement Installer.Actions — Uninstall
  - [x] 9.1 Implement service stop in reverse order (from service-map.yaml stop_order)
  - [x] 9.2 Implement service deregistration
  - [x] 9.3 Implement binary removal (C:\Program Files\ePACS\)
  - [x] 9.4 Implement data preservation (D:\ePACSData\ kept by default)
  - [x] 9.5 Implement governance token verification for data purge (Override Token JWT validation)
  - [x] 9.6 Implement final support bundle generation before removal
  - [x] 9.7 Write unit tests for uninstall flow and token verification

- [x] 10. Implement Installer.Agent (v1)
  - [x] 10.1 Create InstallerAgent as TraceableBackgroundService with configurable main loop interval
  - [x] 10.2 Implement service health polling (configurable interval, endpoint from service-map.yaml)
  - [x] 10.3 Implement disk space monitoring (configurable thresholds from MonitoringOptions)
  - [x] 10.4 Implement log rotation enforcement (configurable retention from LogRotationOptions)
  - [x] 10.5 Implement support bundle auto-generation on critical failure
  - [x] 10.6 Implement configuration drift detection (SHA-256 comparison, configurable interval)
  - [x] 10.7 Implement clock drift detection (configurable thresholds)
  - [x] 10.8 Write unit tests for each monitoring module

- [x] 11. Implement SupportBundle collector
  - [x] 11.1 Implement log collection with IRedactionEngine integration (PII masking)
  - [x] 11.2 Implement service status collection (sc query for all ePACS services)
  - [x] 11.3 Implement version/manifest collection (installed versions, schema version)
  - [x] 11.4 Implement OS/disk/RAM summary collection
  - [x] 11.5 Implement config collection with secret redaction
  - [x] 11.6 Implement correlation-based log extraction (filter by correlationId)
  - [x] 11.7 Implement encrypted ZIP packaging
  - [x] 11.8 Write unit tests for redaction and packaging

- [x] 12. Implement Installer.CLI (silent mode)
  - [x] 12.1 Implement CLI argument parser (/quiet, /config:<path>, /mode:<install|uninstall|repair>)
  - [x] 12.2 Implement .epcfg loading and validation for silent mode
  - [x] 12.3 Implement exit code mapping (0=success, 1=precheck, 2=install, 3=health, 99=unknown)
  - [x] 12.4 Implement file-only logging (no console output in quiet mode)
  - [x] 12.5 Write integration tests for silent install flow

- [x] 13. Implement health endpoints and smoke test
  - [x] 13.1 Create health endpoint contract (/health/live, /health/ready, /health/version)
  - [x] 13.2 Implement smoke test runner (create/verify/delete test record via API)
  - [x] 13.3 Implement health check aggregator (all services green = system healthy)
  - [x] 13.4 Write unit tests for health aggregation logic

- [x] 14. Create AGENTS.md and documentation
  - [x] 14.1 Create AGENTS.md with project context, architecture, build commands, coding standards
  - [x] 14.2 Create ADR-0001 through ADR-0006 (WiX v4, Garnet, Kafka KRaft, Kestrel, DbUp, Sync abstraction)
  - [x] 14.3 Create operator-quick-start.md outline
  - [x] 14.4 Create security-baseline.md outline


## Phase 2: Upgrade, Backup, Restore, Repair

- [x] 15. Implement Backup Engine
  - [x] 15.1 Create IBackupEngine interface and BackupManifest model
  - [x] 15.2 Implement MySQL logical backup (mysqldump wrapper with configurable options)
  - [x] 15.3 Implement attachment backup (tar with per-file SHA-256 manifest)
  - [x] 15.4 Implement config and keys backup (with encryption)
  - [x] 15.5 Implement sync state export (outbox pending + checkpoints)
  - [x] 15.6 Implement backup encryption (AES-256-GCM with certificate-wrapped key)
  - [x] 15.7 Implement backup manifest generation and signing
  - [x] 15.8 Implement backup target validation (path exists, writable, sufficient space)
  - [x] 15.9 Implement backup verification (checksum, manifest signature, dump readability)
  - [x] 15.10 Write unit tests for backup engine

- [x] 16. Implement Restore Engine
  - [x] 16.1 Create IRestoreEngine interface
  - [x] 16.2 Implement backup package verification (signature + manifest + checksums)
  - [x] 16.3 Implement pre-restore safety backup creation
  - [x] 16.4 Implement MySQL restore (to staging datadir first, then validate)
  - [x] 16.5 Implement attachment restore with hash verification
  - [x] 16.6 Implement config and keys restore through decryption flow
  - [x] 16.7 Implement sync checkpoint restore with reconciliation marking
  - [x] 16.8 Write unit tests for restore engine

- [x] 17. Implement Upgrade Engine
  - [x] 17.1 Create IUpgradeEngine interface with side-by-side upgrade flow
  - [x] 17.2 Implement upgrade path validation (version compatibility from manifest)
  - [x] 17.3 Implement pre-upgrade backup (mandatory, blocks upgrade if fails)
  - [x] 17.4 Implement binary staging (extract new version to releases/<new>/)
  - [x] 17.5 Implement schema migration runner (DbUp wrapper with checkpoint persistence)
  - [x] 17.6 Implement junction flip (atomic commit: switch 'current' to new version)
  - [x] 17.7 Implement rollback on failure (revert junction, restore pre-upgrade backup)
  - [x] 17.8 Implement schema fingerprint capture (before and after upgrade)
  - [x] 17.9 Write unit tests for upgrade state transitions and rollback

- [x] 18. Implement Repair Mode
  - [x] 18.1 Implement payload hash verification against installed manifest
  - [x] 18.2 Implement binary replacement for mismatched files
  - [x] 18.3 Implement config regeneration from templates + installation_registry
  - [x] 18.4 Implement ACL re-application
  - [x] 18.5 Implement service re-registration if missing
  - [x] 18.6 Write unit tests for repair flow

- [x] 19. Implement Schema Fingerprinting (DDL drift detection)
  - [x] 19.1 Create ISchemaFingerprinter interface
  - [x] 19.2 Implement INFORMATION_SCHEMA capture (tables, columns, indexes, FKs)
  - [x] 19.3 Implement fingerprint hash computation and storage
  - [x] 19.4 Implement drift detection (compare current vs expected baseline)
  - [x] 19.5 Implement drift classification (benign, compatible, breaking)
  - [x] 19.6 Write unit tests for fingerprint comparison and drift classification


## Phase 3: Offline Sync Hardening

- [x] 20. Implement Outbox Relay (MySQL → Kafka)
  - [x] 20.1 Create IOutboxRelay interface
  - [x] 20.2 Implement MySQL outbox poller (SELECT ... FOR UPDATE SKIP LOCKED pattern)
  - [x] 20.3 Implement Kafka producer with configurable topic and partition key
  - [x] 20.4 Implement graceful Kafka-down handling (retry with backoff, business unaffected)
  - [x] 20.5 Implement checkpoint persistence (last relayed outbox ID in MySQL)
  - [x] 20.6 Write unit tests for outbox relay

- [x] 21. Implement Sync Agent
  - [x] 21.1 Create ISyncAgent interface with connectivity state machine
  - [x] 21.2 Implement connectivity detector (HTTPS HEAD probe to NLDR endpoint)
  - [x] 21.3 Implement circuit breaker (configurable failure threshold, half-open, cooldown)
  - [x] 21.4 Implement chunked upload with per-chunk ACK (resumable on reconnect)
  - [x] 21.5 Implement bandwidth detection and adaptive chunk sizing (4G/3G/2G)
  - [x] 21.6 Implement sync priority queue (financial txns → audit → master data → telemetry)
  - [x] 21.7 Implement dead-letter handling (after max retries → quarantine)
  - [x] 21.8 Implement durable checkpoint in MySQL (not in-memory)
  - [x] 21.9 Write unit tests for circuit breaker and retry logic

- [x] 22. Implement Inbox Processing (NLDR → PACS)
  - [x] 22.1 Create IInboxProcessor interface
  - [x] 22.2 Implement idempotent message apply (duplicate detection via event_id)
  - [x] 22.3 Implement conflict resolution per BRD 12.6 (duplicate, out-of-order, policy change, hash mismatch)
  - [x] 22.4 Implement inbound command handler (policy updates, master data pushes)
  - [x] 22.5 Write unit tests for conflict resolution logic

- [x] 23. Implement Reconciliation
  - [x] 23.1 Create IReconciliationEngine interface
  - [x] 23.2 Implement outbox checkpoint comparison with NLDR acknowledgments
  - [x] 23.3 Implement drift detection (unacknowledged events, sequence gaps, hash mismatches)
  - [x] 23.4 Implement reconciliation report generation (stored in MySQL + health dashboard)
  - [x] 23.5 Write unit tests for reconciliation logic


## Phase 4: Security Hardening

- [x] 24. Implement Signing and Verification Pipeline
  - [x] 24.1 Create ICodeSigner interface for Authenticode signing operations
  - [x] 24.2 Implement release manifest signing (detached CMS signature)
  - [x] 24.3 Implement backup manifest signing
  - [x] 24.4 Implement certificate chain validation (trusted root verification)
  - [x] 24.5 Write unit tests for signing and verification

- [x] 25. Implement Access Control (ACL) Engine
  - [x] 25.1 Create IAclEngine interface
  - [x] 25.2 Implement per-service account ACL rules (from service-map.yaml)
  - [x] 25.3 Implement data directory ACL lockdown (DB, cache, eventing, keys)
  - [x] 25.4 Implement ACL verification (Installer Agent health check)
  - [x] 25.5 Write unit tests for ACL rule generation

- [x] 26. Implement Firewall Rules Engine
  - [x] 26.1 Create IFirewallManager interface
  - [x] 26.2 Implement localhost-only binding rules (ports from config)
  - [x] 26.3 Implement outbound NLDR-only rule (443 to configured FQDN)
  - [x] 26.4 Implement firewall rule verification
  - [x] 26.5 Write unit tests for rule generation

- [x] 27. Implement Audit Log Hash Chaining
  - [x] 27.1 Create IAuditChain interface
  - [x] 27.2 Implement hash chain for critical events (install, upgrade, backup, restore, DB correction)
  - [x] 27.3 Implement chain verification (detect tampering)
  - [x] 27.4 Write unit tests for hash chain integrity

- [x] 28. Implement Secret Management
  - [x] 28.1 Create ISecretStore interface
  - [x] 28.2 Implement credential generation (DB passwords, service account passwords)
  - [x] 28.3 Implement secret encryption at rest (DPAPI or certificate-based)
  - [x] 28.4 Implement secret rotation support
  - [x] 28.5 Implement secret-scan validation (ensure no plaintext in logs/config/bundles)
  - [x] 28.6 Write unit tests for secret management
