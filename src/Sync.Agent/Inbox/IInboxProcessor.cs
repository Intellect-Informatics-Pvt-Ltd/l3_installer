using Sync.Abstractions;

namespace Sync.Agent.Inbox;

/// <summary>
/// Processes inbound events from NLDR (commands, policy updates, master data pushes).
/// Implements idempotent apply with conflict resolution per BRD 12.6.
/// </summary>
public interface IInboxProcessor
{
    /// <summary>
    /// Processes a batch of inbound events.
    /// </summary>
    /// <param name="events">Events received from NLDR.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Processing result with per-event outcomes.</returns>
    Task<InboxProcessingResult> ProcessAsync(IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of inbox processing.
/// </summary>
public sealed record InboxProcessingResult
{
    public required int ProcessedCount { get; init; }
    public required int DuplicateCount { get; init; }
    public required int ConflictCount { get; init; }
    public required int ErrorCount { get; init; }
    public string? LastProcessedCheckpoint { get; init; }
    public IReadOnlyList<ConflictEntry> Conflicts { get; init; } = [];
}

/// <summary>
/// Represents a detected conflict during inbox processing.
/// </summary>
public sealed record ConflictEntry
{
    public required string EventId { get; init; }
    public required ConflictType Type { get; init; }
    public required string Description { get; init; }
    public required string Resolution { get; init; }
}

/// <summary>
/// Types of sync conflicts per BRD 12.6.
/// </summary>
public enum ConflictType
{
    /// <summary>Same event_id already processed.</summary>
    Duplicate,

    /// <summary>Sequence number lower than last applied.</summary>
    OutOfOrder,

    /// <summary>Central policy version > local policy version.</summary>
    PolicyChanged,

    /// <summary>Same master data changed locally and centrally.</summary>
    MasterDataConflict,

    /// <summary>Payload hash does not match payload content.</summary>
    HashMismatch
}
