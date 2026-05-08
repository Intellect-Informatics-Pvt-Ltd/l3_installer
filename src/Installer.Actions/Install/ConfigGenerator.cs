using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Generates site-specific configuration files by replacing tokens in templates.
/// Token format: ${token_name} — resolved from .epcfg values and InstallerOptions.
/// </summary>
public sealed partial class ConfigGenerator : IConfigGenerator
{
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ILogger<ConfigGenerator> _logger;

    public ConfigGenerator(
        IOptions<InstallerOptions> installerOptions,
        IOptions<ServicesOptions> servicesOptions,
        ILogger<ConfigGenerator> logger)
    {
        _installerOptions = installerOptions;
        _servicesOptions = servicesOptions;
        _logger = logger;
    }

    public async Task GenerateAllAsync(
        SiteConfigPack siteConfig,
        string templateDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(templateDirectory))
        {
            _logger.LogWarning("Template directory not found: {Dir}. Skipping config generation.", templateDirectory);
            return;
        }

        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var templateFiles = Directory.GetFiles(templateDirectory, "*.template.*", SearchOption.AllDirectories);
        _logger.LogInformation("Generating {Count} config files from templates.", templateFiles.Length);

        var tokenMap = BuildTokenMap(siteConfig);

        foreach (var templateFile in templateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(templateDirectory, templateFile);
            // Remove .template from filename: appsettings.template.json → appsettings.json
            var outputFileName = relativePath.Replace(".template", "", StringComparison.OrdinalIgnoreCase);
            var outputPath = Path.Combine(outputDirectory, outputFileName);

            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir is not null && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var templateContent = await File.ReadAllTextAsync(templateFile, cancellationToken);
            var resolvedContent = ResolveTokens(templateContent, tokenMap);

            // Write using atomic pattern (write-then-rename)
            var tempPath = outputPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, resolvedContent, cancellationToken);
            File.Move(tempPath, outputPath, overwrite: true);

            _logger.LogInformation("Generated config: {OutputPath}.", outputPath);
        }
    }

    private Dictionary<string, string> BuildTokenMap(SiteConfigPack siteConfig)
    {
        var opts = _installerOptions.Value;
        var svc = _servicesOptions.Value;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Installer paths
            ["DataRoot"] = opts.DataRoot,
            ["BinaryRoot"] = opts.BinaryRoot,
            ["TempRoot"] = opts.ResolvedTempRoot,

            // Site identity
            ["PacsId"] = siteConfig.PacsId,
            ["StateCode"] = siteConfig.StateCode,
            ["DistrictCode"] = siteConfig.DistrictCode ?? "",
            ["Language"] = siteConfig.Language,

            // Service ports
            ["Services:MySql:Port"] = svc.MySql.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Services:Cache:Port"] = svc.Cache.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Services:Eventing:Port"] = svc.Eventing.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Services:Web:HttpsPort"] = svc.Web.HttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture),

            // NLDR
            ["NldrEndpoint"] = siteConfig.NldrEndpoint ?? "",
            ["NldrClientCertThumbprint"] = siteConfig.NldrClientCertThumbprint ?? "",

            // Geo
            ["Latitude"] = siteConfig.SiteCoordinates?.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ["Longitude"] = siteConfig.SiteCoordinates?.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }

    private string ResolveTokens(string content, Dictionary<string, string> tokenMap)
    {
        return TokenPattern().Replace(content, match =>
        {
            var tokenName = match.Groups[1].Value;
            if (tokenMap.TryGetValue(tokenName, out var value))
            {
                return value;
            }

            _logger.LogWarning("Unresolved token in template: ${{{Token}}}.", tokenName);
            return match.Value; // Leave unresolved tokens as-is
        });
    }

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();
}
