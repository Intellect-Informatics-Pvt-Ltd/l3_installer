namespace Sync.Agent.Reconciliation;

/// <summary>
/// Compares local outbox checkpoints with NLDR acknowledgments to detect sync drift.
/// Runs nightly + on-demand after restore or extended offline periods.
/// </summary>
public interface IReconciliationEngine
{
    /// <summary>
    /// Runs a full reconciliation cycle.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reconciliation report.</returns>
    Task<ReconciliationReport> ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Report of a reconciliation cycle.
/// </summary>
public sealed record ReconciliationReport
{
    public required DateTimeOffset ExecutedAt { get; init; }
    public required int TotalOutboxEvents { get; init; }
    public required int AcknowledgedCount { get; init; }
    public required int UnacknowledgedCount { get; init; }
    public required int SequenceGaps { get; init; }
    public required int HashMismatches { get; init; }
    public required int DuplicateAcks { get; init; }
    public required bool RequiresAction { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
}
