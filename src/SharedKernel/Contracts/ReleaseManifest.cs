namespace SharedKernel.Contracts;

/// <summary>
/// Represents a signed release manifest that defines the complete stack release.
/// Parsed from release-manifest.yaml and verified before any installation action.
/// </summary>
public sealed record ReleaseManifest
{
    public required ManifestMetadata Manifest { get; init; }
    public required IReadOnlyList<PayloadEntry> Payloads { get; init; }
    public required CompatibilityInfo Compatibility { get; init; }
}

public sealed record ManifestMetadata
{
    public required string ManifestId { get; init; }
    public required string StackVersion { get; init; }
    public required int SchemaVersion { get; init; }
    public string? SyncProtocolVersion { get; init; }
    public required int MinOsBuild { get; init; }
    public required string InstallerToolVersion { get; init; }
    public required string SigningCertThumbprint { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public string? HotfixBaseVersion { get; init; }
}

public sealed record PayloadEntry
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required int InstallOrder { get; init; }
    public required bool Required { get; init; }
}

public sealed record CompatibilityInfo
{
    public required string MinUpgradeFrom { get; init; }
    public required string MaxUpgradeFrom { get; init; }
    public required bool RequiresSideBySide { get; init; }
    public bool BreakingSchemaChange { get; init; }
}
