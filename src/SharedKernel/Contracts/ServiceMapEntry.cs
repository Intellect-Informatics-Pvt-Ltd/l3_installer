namespace SharedKernel.Contracts;

/// <summary>
/// Represents a service definition from service-map.yaml.
/// Defines service topology, health checks, and recovery actions.
/// </summary>
public sealed record ServiceMapEntry
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Executable { get; init; }
    public string? Arguments { get; init; }
    public required string Account { get; init; }
    public required int StartOrder { get; init; }
    public required int StopOrder { get; init; }
    public string StartupType { get; init; } = "Automatic";
    public required ServiceHealthCheck HealthCheck { get; init; }
    public required ServiceRecovery Recovery { get; init; }
    public string[] DataDirectories { get; init; } = [];
}

public sealed record ServiceHealthCheck
{
    /// <summary>Type of health check: "command", "tcp", "http".</summary>
    public required string Type { get; init; }

    /// <summary>Command to execute (for type=command).</summary>
    public string? Command { get; init; }

    /// <summary>Command arguments (for type=command).</summary>
    public string? Arguments { get; init; }

    /// <summary>Host to connect to (for type=tcp or type=http).</summary>
    public string? Host { get; init; }

    /// <summary>Port to connect to (for type=tcp).</summary>
    public string? Port { get; init; }

    /// <summary>URL to check (for type=http).</summary>
    public string? Url { get; init; }

    /// <summary>Timeout for the health check in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Expected exit code for command-based checks.</summary>
    public int SuccessExitCode { get; init; }

    /// <summary>Expected HTTP status code for HTTP-based checks.</summary>
    public int ExpectedStatus { get; init; } = 200;
}

public sealed record ServiceRecovery
{
    public required RecoveryAction FirstFailure { get; init; }
    public required RecoveryAction SecondFailure { get; init; }
    public required RecoveryAction Subsequent { get; init; }
    public int ResetAfterSeconds { get; init; } = 86400;
}

public sealed record RecoveryAction
{
    /// <summary>Action to take: "restart", "restart_and_bundle", "none".</summary>
    public required string Action { get; init; }

    /// <summary>Delay before taking action in seconds.</summary>
    public required int DelaySeconds { get; init; }
}
