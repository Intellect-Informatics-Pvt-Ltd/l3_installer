using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Registers, starts, stops and removes the payload's services.
///
/// Two implementations, chosen by platform at composition time:
/// <see cref="ServiceOrchestrator"/> (Windows, <c>sc.exe</c> plus a registry write for the
/// environment) and <see cref="SystemdServiceOrchestrator"/> (Debian, unit files plus
/// <c>systemctl</c>) — per ADR-0010.
///
/// The topology is the same object on both: one <c>service-map.yaml</c> describes the payload
/// and each orchestrator renders it into what its own service manager understands. Nothing above
/// this interface knows which platform it is on, which is what keeps the pipeline single-source.
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
