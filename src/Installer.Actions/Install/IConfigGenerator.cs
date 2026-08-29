using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Generates site-specific configuration from templates.
///
/// Token namespaces, all of the form <c>${...}</c>:
///
///   <c>${DataRoot}</c>, <c>${BinaryRoot}</c>, <c>${TempRoot}</c>
///       Installer paths.
///
///   <c>${epcfg:pacs_id}</c>, <c>${epcfg:state_code}</c>, <c>${epcfg:services.mysql_port}</c>
///       Fields from the site configuration pack, by their JSON names. Site identity and site
///       facts — deliberately NOT state policy, which lives in appsettings.&lt;STATE&gt;.json
///       under the state-variation doctrine.
///
///   <c>${Services:MySql:Port}</c>
///       Infrastructure options, by section path.
///
///   <c>${Service:l3_FAS:Port}</c>
///       One of the N application services. This namespace is what makes a 26-service payload
///       expressible; before it, the token map was a fixed dictionary of four infrastructure
///       ports and could not name an application at all.
/// </summary>
public interface IConfigGenerator
{
    /// <summary>
    /// Generates every <c>*.template.*</c> file found under <paramref name="templateDirectory"/>,
    /// writing the resolved output to <paramref name="outputDirectory"/> with <c>.template</c>
    /// removed from the name.
    /// </summary>
    /// <exception cref="ConfigGenerationException">
    /// Thrown when any token cannot be resolved, or when no template is found. Both are fatal —
    /// see the type's own remarks.
    /// </exception>
    Task<ConfigGenerationResult> GenerateAllAsync(
        SiteConfigPack siteConfig,
        string templateDirectory,
        string outputDirectory,
        IReadOnlyList<ServiceMapEntry>? services = null,
        CancellationToken cancellationToken = default);
}

public sealed record ConfigGenerationResult
{
    public required IReadOnlyList<string> GeneratedFiles { get; init; }
    public required int TokensResolved { get; init; }
}

/// <summary>
/// Raised when configuration cannot be generated correctly.
///
/// WHY UNRESOLVED TOKENS ARE FATAL. The previous implementation logged a warning and left
/// <c>${Whatever}</c> in the output. That produces a configuration file containing a literal
/// <c>${...}</c>, which the service then fails on at startup — or, far worse, does not fail on,
/// because a port that should have been 5010 is now the string <c>"${Service:l3_FAS:Port}"</c>
/// and the binding silently falls back to a default. The diagnosis then happens days later and
/// three layers away from the cause.
///
/// This repository has already been bitten twice by the same shape: a service-map parser that
/// dropped health checks it did not understand, and a lock that logged a warning on every
/// failed release. A warning nobody reads is not a safety mechanism.
/// </summary>
public sealed class ConfigGenerationException : Exception
{
    public ConfigGenerationException(string message) : base(message) { }
    public ConfigGenerationException(string message, Exception inner) : base(message, inner) { }
    public ConfigGenerationException() { }
}
