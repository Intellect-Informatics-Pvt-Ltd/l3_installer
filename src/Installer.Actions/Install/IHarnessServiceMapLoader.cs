using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Loads and filters the harness service-map.yaml, returning only the services
/// appropriate for the current installation mode (PACS-only vs demo/full).
/// </summary>
public interface IHarnessServiceMapLoader
{
    /// <summary>
    /// Loads harness service definitions from the specified YAML file.
    /// </summary>
    /// <param name="serviceMapPath">Path to the harness service-map.yaml file.</param>
    /// <param name="includeNldr">When true, includes NLDR-side services (demo mode).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of service entries ready for registration.</returns>
    Task<IReadOnlyList<ServiceMapEntry>> LoadAsync(
        string serviceMapPath,
        bool includeNldr = false,
        CancellationToken cancellationToken = default);
}
