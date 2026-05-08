using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Prechecks;

/// <summary>
/// Validates that sufficient disk space is available on both system and data volumes.
/// All thresholds are configurable via PrecheckOptions.
/// </summary>
public sealed class DiskSpaceCheck : IPrecheck
{
    private readonly IOptions<PrecheckOptions> _options;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly ILogger<DiskSpaceCheck> _logger;

    public DiskSpaceCheck(
        IOptions<PrecheckOptions> options,
        IOptions<InstallerOptions> installerOptions,
        ILogger<DiskSpaceCheck> logger)
    {
        _options = options;
        _installerOptions = installerOptions;
        _logger = logger;
    }

    public string CheckId => "DISK_SPACE";
    public string Name => "Disk Space";
    public int Order => 20;

    public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = _installerOptions.Value.DataRoot;
        var dataVolume = Path.GetPathRoot(dataRoot) ?? "D:\\";
        var systemVolume = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        // Check data volume
        var dataFreeGb = GetFreeSpaceGb(dataVolume);
        var requiredDataGb = _options.Value.MinDataDiskFreeGb;

        if (dataFreeGb < requiredDataGb)
        {
            _logger.LogError("Data volume {Volume} has {FreeGb:F1} GB free. Required: {RequiredGb} GB.",
                dataVolume, dataFreeGb, requiredDataGb);
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Block,
                Message = $"Data volume needs at least {requiredDataGb} GB free space.",
                TechnicalDetail = $"Volume: {dataVolume}, Free: {dataFreeGb:F1} GB, Required: {requiredDataGb} GB.",
                ErrorCode = "ERP-INST-PRE-0004"
            });
        }

        // Check system volume (warning only — installer can relocate temp)
        var systemFreeGb = GetFreeSpaceGb(systemVolume);
        var requiredSystemGb = _options.Value.MinSystemDiskFreeGb;

        if (systemFreeGb < requiredSystemGb)
        {
            _logger.LogWarning("System drive {Volume} has {FreeGb:F1} GB free. Threshold: {ThresholdGb} GB. Will use data volume for staging.",
                systemVolume, systemFreeGb, requiredSystemGb);
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Warning,
                Message = "Low space on system drive. Installer will use data volume for staging.",
                TechnicalDetail = $"System: {systemVolume} ({systemFreeGb:F1} GB free). Data: {dataVolume} ({dataFreeGb:F1} GB free).",
                ErrorCode = "ERP-INST-PRE-0003"
            });
        }

        _logger.LogInformation("Disk space check passed. Data: {DataFreeGb:F1} GB, System: {SystemFreeGb:F1} GB.",
            dataFreeGb, systemFreeGb);
        return Task.FromResult(new PrecheckResult
        {
            CheckId = CheckId,
            Name = Name,
            Severity = PrecheckSeverity.Pass,
            Message = $"Disk space OK. Data volume: {dataFreeGb:F0} GB free.",
            TechnicalDetail = $"Data: {dataVolume} ({dataFreeGb:F1} GB). System: {systemVolume} ({systemFreeGb:F1} GB)."
        });
    }

    private static double GetFreeSpaceGb(string volumePath)
    {
        try
        {
            var driveInfo = new DriveInfo(volumePath);
            return driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            // If we can't determine free space, return 0 to trigger the check
            return 0;
        }
    }
}
