namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for log rotation enforcement by the Installer Agent.
/// Binds to the <c>LogRotation</c> section of appsettings.json.
/// </summary>
public sealed class LogRotationOptions
{
    public const string SectionName = "LogRotation";

    /// <summary>Retention period for application logs in days.</summary>
    public int ApplicationRetentionDays { get; set; } = 30;

    /// <summary>Retention period for audit logs in days.</summary>
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>Retention period for MySQL slow/error logs in days.</summary>
    public int MySqlRetentionDays { get; set; } = 7;

    /// <summary>Number of days after which log files are compressed (gzip).</summary>
    public int CompressAfterDays { get; set; } = 7;

    /// <summary>
    /// Maximum percentage of data partition that logs may consume.
    /// If exceeded, oldest logs are deleted regardless of retention policy.
    /// </summary>
    public int MaxLogVolumePercent { get; set; } = 10;

    /// <summary>
    /// Absolute maximum log volume in GB (whichever is smaller: percent or absolute).
    /// </summary>
    public int MaxLogVolumeGb { get; set; } = 50;

    /// <summary>Time of day to run log rotation (HH:mm format, local time).</summary>
    public string RotationTime { get; set; } = "02:00";
}
