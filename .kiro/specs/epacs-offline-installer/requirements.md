# Requirements: ePACS Offline Installer

## Overview
A production-grade, signed Windows bootstrapper that installs, upgrades, repairs, backs up, restores, and uninstalls the full ePACS ERP stack on offline PACS nodes in rural India. Built with WiX v4 Burn + C# Managed BootstrapperApplication.

## Actors
- **Field Operator**: Non-technical user who runs the installer at PACS sites
- **Installer Agent**: Always-on background service managing health, backups, and monitoring
- **Sync Agent**: Background service managing NLDR data synchronization
- **Release Engineer**: Builds, signs, and publishes installer packages
- **Support Engineer**: Analyzes support bundles and troubleshoots remotely
- **Security Lead**: Manages signing certificates, key escrow, and security policies

## User Stories

### US-1: Fresh Install
As a **Field Operator**, I want to install the full ePACS stack on a clean offline Windows machine from a single signed package, so that the PACS can begin operations without internet access.

**Acceptance Criteria:**
- AC-1.1: Installer runs fully offline after USB media delivery
- AC-1.2: Installer refuses to run if Authenticode signature or payload SHA-256 checksum validation fails
- AC-1.3: Creates durable data root (D:\ePACSData\) with correct NTFS ACLs per service account
- AC-1.4: Installs and starts all services (MySQL, Garnet, Kafka, business services, Web, Sync, Installer Agent) in dependency order
- AC-1.5: All health endpoints report expected versions and schema after install
- AC-1.6: DB/cache/eventing ports bound to localhost only (not LAN-accessible)
- AC-1.7: Support bundle can be generated containing diagnostics and no plaintext secrets
- AC-1.8: Post-install smoke test passes (create/verify/delete test record via API)
- AC-1.9: Fresh install completes in < 15 minutes on reference hardware (8 GB RAM, SSD)
- AC-1.10: All configuration sourced from appsettings.json templates — zero hardcoded values

### US-2: Silent/Unattended Install
As a **Release Engineer**, I want to run the installer silently using a signed Site Configuration Pack (.epcfg), so that centrally managed rollouts don't require operator interaction.

**Acceptance Criteria:**
- AC-2.1: `/quiet /config:<path-to-epcfg>` CLI mode completes without UI
- AC-2.2: All wizard inputs sourced from .epcfg file
- AC-2.3: Exit codes: 0=success, 1=precheck-fail, 2=install-fail, 3=health-fail, 99=unknown
- AC-2.4: Logs written to D:\ePACSData\logs\installer\ only (no console/UI output)
- AC-2.5: .epcfg signature validated before use; invalid signature blocks install

### US-3: Uninstall
As a **Field Operator**, I want to cleanly uninstall ePACS while preserving business data by default, so that reinstallation is possible without data loss.

**Acceptance Criteria:**
- AC-3.1: Stops all ePACS services in reverse dependency order
- AC-3.2: Deregisters all Windows services
- AC-3.3: Removes binaries under C:\Program Files\ePACS\
- AC-3.4: Preserves D:\ePACSData\ by default
- AC-3.5: Data purge requires signed Override Token + typed confirmation "PURGE <pacs_id>"
- AC-3.6: Generates final support bundle before removal

### US-4: Pre-Check Validation
As a **Field Operator**, I want the installer to validate all prerequisites before starting, so that I know immediately if the machine is not ready.

**Acceptance Criteria:**
- AC-4.1: Checks OS version (Win 10 1809+ / Server 2019+), architecture (x64), disk space, RAM, ports, admin rights
- AC-4.2: Checks for pending Windows reboot, AV exclusions, .NET environment conflicts
- AC-4.3: Displays green/yellow/red status per check with plain-language messages
- AC-4.4: Blocks install on critical failures; warns on non-critical issues
- AC-4.5: All thresholds configurable via appsettings.json (disk GB, RAM GB, port numbers)

### US-5: Installer Agent (Always-On Worker)
As a **Support Engineer**, I want an always-on agent monitoring PACS health, so that issues are detected and reported before they become critical.

**Acceptance Criteria:**
- AC-5.1: Polls all ePACS service health endpoints every 60 seconds (configurable)
- AC-5.2: Detects configuration drift (hourly SHA-256 comparison of config files)
- AC-5.3: Monitors disk space with configurable thresholds (Yellow < 20%, Red < 10%, Critical < 5%)
- AC-5.4: Monitors certificate expiry with 60/30/7-day warnings
- AC-5.5: Enforces log rotation (configurable retention: 30-day app, 90-day audit, 7-day MySQL)
- AC-5.6: Detects clock drift > 30s (warns) and > 5 min (blocks sync)
- AC-5.7: Auto-generates support bundle on critical failures
- AC-5.8: Runs post-install/daily smoke test
- AC-5.9: All intervals, thresholds, and retention periods configurable via appsettings.json

### US-6: Structured Logging & Error Handling
As a **Support Engineer**, I want all services to produce consistent structured JSON logs with correlation IDs and PII redaction, so that I can trace issues across services from a support bundle.

**Acceptance Criteria:**
- AC-6.1: All services use Intellect.Erp.Observability.Core (IAppLogger<T>, Serilog-backed)
- AC-6.2: Every operation gets a ULID correlation ID propagated across HTTP/Kafka/logs
- AC-6.3: PII (Aadhaar, mobile, account numbers) redacted via IRedactionEngine before logging
- AC-6.4: Errors use typed AppException hierarchy with stable error codes from YAML catalog
- AC-6.5: Error responses follow RFC 7807 ProblemDetails format with ePACS extensions
- AC-6.6: Support bundles contain only redacted logs — no plaintext secrets
- AC-6.7: Audit events bridge to Traceability module via TraceabilityBridgeAuditHook

### US-7: Configurable Architecture
As a **Release Engineer**, I want all paths, ports, thresholds, credentials, and behavior to be configurable via appsettings.json and .epcfg, so that no code changes are needed for site-specific deployments.

**Acceptance Criteria:**
- AC-7.1: Zero hardcoded file paths — all paths from configuration
- AC-7.2: Zero hardcoded port numbers — all ports from configuration with defaults
- AC-7.3: Zero hardcoded credentials — all generated or sourced from .epcfg
- AC-7.4: All monitoring thresholds configurable (disk %, intervals, retry counts)
- AC-7.5: All retention policies configurable (log days, backup count, attachment quota)
- AC-7.6: Service start/stop order configurable via service-map.yaml
- AC-7.7: Configuration templates generate site-specific configs from .epcfg values

### US-8: Manifest Verification & Security
As a **Security Lead**, I want the installer to verify all payloads against a signed release manifest before any installation action, so that tampered packages are rejected.

**Acceptance Criteria:**
- AC-8.1: Release manifest is signed with Authenticode EV certificate
- AC-8.2: Every payload file has SHA-256 hash listed in manifest; verified before extraction
- AC-8.3: USB media integrity verified (full archive hash) before extraction begins
- AC-8.4: Concurrent installer execution blocked via named mutex
- AC-8.5: Windows Update reboot suppressed during installer operation

## Non-Functional Requirements

### NFR-1: Offline Operation
- System must operate fully offline after initial installation
- No internet dependency at runtime or during install (all payloads bundled)

### NFR-2: Power-Cut Resilience
- Every operation must be resumable after hard power loss
- MySQL: innodb_flush_log_at_trx_commit=1, sync_binlog=1, doublewrite=ON
- Installer state checkpointed to disk before each phase transition
- Backup uses write-then-rename pattern (atomic on NTFS)

### NFR-3: Performance
- Fresh install: < 15 min on reference hardware
- Support bundle generation: < 2 min
- Health check cycle: < 5 sec
- Service restart (single): < 30 sec

### NFR-4: Supportability
- Structured JSON logs with schema v1 canonical fields
- Correlation ID traces across all services in a single operation
- Support bundle: one-click generation, encrypted, no PII

### NFR-5: Security
- Authenticode-signed installer and all payloads
- Least-privilege service accounts per service
- Localhost-only binding for DB/cache/eventing ports
- BitLocker-ready data volume (operator-optional)
- Certificate-wrapped backup encryption (AES-256-GCM)

### NFR-6: Configurability
- All behavior driven by appsettings.json + .epcfg
- No magic numbers in code — all thresholds as named configuration values
- Configuration templates with token replacement for site-specific values
