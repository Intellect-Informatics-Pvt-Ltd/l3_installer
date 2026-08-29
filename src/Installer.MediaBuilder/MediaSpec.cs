using YamlDotNet.Serialization;

namespace Installer.MediaBuilder;

/// <summary>
/// The input to a media build: what goes on the stick, and what release it is.
///
/// Deliberately a file rather than a pile of command-line flags. A medium is a released artefact
/// and its composition should be reviewable in a diff — "why does this build carry Kafka?" ought
/// to be answerable from git history, not from a CI log.
/// </summary>
public sealed class MediaSpec
{
    public required MediaSpecRelease Release { get; set; }

    /// <summary>
    /// The payloads, in install order. Each names a source path relative to the spec file; a
    /// directory is archived, a file is copied.
    /// </summary>
    public List<MediaSpecPayload> Payloads { get; set; } = [];

    public MediaSpecCompatibility? Compatibility { get; set; }

    [YamlIgnore]
    public string? SpecDirectory { get; set; }
}

public sealed class MediaSpecRelease
{
    public required string StackVersion { get; set; }
    public int SchemaVersion { get; set; } = 25;
    public int MinOsBuild { get; set; } = 17763;
    public string InstallerToolVersion { get; set; } = "4.0.0";
    public string CreatedBy { get; set; } = "media-builder";

    /// <summary>
    /// Thumbprint the manifest will declare, and which the installer checks the signature
    /// against. Left null for an unsigned development medium.
    /// </summary>
    public string? SigningCertThumbprint { get; set; }

    /// <summary>
    /// Set for a hotfix: the release this one patches. Consumed by the upgrade engine to refuse
    /// a hotfix applied to the wrong base — see the note in MediaBuilder.
    /// </summary>
    public string? HotfixBaseVersion { get; set; }
}

public sealed class MediaSpecPayload
{
    public required string Name { get; set; }

    /// <summary>Path to a file or directory, relative to the spec file.</summary>
    public required string Source { get; set; }

    public int InstallOrder { get; set; }
    public bool Required { get; set; } = true;

    /// <summary>
    /// The component group this payload belongs to (<c>core</c>, <c>cache</c>, <c>eventing</c>),
    /// matching the service map. A payload for a component the site has switched off is left out
    /// of the medium entirely rather than shipped and ignored — that is the ~290 MB of Kafka and
    /// JRE that ADR-0003 made conditional.
    /// </summary>
    public string Group { get; set; } = "core";
}

public sealed class MediaSpecCompatibility
{
    public string MinUpgradeFrom { get; set; } = "0.0.0";
    public string MaxUpgradeFrom { get; set; } = "0.0.0";
    public bool RequiresSideBySide { get; set; }
    public bool BreakingSchemaChange { get; set; }
}
