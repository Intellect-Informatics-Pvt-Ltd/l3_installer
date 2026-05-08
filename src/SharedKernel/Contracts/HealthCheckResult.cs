namespace SharedKernel.Contracts;

/// <summary>
/// Result of a service health check performed by the Installer Agent.
/// </summary>
public sealed record HealthCheckResult
{
    /// <summary>Name of the service that was checked.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Whether the service is healthy.</summary>
    public required bool Healthy { get; init; }

    /// <summary>Health status: Healthy, Degraded, Unhealthy.</summary>
    public required HealthStatus Status { get; init; }

    /// <summary>Number of consecutive health check failures.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Timestamp of this health check.</summary>
    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>Duration of the health check in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Error message if unhealthy.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional details (e.g., version info from health endpoint).</summary>
    public Dictionary<string, string>? Details { get; init; }
}

/// <summary>
/// Health status of a service.
/// </summary>
public enum HealthStatus
{
    /// <summary>Service is fully operational.</summary>
    Healthy,

    /// <summary>Service is operational but with warnings (e.g., high latency).</summary>
    Degraded,

    /// <summary>Service is not responding or returning errors.</summary>
    Unhealthy,

    /// <summary>Service status is unknown (check could not be performed).</summary>
    Unknown
}
