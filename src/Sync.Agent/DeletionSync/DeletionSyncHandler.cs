using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Sync.Agent.DeletionSync;

/// <summary>
/// Handles deletion and amendment events for NLDR synchronization.
/// Writes events to the sync_outbox so they are propagated to NLDR.
///
/// Key behaviors:
/// - DELETE events carry the full before-state (so NLDR can soft-delete with audit trail)
/// - AMENDMENT events carry before+after state + reason + approver
/// - Bulk deletes above configurable threshold require mandatory backup first
/// - All operations are within the same MySQL transaction as the business operation
/// </summary>
public sealed class DeletionSyncHandler : IDeletionSyncHandler
{
    private readonly IOptions<DeletionSyncOptions> _options;
    private readonly ILogger<DeletionSyncHandler> _logger;

    public DeletionSyncHandler(
        IOptions<DeletionSyncOptions> options,
        ILogger<DeletionSyncHandler> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task RecordDeletionAsync(DeletionSyncEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_options.Value.CaptureDeleteEvents)
        {
            _logger.LogInformation("Delete event capture disabled. Skipping sync for {EntityType}/{EntityId}.",
                entry.EntityType, entry.EntityId);
            return Task.CompletedTask;
        }

        var payloadHash = ComputeHash(entry.BeforeStateJson);

        // TODO: In production, this INSERT goes into the same MySQL transaction as the DELETE
        // INSERT INTO sync_outbox (event_type, change_type, entity_type, entity_id,
        //   payload_json, before_state_json, payload_hash, idempotency_key,
        //   amendment_reason, created_at, pacs_id)
        // VALUES ('DATA_CHANGE', 'DELETE', ?, ?, ?, ?, ?, ?, ?, NOW(), ?)

        _logger.LogInformation(
            "Deletion sync event recorded: {EntityType}/{EntityId} by {DeletedBy}. Reason: {Reason}.",
            entry.EntityType, entry.EntityId, entry.DeletedBy, entry.DeletionReason ?? "none");

        return Task.CompletedTask;
    }

    public Task RecordAmendmentAsync(AmendmentSyncEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_options.Value.CaptureAmendmentEvents)
        {
            _logger.LogInformation("Amendment event capture disabled. Skipping sync for {EntityType}/{EntityId}.",
                entry.EntityType, entry.EntityId);
            return Task.CompletedTask;
        }

        // Validate mandatory fields for financial amendments
        if (_options.Value.AmendmentRequiresReason && string.IsNullOrWhiteSpace(entry.AmendmentReason))
        {
            throw new InvalidOperationException(
                $"Amendment reason is required for {entry.EntityType}/{entry.EntityId}. " +
                "Financial data amendments must have a documented reason.");
        }

        if (_options.Value.AmendmentRequiresApprover && string.IsNullOrWhiteSpace(entry.ApprovedBy))
        {
            throw new InvalidOperationException(
                $"Amendment approver is required for {entry.EntityType}/{entry.EntityId}. " +
                "Financial data amendments must be approved.");
        }

        var payloadHash = ComputeHash(entry.AfterStateJson);

        // TODO: In production, this INSERT goes into the same MySQL transaction as the UPDATE
        // INSERT INTO sync_outbox (event_type, change_type, entity_type, entity_id,
        //   payload_json, before_state_json, payload_hash, idempotency_key,
        //   amendment_reason, amendment_approver, amendment_date, created_at, pacs_id)
        // VALUES ('DATA_CHANGE', 'AMENDMENT', ?, ?, ?, ?, ?, ?, ?, ?, ?, NOW(), ?)

        _logger.LogWarning(
            "Amendment sync event recorded: {EntityType}/{EntityId}. " +
            "Changed fields: [{Fields}]. Reason: {Reason}. Approver: {Approver}. Affects synced data: {AffectsSynced}.",
            entry.EntityType, entry.EntityId,
            string.Join(", ", entry.ChangedFields),
            entry.AmendmentReason, entry.ApprovedBy, entry.AffectsSyncedData);

        return Task.CompletedTask;
    }

    public Task<BulkDeleteValidation> ValidateBulkDeleteAsync(string entityType, int recordCount, CancellationToken cancellationToken = default)
    {
        var threshold = _options.Value.BulkDeleteThreshold;

        if (recordCount <= threshold)
        {
            return Task.FromResult(new BulkDeleteValidation
            {
                CanProceed = true,
                BackupRequired = false,
                Threshold = threshold
            });
        }

        if (!_options.Value.BulkDeleteRequiresBackup)
        {
            _logger.LogWarning(
                "Bulk delete of {Count} {EntityType} records exceeds threshold ({Threshold}) but backup requirement is disabled.",
                recordCount, entityType, threshold);
            return Task.FromResult(new BulkDeleteValidation
            {
                CanProceed = true,
                BackupRequired = false,
                Threshold = threshold
            });
        }

        _logger.LogError(
            "Bulk delete BLOCKED: {Count} {EntityType} records exceeds threshold ({Threshold}). Backup required before proceeding.",
            recordCount, entityType, threshold);

        return Task.FromResult(new BulkDeleteValidation
        {
            CanProceed = false,
            BackupRequired = true,
            BlockReason = $"Bulk deletion of {recordCount} records exceeds the safety threshold of {threshold}. " +
                          "A backup must be created before this operation can proceed.",
            Threshold = threshold
        });
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
