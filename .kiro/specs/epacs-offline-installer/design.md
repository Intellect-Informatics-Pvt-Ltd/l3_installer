# Design: ePACS Offline Installer

## Architecture Overview

The installer is a WiX v4 Burn bundle with a C# Managed BootstrapperApplication (WPF UI) that orchestrates installation of the full ePACS stack. Post-install, an always-on Installer Agent service handles health monitoring, backup scheduling, and operational automation.

## Component Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│  Installer Package (signed EXE)                                  │
│  ┌───────────────────┐  ┌───────────────────┐                   │
│  │ WiX Burn Engine   │  │ Managed BA (C#)   │                   │
│  │ (chain orchestr.) │  │ ┌───────────────┐ │                   │
│  │                   │  │ │ Installer.UI  │ │                   │
│  │                   │  │ │ (WPF + i18n)  │ │                   │
│  │                   │  │ └───────┬───────┘ │                   │
│  │                   │  │ ┌───────▼───────┐ │                   │
│  │                   │  │ │Installer.Core │ │                   │
│  │                   │  │ │(state machine)│ │                   │
│  │                   │  │ └───────┬───────┘ │                   │
│  │                   │  │ ┌───────▼───────┐ │                   │
│  │                   │  │ │Installer.     │ │                   │
│  │                   │  │ │Actions        │ │                   │
│  │                   │  │ └───────────────┘ │                   │
│  └───────────────────┘  └───────────────────┘                   │
│  Payloads: mysql.zip, garnet.zip, kafka.tgz, jre.zip,           │
│            vc-redist, epacs-services.zip, percona-toolkit.zip    │
└─────────────────────────────────────────────────────────────────┘
         │ installs
         ▼
┌─────────────────────────────────────────────────────────────────┐
│  Runtime (per PACS node)                                         │
│  C:\Program Files\ePACS\current\ → releases\<ver>\              │
│  D:\ePACSData\ (mysql, cache, eventing, logs, config, keys...)  │
│  Windows Services: MySQL → Garnet → Kafka → Business → Web →    │
│                    Sync → InstallerAgent                          │
└─────────────────────────────────────────────────────────────────┘
```

## Project Structure

```
/src
  /Installer.BootstrapperApp      # WiX Burn Managed BA entry point
  /Installer.Core                 # State machine, manifest model, lock manager
  /Installer.Core.SchemaMigration # DbUp wrapper, DDL classifier, schema fingerprinter
  /Installer.Actions              # Precheck, data-root, ACL, service orchestration
  /Installer.Actions.Uninstall    # Uninstall workflow
  /Installer.Actions.Repair       # Repair workflow
  /Installer.Actions.Hotfix       # Emergency hotfix fast-path
  /Installer.UI                   # WPF UI (i18n via .resx)
  /Installer.CLI                  # Silent/unattended CLI entry point
  /Installer.Agent                # Always-on worker service
  /Installer.Agent.HealthMonitor  # Service health polling
  /Installer.Agent.DiskMonitor    # Disk space monitoring
  /Installer.Agent.DriftDetector  # Config drift detection
  /Installer.Agent.LogRotator     # Log rotation enforcement
  /Installer.Agent.SmokeTest      # Post-install smoke test runner
  /BackupRestore                  # Backup/restore workflows
  /BackupRestore.XtraBackup       # Percona XtraBackup integration
  /ManifestVerifier               # Release manifest parsing and verification
  /SupportBundle                  # Collector with PII redaction
  /SharedKernel                   # Health contracts, config models, ICache abstraction
/packaging
  /wix                            # Bundle.wxs, payload definitions
  /payloads                       # Binary payloads (mysql, garnet, kafka, jre, etc.)
  /config-templates               # appsettings.template.json, my.ini.template, etc.
  /scripts                        # PowerShell: precheck, av-exclusions, firewall
  /error-catalog                  # YAML error catalogs (installer.yaml, core.yaml)
/tests
  /Installer.UnitTests
  /Installer.IntegrationTests
  /Installer.ChaosTests
/samples
  /release-manifest.yaml
  /service-map.yaml
  /site-config-pack.epcfg
/docs
  /adr
  AGENTS.md
```

## Key Design Decisions

### 1. Configuration-First Architecture
All behavior is driven by configuration. The hierarchy:
1. `appsettings.json` — compiled defaults (shipped with binaries)
2. `appsettings.Production.json` — environment overrides
3. `.epcfg` (Site Config Pack) — site-specific values (signed, distributed out-of-band)
4. Environment variables — runtime overrides (for CI/testing)

No magic numbers in code. Every threshold, path, port, interval, and retry count is a named configuration value with a sensible default.

### 2. State Machine (Installer.Core)
States: LOAD → VERIFY → PRECHECK → {INSTALL|UPGRADE|REPAIR|BACKUP|RESTORE|UNINSTALL} → HEALTH → SMOKE → SUCCESS
On failure: SUPPORT_BUNDLE → ROLLBACK (if applicable) → FAILED

Each state transition writes a checkpoint to `D:\ePACSData\installer\state.json` (fsync'd). On restart after power-cut, the state machine resumes from the last checkpoint.

### 3. Service Orchestration
Services defined in `service-map.yaml` with start_order, stop_order, health_check command, account, and recovery actions. The installer reads this file — not hardcoded service lists.

### 4. Observability Integration
- All projects reference `Intellect.Erp.Observability.Core` + `Intellect.Erp.ErrorHandling`
- Installer Agent extends `TraceableBackgroundService` (auto-correlation, auto-enrichment)
- Error catalog: `config/error-catalog/installer.yaml` with ERP-INST-* codes
- PII redaction via `IRedactionEngine` in support bundle collector

### 5. Percona Toolkit Integration
- Percona XtraBackup 8.4: physical incremental backups (daily)
- pt-online-schema-change: non-blocking DDL on tables > 1M rows
- pt-table-checksum: post-migration data consistency verification
- All bundled in installer payload (no internet dependency)

## Data Models

### Release Manifest (release-manifest.yaml)
```yaml
manifest:
  manifest_id: "rel-2026-05-04-3.2.1"
  stack_version: "3.2.1"
  schema_version: 25
  min_os_build: 17763
  installer_tool_version: "4.0.0"
  signing_cert_thumbprint: "ABC123..."
  created_at: "2026-05-04T00:00:00Z"
payloads:
  - name: "mysql"
    file: "mysql-8.4.2-winx64.zip"
    sha256: "abc123..."
    size_bytes: 450000000
  - name: "garnet"
    file: "garnet-1.0.0-win-x64.zip"
    sha256: "def456..."
    size_bytes: 25000000
  # ... more payloads
compatibility:
  min_upgrade_from: "3.1.0"
  max_upgrade_from: "3.2.0"
  requires_side_by_side: false
```

### Site Config Pack (.epcfg)
```json
{
  "signature": "<base64-encoded-signature>",
  "pacs_id": "AP-XYZ-0001",
  "state_code": "AP",
  "language": "en",
  "data_root": "D:\\ePACSData",
  "nldr_endpoint": "https://nldr.epacs.gov.in",
  "nldr_client_cert_thumbprint": "...",
  "backup_targets": ["D:\\ePACSBackups", "E:\\USBBackup"],
  "backup_schedule": { "daily": "02:00", "weekly": "Sunday 03:00" },
  "log_retention_days": { "application": 30, "audit": 90, "mysql": 7 },
  "attachment_quota_gb": 50,
  "site_coordinates": { "latitude": 16.5062, "longitude": 80.6480 },
  "monitoring": {
    "health_poll_interval_seconds": 60,
    "disk_check_interval_seconds": 900,
    "drift_check_interval_seconds": 3600
  },
  "services": {
    "mysql_port": 3306,
    "cache_port": 6379,
    "eventing_port": 9092,
    "web_https_port": 443
  }
}
```

### Service Map (service-map.yaml)
```yaml
services:
  - name: "ePACSMySQL"
    display_name: "ePACS MySQL"
    account: "ePACSDbSvc"
    start_order: 10
    stop_order: 90
    health_check: "mysqladmin ping --host=127.0.0.1 --port=${mysql_port}"
    recovery:
      first_failure: { action: "restart", delay_seconds: 60 }
      second_failure: { action: "restart", delay_seconds: 120 }
      subsequent: { action: "restart_and_bundle", delay_seconds: 300 }
      reset_after_seconds: 86400
  # ... more services (all configurable)
```

## Configuration Schema (appsettings.json)

```json
{
  "Installer": {
    "DataRoot": "D:\\ePACSData",
    "BinaryRoot": "C:\\Program Files\\ePACS",
    "TempRoot": "${DataRoot}\\temp",
    "StateFile": "${DataRoot}\\installer\\state.json",
    "ServiceMapPath": "config/service-map.yaml",
    "ManifestPath": "release-manifest.yaml"
  },
  "Precheck": {
    "MinOsBuild": 17763,
    "MinRamGb": 8,
    "RecommendedRamGb": 16,
    "MinDataDiskFreeGb": 100,
    "MinSystemDiskFreeGb": 10,
    "RequiredPorts": [3306, 6379, 9092, 443]
  },
  "Services": {
    "MySql": { "Port": 3306, "DataDir": "${DataRoot}\\mysql\\data" },
    "Cache": { "Port": 6379, "DataDir": "${DataRoot}\\cache" },
    "Eventing": { "Port": 9092, "DataDir": "${DataRoot}\\eventing\\data" },
    "Web": { "HttpsPort": 443 }
  },
  "Monitoring": {
    "HealthPollIntervalSeconds": 60,
    "DiskCheckIntervalSeconds": 900,
    "DriftCheckIntervalSeconds": 3600,
    "CertExpiryCheckIntervalSeconds": 21600,
    "ClockDriftCheckIntervalSeconds": 1800,
    "DiskThresholds": {
      "YellowPercent": 20,
      "RedPercent": 10,
      "CriticalPercent": 5
    },
    "ClockDriftThresholds": {
      "WarnSeconds": 30,
      "BlockSyncSeconds": 300
    },
    "HealthFailureThresholds": {
      "RestartAfterConsecutiveFailures": 3,
      "SupportBundleAfterConsecutiveFailures": 5
    }
  },
  "Backup": {
    "Targets": ["${DataRoot}\\backups"],
    "Schedule": { "Daily": "02:00", "Weekly": "Sunday 03:00" },
    "Retention": { "DailyCount": 7, "WeeklyCount": 4 },
    "Encryption": { "Algorithm": "AES-256-GCM" },
    "LargeDbThresholdGb": 5
  },
  "LogRotation": {
    "ApplicationRetentionDays": 30,
    "AuditRetentionDays": 90,
    "MySqlRetentionDays": 7,
    "CompressAfterDays": 7,
    "MaxLogVolumePercent": 10,
    "MaxLogVolumeGb": 50,
    "RotationTime": "02:00"
  },
  "Observability": {
    "ApplicationName": "epacs-installer-agent",
    "ModuleName": "Installer",
    "Environment": "Production",
    "Sinks": {
      "File": {
        "Enabled": true,
        "Path": "${DataRoot}\\logs\\installer\\installer-.json",
        "RollingInterval": "Day",
        "RetainedFileCountLimit": 30
      }
    },
    "Masking": { "Enabled": true },
    "AuditHook": { "Mode": "TraceabilityBridge" }
  },
  "ErrorHandling": {
    "CatalogFiles": ["config/error-catalog/core.yaml", "config/error-catalog/installer.yaml"],
    "SuppressStackTraceInProduction": true
  }
}
```

## Error Handling Strategy
- All errors use `IErrorFactory` to create typed `AppException` instances
- Error codes from YAML catalog: `ERP-INST-PRE-*` (precheck), `ERP-INST-INS-*` (install), `ERP-INST-MIG-*` (migration), `ERP-INST-BAK-*` (backup), `ERP-INST-SYN-*` (sync), `ERP-INST-HLT-*` (health)
- Operator sees: plain-language `userMessage`
- Support bundle contains: `supportMessage` with technical details
- Correlation ID links all log entries for a single operation
