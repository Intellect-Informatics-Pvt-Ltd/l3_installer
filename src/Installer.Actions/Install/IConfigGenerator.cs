using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Generates site-specific configuration files from templates.
/// Token replacement uses values from .epcfg and appsettings.json.
/// </summary>
public interface IConfigGenerator
{
    /// <summary>
    /// Generates all configuration files from templates using site-specific values.
    /// </summary>
    /// <param name="siteConfig">The site configuration pack providing site-specific values.</param>
    /// <param name="templateDirectory">Directory containing .template files.</param>
    /// <param name="outputDirectory">Directory to write generated config files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task GenerateAllAsync(
        SiteConfigPack siteConfig,
        string templateDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
