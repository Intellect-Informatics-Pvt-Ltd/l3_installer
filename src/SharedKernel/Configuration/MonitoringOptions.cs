namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for the Installer Agent monitoring subsystem.
/// Binds to the <c>Monitoring</c> section of appsettings.json.
/// All intervals and thresholds are configurable.
/// </summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>Interval between service health polls in seconds.</summary>
    public int HealthPollIntervalSeconds { get; set; } = 60;

    /// <summary>Interval between disk space checks in seconds.</summary>
    public int DiskCheckIntervalSeconds { get; set; } = 900;

    /// <summary>Interval between configuration drift checks in seconds.</summary>
    public int DriftCheckIntervalSeconds { get; set; } = 3600;

    /// <summary>Interval between certificate expiry checks in seconds.</summary>
    public int CertExpiryCheckIntervalSeconds { get; set; } = 21600;

    /// <summary>Interval between clock drift checks in seconds.</summary>
    public int ClockDriftCheckIntervalSeconds { get; set; } = 1800;

    /// <summary>Disk space thresholds for alerting.</summary>
    public DiskThresholdOptions DiskThresholds { get; set; } = new();

    /// <summary>Clock drift thresholds.</summary>
    public ClockDriftThresholdOptions ClockDriftThresholds { get; set; } = new();

    /// <summary>Health failure thresholds for auto-restart and support bundle generation.</summary>
    public HealthFailureThresholdOptions HealthFailureThresholds { get; set; } = new();

    /// <summary>Certificate expiry warning thresholds in days.</summary>
    public int[] CertExpiryWarningDays { get; set; } = [60, 30, 7];
}

public sealed class DiskThresholdOptions
{
    /// <summary>Percentage free below which status is Yellow (warning).</summary>
    public int YellowPercent { get; set; } = 20;

    /// <summary>Percentage free below which status is Red (block new backups).</summary>
    public int RedPercent { get; set; } = 10;

    /// <summary>Percentage free below which status is Critical (block non-essential writes).</summary>
    public int CriticalPercent { get; set; } = 5;
}

public sealed class ClockDriftThresholdOptions
{
    /// <summary>Drift in seconds above which a warning is logged.</summary>
    public int WarnSeconds { get; set; } = 30;

    /// <summary>Drift in seconds above which sync is blocked.</summary>
    public int BlockSyncSeconds { get; set; } = 300;
}

public sealed class HealthFailureThresholdOptions
{
    /// <summary>Number of consecutive health check failures before attempting service restart.</summary>
    public int RestartAfterConsecutiveFailures { get; set; } = 3;

    /// <summary>Number of consecutive health check failures before generating support bundle.</summary>
    public int SupportBundleAfterConsecutiveFailures { get; set; } = 5;
}
