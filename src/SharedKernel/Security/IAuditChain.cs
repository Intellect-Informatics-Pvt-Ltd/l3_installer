namespace SharedKernel.Security;

/// <summary>
/// Implements hash-chained audit logging for critical installer operations.
/// Each entry's hash includes the previous entry's hash, creating a tamper-evident chain.
/// If any entry is modified or deleted, the chain breaks and tampering is detected.
/// </summary>
public interface IAuditChain
{
    /// <summary>
    /// Appends a new entry to the audit chain.
    /// </summary>
    /// <param name="entry">The audit entry to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chain entry with computed hash.</returns>
    Task<AuditChainEntry> AppendAsync(AuditChainEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the integrity of the entire audit chain.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    Task<ChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last N entries from the chain.
    /// </summary>
    /// <param name="count">Number of entries to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recent chain entries.</returns>
    Task<IReadOnlyList<AuditChainEntry>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default);
}

/// <summary>
/// A single entry in the hash-chained audit log.
/// </summary>
public sealed record AuditChainEntry
{
    /// <summary>Sequence number (monotonically increasing).</summary>
    public long SequenceNumber { get; init; }

    /// <summary>Timestamp of the event (UTC).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Type of event: Install, Upgrade, Backup, Restore, DbCorrection, ConfigChange, Uninstall.</summary>
    public required string EventType { get; init; }

    /// <summary>Actor who performed the action.</summary>
    public required string Actor { get; init; }

    /// <summary>Description of the action.</summary>
    public required string Description { get; init; }

    /// <summary>Correlation ID linking to detailed logs.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Additional data (JSON).</summary>
    public string? DataJson { get; init; }

    /// <summary>Hash of the previous entry (null for first entry).</summary>
    public string? PreviousHash { get; init; }

    /// <summary>Hash of this entry (SHA-256 of: sequence + timestamp + eventType + actor + description + data + previousHash).</summary>
    public string? EntryHash { get; init; }
}

/// <summary>
/// Result of audit chain verification.
/// </summary>
public sealed record ChainVerificationResult
{
    public required bool Valid { get; init; }
    public required int TotalEntries { get; init; }
    public required int VerifiedEntries { get; init; }
    public long? FirstBrokenSequence { get; init; }
    public string? ErrorMessage { get; init; }
}
