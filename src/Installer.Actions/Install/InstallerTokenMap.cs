using System.Globalization;
using System.Text.RegularExpressions;
using SharedKernel.Configuration;

namespace Installer.Actions.Install;

/// <summary>
/// The infrastructure token vocabulary, in one place, shared by everything that substitutes into
/// a generated artefact.
///
/// ── WHY THIS EXISTS ─────────────────────────────────────────────────────────────────────────
///
/// It was extracted on 2026-08-29 after a systemd test caught a defect that had been sitting in
/// the Windows orchestrator since it was written. `samples/service-map.yaml` puts
/// <c>${Services:Web:HttpsPort}</c> in ePACSWeb's arguments and <c>${Services:MySql:Port}</c> in
/// MySQL's health check, but <c>ServiceOrchestrator.ResolveTokens</c> substituted only
/// <c>${BinaryRoot}</c> and <c>${DataRoot}</c>. So the web service would have been registered
/// with
///
///     --urls https://0.0.0.0:${Services:Web:HttpsPort}
///
/// which Kestrel cannot parse, and the MySQL health check would have pinged a port called
/// <c>${Services:MySql:Port}</c>. Neither had ever run, so neither had ever failed.
///
/// Two components each carrying their own idea of the vocabulary is how that happens.
/// <see cref="ConfigGenerator"/> knew about these tokens; the orchestrator did not. Now there is
/// one definition and three consumers.
/// </summary>
public static partial class InstallerTokenMap
{
    /// <summary>
    /// Tokens available everywhere: installer paths and infrastructure ports.
    ///
    /// <see cref="ConfigGenerator"/> seeds from this and adds the site pack's own fields and the
    /// N application services; the orchestrators use it as-is, because a service map describes
    /// infrastructure and binaries, not site policy.
    /// </summary>
    public static Dictionary<string, string> BuildInfrastructure(InstallerOptions installer, ServicesOptions services)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(services);
        var c = CultureInfo.InvariantCulture;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DataRoot"] = installer.DataRoot,
            ["BinaryRoot"] = installer.BinaryRoot,
            ["TempRoot"] = installer.ResolvedTempRoot,

            ["Services:MySql:Port"] = services.MySql.Port.ToString(c),
            ["Services:MySql:DatabaseName"] = services.MySql.DatabaseName,
            ["Services:MySql:ApplicationUser"] = services.MySql.ApplicationUser,
            ["Services:MySql:HealthCheckUser"] = services.MySql.HealthCheckUser,
            ["Services:Cache:Port"] = services.Cache.Port.ToString(c),
            ["Services:Eventing:Port"] = services.Eventing.Port.ToString(c),
            ["Services:Web:HttpsPort"] = services.Web.HttpsPort.ToString(c),
            ["Services:Sync:HealthPort"] = services.Sync.HealthPort.ToString(c),
            ["Services:Agent:HealthPort"] = services.Agent.HealthPort.ToString(c),
        };
    }

    /// <summary>
    /// Substitutes, and throws listing every unresolved token at once.
    ///
    /// Unresolved is fatal for the same reason it is fatal in <see cref="ConfigGenerator"/>:
    /// a service registered with a literal <c>${...}</c> in its command line does not fail at
    /// registration. It fails at start, or worse it starts and binds somewhere nobody expects,
    /// and the diagnosis happens days later and three layers from the cause.
    /// </summary>
    /// <param name="context">What is being resolved, named in the error — e.g. the service name.</param>
    public static string Resolve(string input, IReadOnlyDictionary<string, string> tokens, string context)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        var output = TokenPattern().Replace(input, match =>
        {
            var name = match.Groups[1].Value;
            if (tokens.TryGetValue(name, out var value))
            {
                return value;
            }

            unresolved.Add(name);
            return match.Value;
        });

        return unresolved.Count == 0
            ? output
            : throw new InvalidOperationException(
                $"{context} references {unresolved.Count} token(s) that do not exist: " +
                $"{string.Join(", ", unresolved.Select(t => "${" + t + "}"))}. " +
                "Registration is aborted rather than installing a service whose command line contains a literal ${...}.");
    }

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();
}
