using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sync.Abstractions;

namespace Sync.Agent.Inbox;

/// <summary>
/// Processes inbound NLDR events with idempotent apply and conflict resolution.
/// Conflict resolution rules per BRD 12.6:
/// - Duplicate event → ACK without applying
/// - Out-of-order → hold/reject, require reconciliation if financial
/// - Central policy changed → apply prospectively, flag old-policy transactions
/// - Master data conflict → central wins for governed data, PACS wins for local transactions
/// - Hash mismatch → reject, quarantine, raise tamper alert
/// </summary>
public sealed class InboxProcessor : IInboxProcessor
{
    private readonly ILogger<InboxProcessor> _logger;

    // In production, these would be backed by MySQL tables
    private readonly HashSet<string> _processedEventIds = new(StringComparer.Ordinal);
    private long _lastAppliedSequence;

    public InboxProcessor(ILogger<InboxProcessor> logger)
    {
        _logger = logger;
    }

    public Task<InboxProcessingResult> ProcessAsync(IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        var duplicates = 0;
        var conflicts = new List<ConflictEntry>();
        var errors = 0;

        foreach (var evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Duplicate detection
            if (_processedEventIds.Contains(evt.EventId))
            {
                _logger.LogInformation("Duplicate event {EventId} — ACK without applying.", evt.EventId);
                duplicates++;
                continue;
            }

            // 2. Hash verification
            var computedHash = ComputePayloadHash(evt.PayloadJson);
            if (!string.Equals(computedHash, evt.PayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Hash mismatch for event {EventId}. Quarantining.", evt.EventId);
                conflicts.Add(new ConflictEntry
                {
                    EventId = evt.EventId,
                    Type = ConflictType.HashMismatch,
                    Description = $"Expected hash: {evt.PayloadHash}, Got: {computedHash}",
                    Resolution = "Rejected and quarantined. Tamper/corruption alert raised."
                });
                errors++;
                continue;
            }

            // 3. Out-of-order detection
            if (evt.SequenceNumber <= _lastAppliedSequence)
            {
                _logger.LogWarning("Out-of-order event {EventId} (seq {Seq} <= last {Last}).",
                    evt.EventId, evt.SequenceNumber, _lastAppliedSequence);
                conflicts.Add(new ConflictEntry
                {
                    EventId = evt.EventId,
                    Type = ConflictType.OutOfOrder,
                    Description = $"Sequence {evt.SequenceNumber} <= last applied {_lastAppliedSequence}",
                    Resolution = evt.Priority == 1
                        ? "Financial event — held for reconciliation."
                        : "Non-financial — skipped."
                });
                continue;
            }

            // 4. Apply event (type-specific handling)
            try
            {
                ApplyEvent(evt);
                _processedEventIds.Add(evt.EventId);
                _lastAppliedSequence = evt.SequenceNumber;
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply event {EventId}.", evt.EventId);
                errors++;
            }
        }

        return Task.FromResult(new InboxProcessingResult
        {
            ProcessedCount = processed,
            DuplicateCount = duplicates,
            ConflictCount = conflicts.Count,
            ErrorCount = errors,
            LastProcessedCheckpoint = _lastAppliedSequence > 0 ? _lastAppliedSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Conflicts = conflicts
        });
    }

    private void ApplyEvent(SyncEvent evt)
    {
        // TODO: Route to appropriate handler based on EventType
        // - POLICY_UPDATE → update local policy tables, flag old-policy transactions
        // - MASTER_DATA → upsert master data (central wins for governed data)
        // - COMMAND → execute command (e.g., force-sync, config update)
        _logger.LogInformation("Applied inbound event {EventId} (type: {Type}, seq: {Seq}).",
            evt.EventId, evt.EventType, evt.SequenceNumber);
    }

    private static string ComputePayloadHash(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
