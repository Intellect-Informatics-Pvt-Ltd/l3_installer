namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for pre-installation validation checks.
/// Binds to the <c>Precheck</c> section of appsettings.json.
/// All thresholds are configurable — zero hardcoded values.
/// </summary>
public sealed class PrecheckOptions
{
    public const string SectionName = "Precheck";

    /// <summary>
    /// Minimum Windows OS build number required.
    /// Default: 17763 (Windows 10 version 1809 / Server 2019).
    /// </summary>
    public int MinOsBuild { get; set; } = 17763;

    /// <summary>
    /// Minimum physical RAM in GB required to proceed (blocking).
    /// </summary>
    public int MinRamGb { get; set; } = 8;

    /// <summary>
    /// Recommended physical RAM in GB (warning if below).
    /// </summary>
    public int RecommendedRamGb { get; set; } = 16;

    /// <summary>
    /// Minimum free space on the data volume in GB (blocking).
    /// </summary>
    public int MinDataDiskFreeGb { get; set; } = 100;

    /// <summary>
    /// Minimum free space on the system (C:) drive in GB.
    /// Below this threshold, installer relocates temp to data volume.
    /// </summary>
    public int MinSystemDiskFreeGb { get; set; } = 10;

    /// <summary>
    /// Ports that must be available (not in use by non-ePACS processes).
    /// </summary>
    public int[] RequiredPorts { get; set; } = [3306, 6379, 9092, 443];

    /// <summary>
    /// Whether to block installation if AV exclusions are not detected.
    /// Default: false (warn only).
    /// </summary>
    public bool BlockOnMissingAvExclusions { get; set; }

    /// <summary>
    /// Whether to block installation if a pending Windows reboot is detected.
    /// Default: false (warn only).
    /// </summary>
    public bool BlockOnPendingReboot { get; set; }

    /// <summary>
    /// Paths that should be excluded from antivirus scanning.
    /// Populated from DataRoot subdirectories.
    /// </summary>
    public string[] AvExclusionPaths { get; set; } = [];
}
