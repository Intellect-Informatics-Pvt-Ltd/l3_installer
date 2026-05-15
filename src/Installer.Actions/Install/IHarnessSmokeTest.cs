namespace Installer.Actions.Install;

/// <summary>
/// Post-install smoke test that verifies all harness services are healthy.
/// Called after the installer registers and starts harness Windows services.
/// </summary>
public interface IHarnessSmokeTest
{
    /// <summary>
    /// Runs health checks against all registered harness services.
    /// </summary>
    /// <param name="demoMode">When true, also checks NLDR-side services.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Smoke test result with per-service status.</returns>
    Task<HarnessSmokeResult> RunAsync(bool demoMode = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of the harness post-install smoke test.
/// </summary>
public sealed record HarnessSmokeResult
{
    public required bool AllHealthy { get; init; }
    public required IReadOnlyList<ServiceSmokeStatus> Services { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Health status of a single harness service during smoke test.
/// </summary>
public sealed record ServiceSmokeStatus
{
    public required string ServiceName { get; init; }
    public required string HealthUrl { get; init; }
    public required bool Healthy { get; init; }
    public int? HttpStatus { get; init; }
    public string? Error { get; init; }
    public TimeSpan ResponseTime { get; init; }
}
