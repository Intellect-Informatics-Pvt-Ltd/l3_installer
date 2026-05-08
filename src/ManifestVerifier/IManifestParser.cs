using SharedKernel.Contracts;

namespace ManifestVerifier;

/// <summary>
/// Parses release manifest YAML into strongly-typed models.
/// </summary>
public interface IManifestParser
{
    /// <summary>Parses YAML content string into a ReleaseManifest.</summary>
    ReleaseManifest Parse(string yamlContent);

    /// <summary>Parses a YAML file from disk into a ReleaseManifest.</summary>
    ReleaseManifest ParseFile(string filePath);
}
