namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for PACS heartbeat to CoopsIndia Dashboard.
/// Sends periodic status updates so central operations know which PACS nodes are online.
/// Binds to the <c>Heartbeat</c> section of appsettings.json.
/// </summary>
public sealed class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    /// <summary>Whether heartbeat is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Interval between heartbeats in seconds (configurable, default 5 min).</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Transport protocol: "HTTPS" or "WebSocket".</summary>
    public string Transport { get; set; } = "HTTPS";

    /// <summary>HTTPS transport configuration.</summary>
    public HeartbeatHttpsOptions Https { get; set; } = new();

    /// <summary>WebSocket transport configuration.</summary>
    public HeartbeatWebSocketOptions WebSocket { get; set; } = new();

    /// <summary>Payload configuration — what data to include in heartbeat.</summary>
    public HeartbeatPayloadOptions Payload { get; set; } = new();

    /// <summary>Number of consecutive failures before circuit breaker opens.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Seconds to wait before retrying after circuit breaker opens.</summary>
    public int CircuitBreakerCooldownSeconds { get; set; } = 300;
}

public sealed class HeartbeatHttpsOptions
{
    /// <summary>CoopsIndia Dashboard endpoint URL.</summary>
    public string Endpoint { get; set; } = "https://dashboard.coopsindia.gov.in/api/v1.0/pacsStatus";

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Number of retry attempts per heartbeat.</summary>
    public int RetryCount { get; set; } = 3;
}

public sealed class HeartbeatWebSocketOptions
{
    /// <summary>WebSocket endpoint URL.</summary>
    public string Endpoint { get; set; } = "wss://dashboard.coopsindia.gov.in/ws/v1.0/pacsStatus";

    /// <summary>Reconnect interval after disconnect in seconds.</summary>
    public int ReconnectIntervalSeconds { get; set; } = 30;
}

public sealed class HeartbeatPayloadOptions
{
    /// <summary>Include disk usage percentage in heartbeat.</summary>
    public bool IncludeDiskUsage { get; set; } = true;

    /// <summary>Include sync status (pending outbox count, last sync time).</summary>
    public bool IncludeSyncStatus { get; set; } = true;

    /// <summary>Include service health status.</summary>
    public bool IncludeHealthStatus { get; set; } = true;

    /// <summary>Include file sync status (pending files count).</summary>
    public bool IncludeFilesSyncStatus { get; set; } = true;
}
