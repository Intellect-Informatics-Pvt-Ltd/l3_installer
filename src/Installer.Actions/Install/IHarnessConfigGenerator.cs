using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Generates harness-specific configuration files (appsettings.Production.json)
/// from the site configuration pack (.epcfg) and installer options.
/// Used when the harness is deployed as native Windows services via the installer.
/// </summary>
public interface IHarnessConfigGenerator
{
    /// <summary>
    /// Generates the harness appsettings.Production.json at the specified output path.
    /// </summary>
    /// <param name="siteConfig">Site configuration pack providing PACS identity and endpoints.</param>
    /// <param name="outputPath">Full path to write the generated config file.</param>
    /// <param name="demoMode">When true, includes NLDR-side configuration for single-machine demos.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task GenerateAsync(
        SiteConfigPack siteConfig,
        string outputPath,
        bool demoMode = false,
        CancellationToken cancellationToken = default);
}
