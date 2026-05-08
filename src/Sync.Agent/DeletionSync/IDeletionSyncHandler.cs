namespace Sync.Agent.DeletionSync;

/// <summary>
/// Handles deletion and amendment events for NLDR synchronization.
/// Ensures that hard deletes and in-place updates are captured as sync events
/// so NLDR never has stale data.
///
/// Design principle: "Nothing is ever truly deleted from the sync perspective.
/// Deletions and amendments are EVENTS that must be propagated."
/// </summary>
public interface IDeletionSyncHandler
{
    /// <summary>
    /// Records a deletion event in the sync outbox.
    /// Called by the application BEFORE or AFTER the hard delete, within the same transaction.
    /// </summary>
    /// <param name="entry">The deletion event details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordDeletionAsync(DeletionSyncEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an amendment event in the sync outbox.
    /// Called by the application when an auditor corrects financial data.
    /// </summary>
    /// <param name="entry">The amendment event details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAmendmentAsync(AmendmentSyncEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a bulk deletion operation has a backup before proceeding.
    /// Blocks if record count exceeds configurable threshold without backup.
    /// </summary>
    /// <param name="entityType">Type of entity being deleted.</param>
    /// <param name="recordCount">Number of records to be deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if bulk delete can proceed; false if backup required first.</returns>
    Task<BulkDeleteValidation> ValidateBulkDeleteAsync(string entityType, int recordCount, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a deletion event to be synced to NLDR.
/// </summary>
public sealed record DeletionSyncEntry
{
    /// <summary>Table/entity type that was deleted from.</summary>
    public required string EntityType { get; init; }

    /// <summary>Primary key of the deleted record.</summary>
    public required string EntityId { get; init; }

    /// <summary>JSON representation of the record BEFORE deletion (the deleted data).</summary>
    public required string BeforeStateJson { get; init; }

    /// <summary>Who performed the deletion.</summary>
    public required string DeletedBy { get; init; }

    /// <summary>Reason for deletion (from Correction Tool or business workflow).</summary>
    public string? DeletionReason { get; init; }

    /// <summary>Correlation ID linking to the deletion operation logs.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>PACS ID where the deletion occurred.</summary>
    public required string PacsId { get; init; }

    /// <summary>Timestamp of the deletion.</summary>
    public DateTimeOffset DeletedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents an amendment event to be synced to NLDR.
/// </summary>
public sealed record AmendmentSyncEntry
{
    /// <summary>Table/entity type that was amended.</summary>
    public required string EntityType { get; init; }

    /// <summary>Primary key of the amended record.</summary>
    public required string EntityId { get; init; }

    /// <summary>JSON representation of the record BEFORE amendment.</summary>
    public required string BeforeStateJson { get; init; }

    /// <summary>JSON representation of the record AFTER amendment.</summary>
    public required string AfterStateJson { get; init; }

    /// <summary>Fields that were changed (for targeted sync).</summary>
    public IReadOnlyList<string> ChangedFields { get; init; } = [];

    /// <summary>Reason for the amendment (mandatory for financial data).</summary>
    public required string AmendmentReason { get; init; }

    /// <summary>Who approved the amendment.</summary>
    public required string ApprovedBy { get; init; }

    /// <summary>Who performed the amendment.</summary>
    public required string PerformedBy { get; init; }

    /// <summary>PACS ID where the amendment occurred.</summary>
    public required string PacsId { get; init; }

    /// <summary>Timestamp of the amendment.</summary>
    public DateTimeOffset AmendedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this amendment affects already-synced data (requires NLDR notification).</summary>
    public bool AffectsSyncedData { get; init; }
}

/// <summary>
/// Result of bulk delete validation.
/// </summary>
public sealed record BulkDeleteValidation
{
    /// <summary>Whether the bulk delete can proceed.</summary>
    public required bool CanProceed { get; init; }

    /// <summary>Whether a backup is required before proceeding.</summary>
    public bool BackupRequired { get; init; }

    /// <summary>Message explaining why the operation is blocked (if blocked).</summary>
    public string? BlockReason { get; init; }

    /// <summary>Configured threshold that was exceeded.</summary>
    public int Threshold { get; init; }
}
