# AGENTS.md — ePACS Offline Installer

> This file provides context for AI coding assistants (Kiro, Cursor, Copilot, Windsurf, Cline, etc.) working on this repository.

## Project Overview

**ePACS Offline Installer** is a production-grade, signed Windows bootstrapper that installs, upgrades, repairs, backs up, restores, and uninstalls the full ePACS ERP stack on offline PACS (Primary Agricultural Credit Society) nodes in rural India.

**Target environment**: Windows 10/11 Pro or Server 2019+ (x64), 8 GB+ RAM, SSD, fully offline after installation. Rural Indian conditions: unreliable power, intermittent 4G connectivity, 35–45°C temperatures.

## Technology Stack

| Component | Technology | Version |
|-----------|-----------|---------|
| Language | C# 12 | .NET 8 LTS |
| Installer framework | WiX v4 Burn | 4.x |
| Database | MySQL | 8.4 LTS |
| Cache | Microsoft Garnet | Latest stable |
| Eventing | Apache Kafka (KRaft mode) | 3.7.x LTS |
| JRE (for Kafka) | Eclipse Temurin | 17 LTS |
| Backup tool | Percona XtraBackup | 8.4 |
| DDL migration | Percona pt-online-schema-change | Latest |
| Migration runner | DbUp | Latest |
| Logging | Serilog (via Intellect.Erp.Observability) | — |
| Testing | xUnit + FluentAssertions + Moq | — |

## Architecture

```
Solution: ePACS.Installer.sln
├── src/SharedKernel/           # Health contracts, config models, ICache abstraction
├── src/Installer.Core/         # State machine, manifest model, lock manager
├── src/Installer.Actions/      # Precheck, data-root, ACL, service orchestration
├── src/Installer.Agent/        # Always-on worker service (health, disk, drift, logs)
├── src/Installer.CLI/          # Silent/unattended CLI entry point
├── src/ManifestVerifier/       # Signed release manifest parsing and verification
├── src/SupportBundle/          # Collector with PII redaction
├── src/BackupRestore/          # Backup/restore workflows
├── tests/Installer.UnitTests/
├── tests/Installer.IntegrationTests/
├── packaging/                  # WiX, payloads, config templates, error catalogs
├── samples/                    # Sample manifests, service maps, .epcfg files
└── docs/                       # ADRs, operator guides, security baseline
```

## Critical Coding Standards

### 1. ZERO HARDCODING
**Every** path, port, threshold, interval, credential, and behavioral parameter MUST come from configuration:
- `appsettings.json` — compiled defaults
- `appsettings.Production.json` — environment overrides
- `.epcfg` (Site Config Pack) — site-specific values
- Environment variables — runtime overrides

```csharp
// ❌ NEVER DO THIS
var dataRoot = @"D:\ePACSData";
var mysqlPort = 3306;
var healthInterval = TimeSpan.FromSeconds(60);

// ✅ ALWAYS DO THIS
var dataRoot = _options.Value.DataRoot;
var mysqlPort = _options.Value.Services.MySql.Port;
var healthInterval = TimeSpan.FromSeconds(_options.Value.Monitoring.HealthPollIntervalSeconds);
```

### 2. Structured Logging (Intellect.Erp.Observability)
- Use `IAppLogger<T>` (not raw `ILogger<T>`)
- Use structured message templates with named parameters
- Use `BeginOperation()` for business context scoping
- Use `Checkpoint()` for tracking progress
- Never log PII — use `[Sensitive]`, `[DoNotLog]`, `[Mask]` attributes

```csharp
// ✅ Correct logging pattern
_logger.Information("Service {ServiceName} health check {Outcome} in {DurationMs}ms",
    serviceName, outcome, duration.TotalMilliseconds);

_logger.Checkpoint("PrecheckCompleted", new Dictionary<string, object?>
{
    ["PassedCount"] = passed,
    ["FailedCount"] = failed,
    ["BlockingCount"] = blocking
});
```

### 3. Error Handling (Intellect.Erp.ErrorHandling)
- Use `IErrorFactory` to create typed exceptions
- All error codes from YAML catalog (`packaging/error-catalog/*.yaml`)
- Error code format: `ERP-INST-{CATEGORY}-{NUMBER}` (e.g., `ERP-INST-PRE-0001`)
- Categories: PRE (precheck), INS (install), MIG (migration), BAK (backup), SYN (sync), HLT (health)

```csharp
// ✅ Correct error pattern
throw _errorFactory.FromCatalog("ERP-INST-PRE-0004",
    $"Data volume {volume} has only {freeGb} GB free, requires {requiredGb} GB");
```

### 4. Configuration Pattern
All options classes follow this pattern:
```csharp
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";
    
    public int HealthPollIntervalSeconds { get; set; } = 60;
    public int DiskCheckIntervalSeconds { get; set; } = 900;
    public DiskThresholdOptions DiskThresholds { get; set; } = new();
}

// Registration:
services.Configure<MonitoringOptions>(configuration.GetSection(MonitoringOptions.SectionName));
```

### 5. Power-Cut Resilience
- Every state transition writes a checkpoint (fsync'd)
- Use write-then-rename for atomic file operations
- MySQL: `innodb_flush_log_at_trx_commit=1`
- All operations must be resumable from the last checkpoint

### 6. Testing
- Unit tests: xUnit + FluentAssertions + Moq
- Test naming: `MethodName_Scenario_ExpectedResult`
- All configuration-driven behavior must be tested with different config values
- Use `Intellect.Erp.Observability.Testing` fakes for logging assertions

## Build Commands

```bash
# Build all
dotnet build ePACS.Installer.sln

# Run tests
dotnet test ePACS.Installer.sln

# Publish self-contained
dotnet publish src/Installer.CLI/Installer.CLI.csproj -c Release -r win-x64 --self-contained

# Publish Installer Agent
dotnet publish src/Installer.Agent/Installer.Agent.csproj -c Release -r win-x64 --self-contained
```

## Key Configuration Files

| File | Purpose | Location |
|------|---------|----------|
| `appsettings.json` | Default configuration | Shipped with binaries |
| `appsettings.Production.json` | Production overrides | Generated by installer |
| `service-map.yaml` | Service definitions (order, health, recovery) | `packaging/config-templates/` |
| `release-manifest.yaml` | Signed payload manifest | Root of installer package |
| `site-config-pack.epcfg` | Site-specific config (signed) | Distributed out-of-band |
| `installer.yaml` | Error catalog | `packaging/error-catalog/` |

## Dependencies (Internal NuGet Packages)

These are sibling utilities from the ePACS platform:
- `Intellect.Erp.Observability.Core` — Structured logging, enrichers, redaction
- `Intellect.Erp.Observability.Abstractions` — Contracts (IAppLogger, IErrorFactory, etc.)
- `Intellect.Erp.ErrorHandling` — Typed exceptions, YAML error catalog
- `Intellect.Erp.Observability.Propagation` — Correlation across HTTP/Kafka, TraceableBackgroundService
- `Intellect.Erp.Observability.AuditHooks` — Audit event bridge to Traceability
- `Intellect.Erp.Traceability` — Compliance-grade audit (11 tables, geo-tag, anomaly rules)

## File Naming Conventions

- Configuration models: `*Options.cs` (e.g., `MonitoringOptions.cs`)
- Service interfaces: `I*.cs` (e.g., `IHealthChecker.cs`)
- Implementations: `*.cs` matching interface (e.g., `HealthChecker.cs`)
- Tests: `*Tests.cs` (e.g., `HealthCheckerTests.cs`)
- Error catalog: `*.yaml` in `packaging/error-catalog/`
- Config templates: `*.template.json` in `packaging/config-templates/`

## Important Constraints

1. **Offline-only**: No internet access at runtime. All dependencies bundled.
2. **Windows-only**: Target is win-x64. No cross-platform needed.
3. **Self-contained publish**: No .NET runtime dependency on target machine.
4. **No containers**: Native Windows services only.
5. **Signed packages**: All binaries must be Authenticode-signed in CI.
6. **Data preservation**: Uninstall NEVER deletes business data without governance token.
7. **Resumable**: Every long operation must survive power-cut and resume.
