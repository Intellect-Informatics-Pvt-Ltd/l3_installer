using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.Actions.Install;

/// <inheritdoc cref="IPayloadConfigRewriter"/>
public sealed class PayloadConfigRewriter : IPayloadConfigRewriter
{
    /// <summary>The key MySqlBootstrapper stores the application account's password under.</summary>
    public const string AppPasswordSecretKey = "mysql.app.password";

    /// <summary>What the medium calls the generator's sibling-URL table, relative to its root.</summary>
    public const string SiblingUrlsFileName = "sibling-urls.json";

    private static readonly JsonDocumentOptions Tolerant = new()
    {
        // The estate's appsettings carry comments and trailing commas in places
        // (build/config-hygiene.py has a tolerant_load for the same reason).
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly ISecretStore _secrets;
    private readonly ILogger<PayloadConfigRewriter> _logger;

    public PayloadConfigRewriter(ISecretStore secrets, ILogger<PayloadConfigRewriter> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<PayloadConfigResult> RewriteAsync(
        string servicesRoot,
        string? siteOverlayPath,
        string? siblingUrlsPath,
        IReadOnlyList<ServiceMapEntry> services,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servicesRoot);
        ArgumentNullException.ThrowIfNull(services);

        var overlay = siteOverlayPath is null ? null : await LoadOverlayAsync(siteOverlayPath, cancellationToken);
        var siblings = siblingUrlsPath is null ? null : await LoadSiblingUrlsAsync(siblingUrlsPath, cancellationToken);
        var password = await _secrets.RetrieveAsync(AppPasswordSecretKey, cancellationToken);

        var rewritten = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipped = new List<string>();

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var serviceDir = Path.Combine(servicesRoot, service.Name);
            var path = Path.Combine(serviceDir, "appsettings.json");

            if (!File.Exists(path))
            {
                if (RunsFromServicesDirectory(service))
                {
                    // The map says this service runs from services/<name>/ and there is no
                    // configuration there. It would start with no database, no log path and
                    // the compiled-in defaults - and it might even pass a TCP health check.
                    throw new ConfigGenerationException(
                        $"{service.Name} is deployed under {serviceDir} but carries no appsettings.json. It cannot be " +
                        "configured for this node and would start under compiled-in defaults, so the install is refused.");
                }

                skipped.Add(service.Name);
                continue;
            }

            var perService = siblings?.GetValueOrDefault(service.Name);
            var count = await RewriteOneAsync(path, service.Name, overlay, perService, password, cancellationToken);
            rewritten[service.Name] = count;
            LogEvents.PayloadConfigRewritten(_logger, service.Name, count);
        }

        if (rewritten.Count > 0 && password is null)
        {
            // Not fatal here - a harness medium has no bootstrapped database - but never silent.
            LogEvents.PayloadConfigNoPassword(_logger, rewritten.Count);
        }

        return new PayloadConfigResult
        {
            Rewritten = rewritten,
            Skipped = skipped,
            PasswordWritten = password is not null && rewritten.Count > 0
        };
    }

    private static bool RunsFromServicesDirectory(ServiceMapEntry service)
    {
        var marker = "/services/" + service.Name + "/";
        var exe = service.Executable.Replace('\\', '/');
        var args = (service.Arguments ?? "").Replace('\\', '/');
        return exe.Contains(marker, StringComparison.OrdinalIgnoreCase)
               || args.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> RewriteOneAsync(
        string path,
        string serviceName,
        JsonObject? overlay,
        Dictionary<string, Dictionary<string, string>>? siblingSections,
        string? password,
        CancellationToken ct)
    {
        JsonObject target;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            target = JsonNode.Parse(text, documentOptions: Tolerant)?.AsObject()
                     ?? throw new ConfigGenerationException($"{serviceName}: appsettings.json is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ConfigGenerationException(
                $"{serviceName}: appsettings.json does not parse ({ex.Message}). Refusing to rewrite a file that cannot be read back.", ex);
        }

        var count = 0;

        // 1. The site overlay: database, cache, log path, identity. Overlay wins on scalars,
        //    objects merge, arrays are replaced (a Serilog WriteTo list is a whole decision).
        if (overlay is not null)
        {
            count += Merge(target, overlay);
        }

        // 2. Sibling URLs, into the section the module actually reads (APIKeys or ERPKeys), and
        //    only for keys the file already has - the compose rule. A key that does not exist
        //    is not one the module reads, and inventing it would be inert at best.
        if (siblingSections is not null)
        {
            foreach (var (section, keys) in siblingSections)
            {
                if (target[section] is not JsonObject block)
                {
                    continue;
                }

                foreach (var (key, url) in keys)
                {
                    if (block.ContainsKey(key))
                    {
                        block[key] = url;
                        count++;
                    }
                }
            }
        }

        // 3. The application database password, onto every connection string. Appended, never
        //    logged: the value came from the secret store and goes into a file the service
        //    account alone can read.
        if (password is not null && target["ConnectionStrings"] is JsonObject conns)
        {
            foreach (var key in conns.Select(kv => kv.Key).ToList())
            {
                if (!key.StartsWith("//", StringComparison.Ordinal)
                    && conns[key] is JsonValue v && v.TryGetValue<string>(out var cs))
                {
                    conns[key] = WithPassword(cs, password);
                    count++;
                }
            }
        }

        // Prove it before writing it, then write-then-rename so a power cut cannot leave a
        // half-written file that parses as far as the truncation.
        var output = target.ToJsonString(Pretty);
        using (JsonDocument.Parse(output))
        {
        }

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, output, ct);
        File.Move(temp, path, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            // Owner read/write, group read, nobody else. Ownership (l2r2) is the ACL engine's job.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        return count;
    }

    /// <summary>Strips the connection string's existing Pwd/Password and appends the node's.</summary>
    internal static string WithPassword(string connectionString, string password)
    {
        var parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith("Password=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // MySqlConnector quotes a value containing ; or ' or a space with single quotes, doubling any inside.
        var quoted = password.IndexOfAny([';', '\'', ' ']) >= 0
            ? "'" + password.Replace("'", "''", StringComparison.Ordinal) + "'"
            : password;
        parts.Add("Pwd=" + quoted);
        return string.Join(";", parts);
    }

    /// <summary>Deep merge: overlay into target. Returns the number of leaf values set.</summary>
    internal static int Merge(JsonObject target, JsonObject overlay)
    {
        var count = 0;
        foreach (var (key, value) in overlay)
        {
            if (key.StartsWith("//", StringComparison.Ordinal))
            {
                continue;  // the template's own commentary is not configuration
            }

            if (value is JsonObject childOverlay)
            {
                if (target[key] is JsonObject childTarget)
                {
                    count += Merge(childTarget, childOverlay);
                }
                else
                {
                    target[key] = StripCommentary(childOverlay);
                    count += CountLeaves(childOverlay);
                }
            }
            else
            {
                target[key] = value?.DeepClone();
                count++;
            }
        }

        return count;
    }

    private static JsonObject StripCommentary(JsonObject source)
    {
        var clean = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (key.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            clean[key] = value is JsonObject o ? StripCommentary(o) : value?.DeepClone();
        }

        return clean;
    }

    private static int CountLeaves(JsonNode? node) => node switch
    {
        JsonObject o => o.Where(kv => !kv.Key.StartsWith("//", StringComparison.Ordinal)).Sum(kv => CountLeaves(kv.Value)),
        _ => 1
    };

    private static async Task<JsonObject> LoadOverlayAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new ConfigGenerationException(
                $"Site overlay not found: {path}. Generate the configuration before rewriting the payload.");
        }

        var text = await File.ReadAllTextAsync(path, ct);
        return JsonNode.Parse(text, documentOptions: Tolerant)?.AsObject()
               ?? throw new ConfigGenerationException($"Site overlay {path} is not a JSON object.");
    }

    /// <summary>service → section → key → url, exactly the shape build/generate-topology.py emits.</summary>
    private static async Task<Dictionary<string, Dictionary<string, Dictionary<string, string>>>?> LoadSiblingUrlsAsync(
        string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, ct);
        var root = JsonNode.Parse(text, documentOptions: Tolerant)?.AsObject();
        if (root?["services"] is not JsonObject services)
        {
            throw new ConfigGenerationException(
                $"{path} carries no `services` object; it is not a sibling-urls file the topology generator emits.");
        }

        var result = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var (service, sectionsNode) in services)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (sectionsNode is JsonObject sectionsObj)
            {
                foreach (var (section, keysNode) in sectionsObj)
                {
                    if (keysNode is not JsonObject keys)
                    {
                        continue;
                    }

                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (key, url) in keys)
                    {
                        map[key] = url?.GetValue<string>() ?? "";
                    }

                    sections[section] = map;
                }
            }

            result[service] = sections;
        }

        return result;
    }
}
