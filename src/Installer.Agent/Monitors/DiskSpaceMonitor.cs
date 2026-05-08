using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Agent.Monitors;

/// <summary>
/// Monitors data volume free space with configurable thresholds.
/// Yellow/Red/Critical alerts with escalating actions.
/// </summary>
public sealed class DiskSpaceMonitor : IMonitor
{
    private readonly IOptions<MonitoringOptions> _monitoringOptions;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly ILogger<DiskSpaceMonitor> _logger;

    public DiskSpaceMonitor(
        IOptions<MonitoringOptions> monitoringOptions,
        IOptions<InstallerOptions> installerOptions,
        ILogger<DiskSpaceMonitor> logger)
    {
        _monitoringOptions = monitoringOptions;
        _installerOptions = installerOptions;
        _logger = logger;
    }

    public string Name => "DiskSpace";
    public int IntervalSeconds => _monitoringOptions.Value.DiskCheckIntervalSeconds;

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = _installerOptions.Value.DataRoot;
        var volumePath = Path.GetPathRoot(dataRoot) ?? dataRoot;
        var thresholds = _monitoringOptions.Value.DiskThresholds;

        try
        {
            var driveInfo = new DriveInfo(volumePath);
            var totalBytes = driveInfo.TotalSize;
            var freeBytes = driveInfo.AvailableFreeSpace;
            var freePercent = (int)(freeBytes * 100.0 / totalBytes);
            var freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);

            if (freePercent < thresholds.CriticalPercent)
            {
                _logger.LogCritical(
                    "DISK CRITICAL: Data volume {Volume} at {FreePercent}% free ({FreeGb:F1} GB). Threshold: {Threshold}%.",
                    volumePath, freePercent, freeGb, thresholds.CriticalPercent);
            }
            else if (freePercent < thresholds.RedPercent)
            {
                _logger.LogError(
                    "DISK RED: Data volume {Volume} at {FreePercent}% free ({FreeGb:F1} GB). Threshold: {Threshold}%.",
                    volumePath, freePercent, freeGb, thresholds.RedPercent);
            }
            else if (freePercent < thresholds.YellowPercent)
            {
                _logger.LogWarning(
                    "DISK YELLOW: Data volume {Volume} at {FreePercent}% free ({FreeGb:F1} GB). Threshold: {Threshold}%.",
                    volumePath, freePercent, freeGb, thresholds.YellowPercent);
            }
            else
            {
                _logger.LogInformation(
                    "Disk space OK: {Volume} at {FreePercent}% free ({FreeGb:F1} GB).",
                    volumePath, freePercent, freeGb);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check disk space for volume {Volume}.", volumePath);
        }

        return Task.CompletedTask;
    }
}
