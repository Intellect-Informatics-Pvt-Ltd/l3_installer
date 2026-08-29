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
    /// The signing certificate this installer will accept, as a SHA-1 thumbprint.
    ///
    /// THE TRUST ANCHOR, and it lives here on purpose. It is fixed when the installer is built
    /// and travels with the installer, not with the medium.
    ///
    /// It is deliberately NOT read from the release manifest's `signing_cert_thumbprint`. That
    /// value is self-asserted: an attacker who re-signs a tampered manifest with their own key
    /// writes their own thumbprint alongside it, and a check against it passes. The manifest's
    /// declaration is compared and reported; this one decides.
    ///
    /// When null, verification falls back to full certificate-chain validation against the
    /// machine's trust store — which is correct for a connected build machine and wrong for an
    /// air-gapped node, where an internal chain is not present and a correctly-signed medium
    /// would be refused with "certificate not trusted". Set it for anything that ships.
    /// </summary>
    public string? ExpectedSigningThumbprint { get; set; }

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
