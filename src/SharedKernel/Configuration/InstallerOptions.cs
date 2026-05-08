namespace SharedKernel.Configuration;

/// <summary>
/// Root configuration for installer paths and identity.
/// Binds to the <c>Installer</c> section of appsettings.json.
/// </summary>
public sealed class InstallerOptions
{
    public const string SectionName = "Installer";

    /// <summary>
    /// Root path for durable data (MySQL data, logs, config, keys, backups, attachments).
    /// Default: D:\ePACSData
    /// </summary>
    public string DataRoot { get; set; } = @"D:\ePACSData";

    /// <summary>
    /// Root path for application binaries and releases.
    /// Default: C:\Program Files\ePACS
    /// </summary>
    public string BinaryRoot { get; set; } = @"C:\Program Files\ePACS";

    /// <summary>
    /// Temporary staging directory for payload extraction.
    /// Defaults to {DataRoot}\temp if not specified.
    /// </summary>
    public string? TempRoot { get; set; }

    /// <summary>
    /// Path to the installer state checkpoint file (for power-cut recovery).
    /// Defaults to {DataRoot}\installer\state.json if not specified.
    /// </summary>
    public string? StateFile { get; set; }

    /// <summary>
    /// Path to the service map YAML file defining service topology.
    /// </summary>
    public string ServiceMapPath { get; set; } = "config/service-map.yaml";

    /// <summary>
    /// Path to the release manifest YAML file.
    /// </summary>
    public string ManifestPath { get; set; } = "release-manifest.yaml";

    /// <summary>
    /// Path to the site configuration pack (.epcfg) file.
    /// Can be overridden via CLI: /config:&lt;path&gt;
    /// </summary>
    public string? SiteConfigPath { get; set; }

    /// <summary>
    /// Resolved temp root (uses TempRoot if set, otherwise DataRoot\temp).
    /// </summary>
    public string ResolvedTempRoot => TempRoot ?? Path.Combine(DataRoot, "temp");

    /// <summary>
    /// Resolved state file path (uses StateFile if set, otherwise DataRoot\installer\state.json).
    /// </summary>
    public string ResolvedStateFile => StateFile ?? Path.Combine(DataRoot, "installer", "state.json");

    /// <summary>
    /// Path to the 'current' junction that points to the active release.
    /// </summary>
    public string CurrentJunctionPath => Path.Combine(BinaryRoot, "current");

    /// <summary>
    /// Path to the releases directory containing versioned binary folders.
    /// </summary>
    public string ReleasesPath => Path.Combine(BinaryRoot, "releases");

    /// <summary>
    /// Path to the tools directory (support bundle, backup CLI, smoke test).
    /// </summary>
    public string ToolsPath => Path.Combine(BinaryRoot, "tools");
}
