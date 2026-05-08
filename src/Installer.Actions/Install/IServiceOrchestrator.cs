using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Orchestrates Windows service registration, start, stop, and deregistration.
/// Service topology is driven entirely by service-map.yaml — no hardcoded service names.
/// </summary>
public interface IServiceOrchestrator
{
    /// <summary>
    /// Registers all services defined in the service map as Windows services.
    /// </summary>
    /// <param name="services">Service definitions from service-map.yaml.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RegisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts all services in dependency order (by start_order).
    /// </summary>
    /// <param name="services">Service definitions from service-map.yaml.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StartAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops all services in reverse dependency order (by stop_order descending).
    /// </summary>
    /// <param name="services">Service definitions from service-map.yaml.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StopAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deregisters (removes) all ePACS Windows services.
    /// </summary>
    /// <param name="services">Service definitions from service-map.yaml.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeregisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default);
}
