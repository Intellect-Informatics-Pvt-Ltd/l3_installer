using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <inheritdoc cref="IConfigGenerator"/>
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

    public async Task<ConfigGenerationResult> GenerateAllAsync(
        SiteConfigPack siteConfig,
        string templateDirectory,
        string outputDirectory,
        IReadOnlyList<ServiceMapEntry>? services = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(siteConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!Directory.Exists(templateDirectory))
        {
            // Was a warning-and-return. A silent skip means the node installs with no
            // site-specific configuration at all: default ports, default paths, default
            // identity — and reports success.
            throw new ConfigGenerationException(
                $"Template directory not found: {templateDirectory}. Without templates the node would install " +
                "with default paths, ports and identity and report success, so this is fatal rather than skipped.");
        }

        var templateFiles = Directory.GetFiles(templateDirectory, "*.template.*", SearchOption.AllDirectories);
        if (templateFiles.Length == 0)
        {
            throw new ConfigGenerationException(
                $"No *.template.* files under {templateDirectory}. This was the state of the repository until " +
                "2026-08-29: the generator was correct code that never ran, because nothing matched its pattern.");
        }

        Directory.CreateDirectory(outputDirectory);
        var tokens = BuildTokenMap(siteConfig, services);
        var generated = new List<string>();
        var resolved = 0;

        foreach (var templateFile in templateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(templateDirectory, templateFile);
            var outputPath = Path.Combine(outputDirectory, relativePath.Replace(".template", "", StringComparison.OrdinalIgnoreCase));

            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir is not null)
            {
                Directory.CreateDirectory(outputDir);
            }

            var content = await File.ReadAllTextAsync(templateFile, cancellationToken);
            var (output, count) = ResolveTokens(content, tokens, templateFile, NeedsJsonEscaping(outputPath));
            resolved += count;

            // Prove it before writing it. A config file that does not parse is discovered by
            // the service at startup, on the node, after the installer has reported success -
            // which is the worst possible place and time to find it.
            if (NeedsJsonEscaping(outputPath))
            {
                ValidateJson(output, templateFile);
            }

            // Write-then-rename: atomic on NTFS. A power cut mid-write must not leave a service
            // with a half-written configuration file, which parses as far as the truncation and
            // then silently omits everything after it.
            var tempPath = outputPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, output, cancellationToken);
            File.Move(tempPath, outputPath, overwrite: true);

            generated.Add(outputPath);
            LogEvents.ConfigGenerated(_logger, outputPath);
        }

        LogEvents.GeneratingConfigs(_logger, generated.Count);
        return new ConfigGenerationResult { GeneratedFiles = generated, TokensResolved = resolved };
    }

    /// <summary>
    /// Builds every token the templates may reference.
    ///
    /// Built eagerly and completely rather than resolved on demand, so an unresolved token means
    /// "this token does not exist" and never "this token exists but was not reachable from
    /// here" — which would be an error message pointing at the wrong thing.
    /// </summary>
    private Dictionary<string, string> BuildTokenMap(SiteConfigPack site, IReadOnlyList<ServiceMapEntry>? services)
    {
        var opts = _installerOptions.Value;
        var svc = _servicesOptions.Value;
        var c = CultureInfo.InvariantCulture;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Installer paths
            ["DataRoot"] = opts.DataRoot,
            ["BinaryRoot"] = opts.BinaryRoot,
            ["TempRoot"] = opts.ResolvedTempRoot,

            // Site identity, by the .epcfg's own JSON field names
            ["epcfg:pacs_id"] = site.PacsId,
            ["epcfg:state_code"] = site.StateCode,
            ["epcfg:district_code"] = site.DistrictCode ?? "",
            ["epcfg:language"] = site.Language,
            ["epcfg:data_root"] = site.DataRoot,
            ["epcfg:nldr_endpoint"] = site.NldrEndpoint ?? "",
            ["epcfg:nldr_client_cert_thumbprint"] = site.NldrClientCertThumbprint ?? "",
            ["epcfg:attachment_quota_gb"] = site.AttachmentQuotaGb.ToString(c),

            // Infrastructure
            ["Services:MySql:Port"] = svc.MySql.Port.ToString(c),
            ["Services:MySql:DatabaseName"] = svc.MySql.DatabaseName,
            ["Services:MySql:ApplicationUser"] = svc.MySql.ApplicationUser,
            ["Services:Cache:Port"] = svc.Cache.Port.ToString(c),
            ["Services:Eventing:Port"] = svc.Eventing.Port.ToString(c),
            ["Services:Web:HttpsPort"] = svc.Web.HttpsPort.ToString(c),
            ["Services:Sync:HealthPort"] = svc.Sync.HealthPort.ToString(c),
            ["Services:Agent:HealthPort"] = svc.Agent.HealthPort.ToString(c),
        };

        // Site-supplied port overrides. The .epcfg may pin ports for a site whose network
        // already uses the defaults; when it does, it wins over the installer's own options.
        if (site.Services is not null)
        {
            map["epcfg:services.mysql_port"] = site.Services.MysqlPort.ToString(c);
            map["epcfg:services.cache_port"] = site.Services.CachePort.ToString(c);
            map["epcfg:services.eventing_port"] = site.Services.EventingPort.ToString(c);
            map["epcfg:services.web_https_port"] = site.Services.WebHttpsPort.ToString(c);
        }

        // ── The N application services ───────────────────────────────────────
        // This is what F4 was about. A payload of 26 services is addressable here; a fixed
        // dictionary of four infrastructure ports was not.
        foreach (var (name, app) in svc.Applications)
        {
            map[$"Service:{name}:Port"] = app.Port.ToString(c);
            map[$"Service:{name}:StartOrder"] = app.StartOrder.ToString(c);
            map[$"Service:{name}:Account"] = app.ServiceAccount;
            map[$"Service:{name}:HealthPath"] = app.HealthPath ?? "";
        }

        // Anything the topology declares that the options did not. The service map is generated
        // from the modules' own appsettings in the estate's model - the code is authoritative -
        // so it can legitimately carry a service the installer's options have not been told about.
        foreach (var entry in services ?? [])
        {
            map.TryAdd($"Service:{entry.Name}:Account", entry.Account);
            map.TryAdd($"Service:{entry.Name}:StartOrder", entry.StartOrder.ToString(c));
        }

        return map;
    }

    /// <summary>
    /// Substitutes tokens, and throws listing every unresolved one at once.
    ///
    /// All of them, not the first: an operator fixing a template one failed build at a time is
    /// an operator making six trips to a site with no internet.
    /// </summary>
    /// <summary>
    /// True when the generated file is JSON and substituted values must be escaped for it.
    /// </summary>
    private static bool NeedsJsonEscaping(string outputPath) =>
        Path.GetExtension(outputPath).Equals(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Confirms the generated file parses. Comments are skipped: the shipped templates use
    /// <c>//</c> keys to carry the reasoning behind each setting, which is worth keeping.
    /// </summary>
    private static void ValidateJson(string output, string templatePath)
    {
        try
        {
            using var _ = JsonDocument.Parse(output,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new ConfigGenerationException(
                $"{Path.GetFileName(templatePath)} produced invalid JSON: {ex.Message}. " +
                "The file is not written. A config that does not parse is otherwise found by the service at startup, " +
                "on the node, after the installer has already reported success.", ex);
        }
    }

    /// <summary>
    /// Escapes a substituted value for a JSON string literal.
    ///
    /// NOT OPTIONAL ON THE TARGET PLATFORM. Every path token on Windows expands to something
    /// like <c>D:\ePACSData</c>, and dropping that raw into a JSON string produces <c>\e</c> -
    /// an invalid escape sequence. The whole file then fails to parse, and the first thing that
    /// notices is a service refusing to start on a node in a village.
    ///
    /// Found by a test asserting the shipped template's Serilog path, which is exactly the kind
    /// of defect that only appears once something actually runs the code.
    /// </summary>
    private static string EscapeForJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal)
             .Replace("\r", "\\r", StringComparison.Ordinal)
             .Replace("\t", "\\t", StringComparison.Ordinal);

    private static (string Output, int Resolved) ResolveTokens(
        string content, Dictionary<string, string> tokens, string templatePath, bool escapeForJson)
    {
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var resolved = 0;

        var output = TokenPattern().Replace(content, match =>
        {
            var name = match.Groups[1].Value;
            if (tokens.TryGetValue(name, out var value))
            {
                resolved++;
                return escapeForJson ? EscapeForJson(value) : value;
            }

            unresolved.Add(name);
            return match.Value;
        });

        return unresolved.Count == 0
            ? (output, resolved)
            : throw new ConfigGenerationException(
                $"{Path.GetFileName(templatePath)} references {unresolved.Count} token(s) that do not exist: " +
                $"{string.Join(", ", unresolved.Select(t => "${" + t + "}"))}. " +
                "Generation is aborted rather than writing a file containing a literal ${...}, which a service " +
                "either fails on at startup or - worse - silently treats as a value and falls back to a default.");
    }

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();
}
