using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharedKernel.Contracts;

namespace Installer.Core.SiteConfig;

/// <summary>
/// JSON-backed <see cref="ISiteConfigLoader"/>.
///
/// Until 2026-08-29 nothing in the product opened a <c>.epcfg</c> at all: <c>Installer.CLI</c>
/// parsed <c>/config:</c>, printed the path, and never read the file. Every site-specific value
/// the installer claims to honour therefore came from defaults.
/// </summary>
public sealed class SiteConfigLoader : ISiteConfigLoader
{
    // The value the sample pack ships with. Treated as "unsigned", not as a signature, because
    // a placeholder that parses is more dangerous than one that does not.
    private const string SignaturePlaceholder = "BASE64_ENCODED_SIGNATURE_PLACEHOLDER";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger<SiteConfigLoader> _logger;

    public SiteConfigLoader(ILogger<SiteConfigLoader> logger) => _logger = logger;

    public async Task<SiteConfigPack> LoadAsync(
        string path,
        bool allowUnsigned = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new SiteConfigException($"Site configuration pack not found: {path}");
        }

        SiteConfigPack? pack;
        try
        {
            await using var stream = File.OpenRead(path);
            pack = await JsonSerializer.DeserializeAsync<SiteConfigPack>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new SiteConfigException(
                $"Site configuration pack is not valid JSON ({path}): {ex.Message}", ex);
        }

        if (pack is null)
        {
            throw new SiteConfigException($"Site configuration pack is empty: {path}");
        }

        Validate(pack, path);
        CheckSignature(pack, path, allowUnsigned);

        LogEvents.SiteConfigLoaded(_logger, pack.PacsId, pack.StateCode, path);
        return pack;
    }

    /// <summary>
    /// Structural validation. These are the fields without which the installer cannot place a
    /// single file correctly, so each one is a hard stop rather than a defaulted value.
    /// </summary>
    private static void Validate(SiteConfigPack pack, string path)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(pack.PacsId)) missing.Add("pacs_id");
        if (string.IsNullOrWhiteSpace(pack.StateCode)) missing.Add("state_code");
        if (string.IsNullOrWhiteSpace(pack.DataRoot)) missing.Add("data_root");

        if (missing.Count > 0)
        {
            throw new SiteConfigException(
                $"Site configuration pack {path} is missing required field(s): {string.Join(", ", missing)}.");
        }

        // The state code selects appsettings.<STATE>.json inside every L2-R2 service via
        // ASPNETCORE_ENVIRONMENT. A wrong or malformed code does not fail at startup — the
        // service runs the wrong state's configuration, silently, which is far worse than a
        // crash. Constrain the shape here, where it is cheap to catch.
        if (pack.StateCode.Length is < 2 or > 3 || !pack.StateCode.All(char.IsAsciiLetterUpper))
        {
            throw new SiteConfigException(
                $"Site configuration pack {path} has state_code '{pack.StateCode}'. Expected 2-3 uppercase letters (for example 'AP', 'KA', 'MH'). " +
                "This value selects appsettings.<STATE>.json in every service; a wrong one runs the wrong state's configuration without failing.");
        }

        if (pack.SchemaVersion <= 0)
        {
            throw new SiteConfigException($"Site configuration pack {path} has no usable schema_version.");
        }
    }

    private void CheckSignature(SiteConfigPack pack, string path, bool allowUnsigned)
    {
        var signed = !string.IsNullOrWhiteSpace(pack.Signature)
                     && !string.Equals(pack.Signature, SignaturePlaceholder, StringComparison.Ordinal);

        if (signed)
        {
            // PRESENCE ONLY — this does not verify the signature. Cryptographic verification of
            // the pack is task 7.9 and needs a canonical serialisation of the document minus the
            // signature field, plus a byte-oriented overload on ISignatureVerifier (today it
            // takes file paths). Until that exists, say so rather than implying a check happened.
            LogEvents.SiteConfigSignaturePresent(_logger, path);
            return;
        }

        if (!allowUnsigned)
        {
            throw new SiteConfigException(
                $"Site configuration pack {path} carries no signature. An unsigned pack decides this node's identity, " +
                "data root, ports and backup targets, so it is refused by default. Pass --allow-unsigned-config only " +
                "for development against a pack you produced yourself.");
        }

        LogEvents.SiteConfigUnsignedAccepted(_logger, path);
    }
}
