using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Installer.MediaBuilder;

/// <summary>
/// Refuses to build a medium whose payload carries a credential (tasks.md 28.5; gap G27).
///
/// WHY. On 2026-09-13 the base <c>appsettings.json</c> files on <c>r2-dev-stable</c> still held the
/// 139 TD-123 secret values — the Keycloak client secret and admin password, the Gmail app
/// password, the Aadhaar API key, the WebLand password — and a medium built from a plain publish
/// would have carried every one of them to every PACS node, in a file that ends up in every
/// support bundle. Rotation is the owner's; this gate makes shipping the unrotated values
/// impossible in the meantime.
///
/// THE RULES ARE THE ESTATE'S, NOT A SECOND OPINION. Every pattern below is a byte-for-byte copy
/// of the one in <c>build/config-hygiene.py</c> (the JSON scanner the estate's CI runs), and
/// <c>SecretGateTests</c> reads that file and asserts equality, so the two cannot drift: a key
/// the estate's guard would flag is a key this gate refuses, and nothing more. In particular an
/// endpoint URL is not a credential (thirteen <c>/verifyaadharotp = https://...</c> settings are
/// topology), a key naming a location is not a credential (<c>TokenUrl: api/Token</c> is a path),
/// and a placeholder (<c>REPLACE_VIA_SECRETS</c>, <c>${...}</c>) is what a shipped file is supposed
/// to hold.
/// </summary>
public static partial class SecretGate
{
    // ── Verbatim from build/config-hygiene.py ────────────────────────────────
    public const string SecretValuePattern = @"(pwd|password)\s*=";
    public const string DefaultSecretKeyPattern = @"((password|secret|clientsecret|apikey|api_key|authtoken|accesskey|privatekey)$|aadhar|aadhaar|uidai)";
    public const string PlaceholderPattern = @"^(REPLACE_WITH|REPLACE_VIA|CHANGE_ME|TODO|<|\$\{|x{3,}$|\s*$|your[-_ ])|x{8,}$";
    public const string EndpointUrlPattern = @"^https?://";
    public const string LocationKeyPattern = @"(url|uri|endpoint|path|route)$";
    public const string UrlEmbeddedSecretPattern = @"[?&](key|token|secret|password|pwd|apikey|api_key)=";
    public const int MinSecretLength = 8;
    public const string FilePattern = "appsettings*.json";

    private static readonly Regex SecretValue = new(SecretValuePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretKey = new(DefaultSecretKeyPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndpointUrl = new(EndpointUrlPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LocationKey = new(LocationKeyPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlEmbeddedSecret = new(UrlEmbeddedSecretPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonDocumentOptions Tolerant = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public sealed record Finding(string File, string KeyPath, string MaskedValue);

    /// <summary>The estate's <c>Rules.is_secret</c>, one decision per leaf.</summary>
    public static bool IsSecret(string key, string? value, string path, IReadOnlyList<Regex>? allow = null)
    {
        if (value is null)
        {
            return false;
        }

        if (allow is not null && allow.Any(a => a.IsMatch(path)))
        {
            return false;
        }

        if (SecretValue.IsMatch(value))
        {
            return true;
        }

        if (EndpointUrl.IsMatch(value.Trim()) && !UrlEmbeddedSecret.IsMatch(value))
        {
            return false;
        }

        if (LocationKey.IsMatch(key))
        {
            return false;
        }

        return SecretKey.IsMatch(key)
               && value.Length >= MinSecretLength
               && !Placeholder.IsMatch(value);
    }

    /// <summary>config-hygiene's <c>mask()</c>: first two and last two characters, the rest starred.</summary>
    public static string Mask(string value) =>
        value.Length <= 4 ? new string('*', value.Length) : value[..2] + new string('*', Math.Min(value.Length - 4, 12)) + value[^2..];

    /// <summary>Scans a payload source — a directory, or a .zip — for every appsettings*.json it carries.</summary>
    public static IReadOnlyList<Finding> Scan(string source, IReadOnlyList<Regex>? allow = null)
    {
        var findings = new List<Finding>();

        if (Directory.Exists(source))
        {
            foreach (var file in Directory.EnumerateFiles(source, FilePattern, SearchOption.AllDirectories))
            {
                ScanJson(File.ReadAllText(file), Path.GetRelativePath(source, file), findings, allow);
            }
        }
        else if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(source);
            foreach (var entry in zip.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                if (!name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var reader = new StreamReader(entry.Open());
                ScanJson(reader.ReadToEnd(), entry.FullName, findings, allow);
            }
        }

        return findings;
    }

    private static void ScanJson(string text, string file, List<Finding> findings, IReadOnlyList<Regex>? allow)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, Tolerant);
        }
        catch (JsonException)
        {
            // A file that does not parse cannot be scanned - and cannot be trusted either.
            findings.Add(new Finding(file, "(unparseable)", "the file does not parse as JSON, so it cannot be cleared"));
            return;
        }

        using (doc)
        {
            Walk(doc.RootElement, "", file, findings, allow);
        }
    }

    private static void Walk(JsonElement node, string path, string file, List<Finding> findings, IReadOnlyList<Regex>? allow)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    var p = path + "/" + prop.Name;
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString();
                        if (IsSecret(prop.Name, value, p, allow))
                        {
                            findings.Add(new Finding(file, p, Mask(value!)));
                            continue;
                        }
                    }

                    Walk(prop.Value, p, file, findings, allow);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, $"{path}[{i++}]", file, findings, allow);
                }

                break;
        }
    }
}

/// <summary>Raised when a payload carries a credential. Exit 3 from the CLI, distinct from a build error (1) and an unsigned build (2).</summary>
public sealed class SecretGateException : MediaBuildException
{
    public IReadOnlyList<SecretGate.Finding> Findings { get; }

    public SecretGateException(string payload, IReadOnlyList<SecretGate.Finding> findings)
        : base(Describe(payload, findings))
    {
        Findings = findings;
    }

    private static string Describe(string payload, IReadOnlyList<SecretGate.Finding> findings)
    {
        var shown = findings.Take(20).Select(f => $"  {f.File} {f.KeyPath} = {f.MaskedValue}");
        var more = findings.Count > 20 ? $"\n  … and {findings.Count - 20} more" : "";
        return $"Payload '{payload}' carries {findings.Count} credential(s) and will not be put on a medium:\n" +
               string.Join("\n", shown) + more + "\n" +
               "A shipped configuration holds placeholders (REPLACE_VIA_SECRETS) or nothing; the node's own values are " +
               "generated at install time. Redact with `python3 build/config-hygiene.py redact` in the L2-R2 workspace, " +
               "or mark a fixture value with an --allow pattern if it is genuinely not a secret. Rotation is separate (TD-123).";
    }
}
