using SharedKernel.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ManifestVerifier;

/// <summary>
/// Parses release manifest YAML files into strongly-typed <see cref="ReleaseManifest"/> models.
/// </summary>
public sealed class ManifestParser : IManifestParser
{
    private readonly IDeserializer _deserializer;

    public ManifestParser()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <inheritdoc />
    public ReleaseManifest Parse(string yamlContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlContent);

        var raw = _deserializer.Deserialize<RawManifestDocument>(yamlContent);

        if (raw?.Manifest is null)
        {
            throw new InvalidOperationException("Manifest YAML is missing the 'manifest' root element.");
        }

        if (raw.Payloads is null || raw.Payloads.Count == 0)
        {
            throw new InvalidOperationException("Manifest YAML contains no payloads.");
        }

        return new ReleaseManifest
        {
            Manifest = MapMetadata(raw.Manifest),
            Payloads = raw.Payloads.Select(MapPayload).ToList(),
            Compatibility = MapCompatibility(raw.Compatibility ?? new RawCompatibility())
        };
    }

    /// <inheritdoc />
    public ReleaseManifest ParseFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Manifest file not found: {filePath}", filePath);
        }

        var content = File.ReadAllText(filePath);
        return Parse(content);
    }

    private static ManifestMetadata MapMetadata(RawManifestMetadata raw) => new()
    {
        ManifestId = raw.ManifestId ?? throw new InvalidOperationException("manifest_id is required"),
        StackVersion = raw.StackVersion ?? throw new InvalidOperationException("stack_version is required"),
        SchemaVersion = raw.SchemaVersion,
        SyncProtocolVersion = raw.SyncProtocolVersion,
        MinOsBuild = raw.MinOsBuild,
        InstallerToolVersion = raw.InstallerToolVersion ?? "unknown",
        SigningCertThumbprint = raw.SigningCertThumbprint ?? "",
        CreatedAt = raw.CreatedAt,
        CreatedBy = raw.CreatedBy ?? "unknown",
        HotfixBaseVersion = raw.HotfixBaseVersion
    };

    private static PayloadEntry MapPayload(RawPayload raw) => new()
    {
        Name = raw.Name ?? throw new InvalidOperationException("Payload name is required"),
        File = raw.File ?? throw new InvalidOperationException("Payload file is required"),
        Sha256 = raw.Sha256 ?? throw new InvalidOperationException("Payload sha256 is required"),
        SizeBytes = raw.SizeBytes,
        InstallOrder = raw.InstallOrder,
        Required = raw.Required
    };

    private static CompatibilityInfo MapCompatibility(RawCompatibility raw) => new()
    {
        MinUpgradeFrom = raw.MinUpgradeFrom ?? "0.0.0",
        MaxUpgradeFrom = raw.MaxUpgradeFrom ?? "99.99.99",
        RequiresSideBySide = raw.RequiresSideBySide,
        BreakingSchemaChange = raw.BreakingSchemaChange
    };

    // ── Raw YAML deserialization models ──

    private sealed class RawManifestDocument
    {
        public RawManifestMetadata? Manifest { get; set; }
        public List<RawPayload>? Payloads { get; set; }
        public RawCompatibility? Compatibility { get; set; }
    }

    private sealed class RawManifestMetadata
    {
        public string? ManifestId { get; set; }
        public string? StackVersion { get; set; }
        public int SchemaVersion { get; set; }
        public string? SyncProtocolVersion { get; set; }
        public int MinOsBuild { get; set; }
        public string? InstallerToolVersion { get; set; }
        public string? SigningCertThumbprint { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? HotfixBaseVersion { get; set; }
    }

    private sealed class RawPayload
    {
        public string? Name { get; set; }
        public string? File { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public int InstallOrder { get; set; }
        public bool Required { get; set; } = true;
    }

    private sealed class RawCompatibility
    {
        public string? MinUpgradeFrom { get; set; }
        public string? MaxUpgradeFrom { get; set; }
        public bool RequiresSideBySide { get; set; }
        public bool BreakingSchemaChange { get; set; }
    }
}
