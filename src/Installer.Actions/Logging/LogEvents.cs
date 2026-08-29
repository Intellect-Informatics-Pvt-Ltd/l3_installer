using Microsoft.Extensions.Logging;

using Installer.Actions.Prechecks;
using SharedKernel.Contracts;
namespace Installer.Actions;

/// <summary>
/// Source-generated log messages for Installer.Actions.
///
/// WHY THESE ARE NOT PLAIN ILogger CALLS. Two reasons, and the second is the one that matters
/// for this product.
///
/// 1. Cost. The ILogger extension overloads take <c>params object?[]</c>, so every argument is
///    boxed and an array allocated whether or not the level is enabled. CA1873 flags this. The
///    generator emits an IsEnabled guard, so a disabled level costs a branch.
///
/// 2. Stable EventIds. An installer is diagnosed from a support bundle by someone who was not
///    there. A stable, documented EventId is what lets a support engineer grep a bundle for
///    "what happened at the junction flip" without knowing the message wording, and it survives
///    message text being reworded or translated. Treat an EventId as an interface: reuse is
///    forbidden, retirement is fine, renumbering breaks every runbook that cites it.
///
/// EventId ranges across the product:
///   1000-1099  Installer.Core        (state machine, mode detection)
///   2000-2099  Installer.Actions     prechecks
///   2100-2199  Installer.Actions     install
///   2200-2299  Installer.Actions     uninstall
///   2300-2399  Installer.Actions     topology
///   2400-2499  Installer.Actions     harness integration
///   2700-2799  SharedKernel          audit chain
///   2800-2899  SharedKernel          secret store
///   3000-3099  BackupRestore
///   4000-4099  SupportBundle
///   5000-5099  Sync.Agent
/// </summary>
internal static partial class LogEvents
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Disk space check passed. Data: {DataFreeGb:F1} GB, System: {SystemFreeGb:F1} GB.")]
    public static partial void DiskSpacePassed(ILogger logger, double dataFreeGb, double systemFreeGb);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "OS version check passed. Build: {Build}, Architecture: x64.")]
    public static partial void OsVersionPassed(ILogger logger, int build);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "RAM check passed. Detected: {DetectedGb:F1} GB.")]
    public static partial void RamPassed(ILogger logger, double detectedGb);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Information, Message = "Starting precheck suite with {CheckCount} checks.")]
    public static partial void PrecheckSuiteStarting(ILogger logger, int checkCount);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Information, Message = "Running precheck: {CheckName} ({CheckId}).")]
    public static partial void PrecheckRunning(ILogger logger, string checkName, string checkId);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Information, Message = "Precheck suite complete. Passed: {Passed}, Warnings: {Warnings}, Blocking: {Blocking}. Can proceed: {CanProceed}.")]
    public static partial void PrecheckSuiteComplete(ILogger logger, int passed, int warnings, int blocking, bool canProceed);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Deploying version {Version} to {Path}.")]
    public static partial void DeployingVersion(ILogger logger, string version, string path);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Binaries deployed to {Path}.")]
    public static partial void BinariesDeployed(ILogger logger, string path);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Information, Message = "Switching 'current' junction to version {Version}.")]
    public static partial void SwitchingJunction(ILogger logger, string version);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Information, Message = "Junction '{Junction}' now points to '{Target}'.")]
    public static partial void JunctionSwitched(ILogger logger, string junction, string target);

    [LoggerMessage(EventId = 2110, Level = LogLevel.Information, Message = "Generating {Count} config files from templates.")]
    public static partial void GeneratingConfigs(ILogger logger, int count);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Information, Message = "Generated config: {OutputPath}.")]
    public static partial void ConfigGenerated(ILogger logger, string outputPath);

    [LoggerMessage(EventId = 2120, Level = LogLevel.Information, Message = "Initializing data root at {DataRoot}.")]
    public static partial void InitializingDataRoot(ILogger logger, string dataRoot);

    [LoggerMessage(EventId = 2121, Level = LogLevel.Information, Message = "Created directory: {Directory}.")]
    public static partial void DirectoryCreated(ILogger logger, string directory);

    [LoggerMessage(EventId = 2122, Level = LogLevel.Information, Message = "Directory already exists: {Directory}.")]
    public static partial void DirectoryAlreadyExists(ILogger logger, string directory);

    [LoggerMessage(EventId = 2123, Level = LogLevel.Information, Message = "Data root initialization complete. Created {Count} directories.")]
    public static partial void DataRootReady(ILogger logger, int count);

    [LoggerMessage(EventId = 2130, Level = LogLevel.Information, Message = "Extracting {Count} payloads to {StagingDir}.")]
    public static partial void ExtractionStarting(ILogger logger, int count, string stagingDir);

    [LoggerMessage(EventId = 2131, Level = LogLevel.Information, Message = "Payload {Name} already extracted. Skipping.")]
    public static partial void PayloadAlreadyExtracted(ILogger logger, string name);

    [LoggerMessage(EventId = 2132, Level = LogLevel.Information, Message = "Extracting payload {Index}/{Total}: {Name} ({File}).")]
    public static partial void ExtractingPayload(ILogger logger, int index, int total, string name, string file);

    [LoggerMessage(EventId = 2133, Level = LogLevel.Information, Message = "All {Count} payloads extracted successfully.")]
    public static partial void ExtractionComplete(ILogger logger, int count);

    [LoggerMessage(EventId = 2140, Level = LogLevel.Information, Message = "Registering {Count} Windows services.")]
    public static partial void RegisteringServices(ILogger logger, int count);

    [LoggerMessage(EventId = 2141, Level = LogLevel.Information, Message = "Starting {Count} services in dependency order.")]
    public static partial void StartingServices(ILogger logger, int count);

    [LoggerMessage(EventId = 2142, Level = LogLevel.Information, Message = "Stopping {Count} services in reverse dependency order.")]
    public static partial void StoppingServices(ILogger logger, int count);

    [LoggerMessage(EventId = 2143, Level = LogLevel.Information, Message = "Deregistering {Count} Windows services.")]
    public static partial void DeregisteringServices(ILogger logger, int count);

    [LoggerMessage(EventId = 2144, Level = LogLevel.Information, Message = "Registering service {Name} (account: {Account}, startup: {Startup}).")]
    public static partial void RegisteringService(ILogger logger, string name, string account, string startup);

    [LoggerMessage(EventId = 2145, Level = LogLevel.Information, Message = "Starting service {Name}...")]
    public static partial void StartingService(ILogger logger, string name);

    [LoggerMessage(EventId = 2146, Level = LogLevel.Information, Message = "Service {Name} started successfully.")]
    public static partial void ServiceStarted(ILogger logger, string name);

    [LoggerMessage(EventId = 2147, Level = LogLevel.Information, Message = "Stopping service {Name}...")]
    public static partial void StoppingService(ILogger logger, string name);

    [LoggerMessage(EventId = 2148, Level = LogLevel.Information, Message = "Service {Name} stopped.")]
    public static partial void ServiceStopped(ILogger logger, string name);

    [LoggerMessage(EventId = 2149, Level = LogLevel.Information, Message = "Deregistering service {Name}.")]
    public static partial void DeregisteringService(ILogger logger, string name);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "Starting uninstall. PurgeData: {PurgeData}.")]
    public static partial void UninstallStarting(ILogger logger, bool purgeData);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Information, Message = "Removing binaries from {BinaryRoot}.")]
    public static partial void RemovingBinaries(ILogger logger, string binaryRoot);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Information, Message = "Data preserved at {DataRoot}. To purge, re-run with override token and typed confirmation.")]
    public static partial void DataPreserved(ILogger logger, string dataRoot);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Loaded {Count} services from {Path}.")]
    public static partial void ServiceMapLoaded(ILogger logger, int count, string path);

    [LoggerMessage(EventId = 2401, Level = LogLevel.Information, Message = "Generating harness config for PACS {PacsId} (demo={Demo}) at {Path}.")]
    public static partial void HarnessConfigGenerating(ILogger logger, string pacsId, bool demo, string path);

    [LoggerMessage(EventId = 2402, Level = LogLevel.Information, Message = "Harness config generated successfully at {Path}.")]
    public static partial void HarnessConfigGenerated(ILogger logger, string path);

    [LoggerMessage(EventId = 2403, Level = LogLevel.Information, Message = "Loaded {Total} harness services from {Path}. Filtered to {Count} (includeNldr={IncludeNldr}).")]
    public static partial void HarnessServiceMapLoaded(ILogger logger, int total, string path, int count, bool includeNldr);

    [LoggerMessage(EventId = 2410, Level = LogLevel.Information, Message = "Running harness smoke test against {Count} services (demo={Demo}).")]
    public static partial void SmokeTestStarting(ILogger logger, int count, bool demo);

    [LoggerMessage(EventId = 2411, Level = LogLevel.Information, Message = "  {Icon} {Service}: {Status} ({ResponseMs}ms)")]
    public static partial void SmokeTestServiceResult(ILogger logger, string icon, string service, string status, int responseMs);

    [LoggerMessage(EventId = 2412, Level = LogLevel.Information, Message = "Harness smoke test PASSED in {Duration}ms.")]
    public static partial void SmokeTestPassed(ILogger logger, int duration);

    [LoggerMessage(EventId = 2413, Level = LogLevel.Debug, Message = "  {Service} returned {Status} on attempt {Attempt}/{Max}. Retrying...")]
    public static partial void SmokeTestRetryStatus(ILogger logger, string service, int status, int attempt, int max);

    [LoggerMessage(EventId = 2414, Level = LogLevel.Debug, Message = "  {Service} unreachable on attempt {Attempt}/{Max}: {Error}. Retrying...")]
    public static partial void SmokeTestRetryUnreachable(ILogger logger, string service, int attempt, int max, string error);

    /// <summary>
    /// Level is decided at runtime from the precheck severity, so this one takes a LogLevel
    /// rather than fixing it in the attribute. EventId 2012.
    /// </summary>
    [LoggerMessage(EventId = 2012, Message = "Precheck {CheckId}: {Severity} - {Message}")]
    public static partial void PrecheckResultLogged(
        ILogger logger, LogLevel level, string checkId, PrecheckSeverity severity, string message);
}
