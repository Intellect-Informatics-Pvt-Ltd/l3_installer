namespace Sync.Abstractions;

/// <summary>
/// Transport abstraction for NLDR synchronization.
/// Implementations: HTTP (default), Disabled (pilot mode).
/// Selected via configuration — sync can be fully disabled for pilot deployments.
/// </summary>
public interface ISyncTransport
{
    /// <summary>Transport name for logging.</summary>
    string Name { get; }

    /// <summary>Whether this transport is enabled.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Sends a batch of sync events to NLDR.
    /// </summary>
    /// <param name="events">Events to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with per-event acknowledgments.</returns>
    Task<SyncBatchResult> SendBatchAsync(IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives inbound commands/data from NLDR.
    /// </summary>
    /// <param name="lastCheckpoint">Last processed checkpoint for resumption.</param>
    /// <param name="maxCount">Maximum events to receive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Inbound events.</returns>
    Task<IReadOnlyList<SyncEvent>> ReceiveAsync(string? lastCheckpoint, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests connectivity to NLDR endpoint.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if NLDR is reachable.</returns>
    Task<bool> ProbeConnectivityAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a sync event (outbound or inbound).
/// </summary>
public sealed record SyncEvent
{
    /// <summary>Unique event identifier for idempotency.</summary>
    public required string EventId { get; init; }

    /// <summary>Event type (e.g., "TRANSACTION", "MASTER_DATA_CHANGE", "AUDIT").</summary>
    public required string EventType { get; init; }

    /// <summary>Sequence number for ordering.</summary>
    public required long SequenceNumber { get; init; }

    /// <summary>Idempotency key for deduplication.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>JSON payload.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>SHA-256 hash of the payload for integrity verification.</summary>
    public required string PayloadHash { get; init; }

    /// <summary>Priority: 1=highest (financial), 2=audit, 3=master data, 4=telemetry.</summary>
    public int Priority { get; init; } = 3;

    /// <summary>Timestamp when the event was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>PACS ID that originated this event.</summary>
    public required string PacsId { get; init; }

    /// <summary>Sync protocol version.</summary>
    public string ProtocolVersion { get; init; } = "1.0";
}

/// <summary>
/// Result of sending a batch of events.
/// </summary>
public sealed record SyncBatchResult
{
    /// <summary>Whether the entire batch was acknowledged.</summary>
    public required bool Success { get; init; }

    /// <summary>Number of events successfully acknowledged.</summary>
    public int AcknowledgedCount { get; init; }

    /// <summary>Number of events that failed.</summary>
    public int FailedCount { get; init; }

    /// <summary>Last acknowledged sequence number (for checkpoint).</summary>
    public long? LastAcknowledgedSequence { get; init; }

    /// <summary>Error message if batch failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Per-event results for partial failures.</summary>
    public IReadOnlyList<EventAck> EventAcks { get; init; } = [];
}

/// <summary>
/// Acknowledgment for a single event.
/// </summary>
public sealed record EventAck
{
    public required string EventId { get; init; }
    public required bool Acknowledged { get; init; }
    public string? ErrorMessage { get; init; }
}
