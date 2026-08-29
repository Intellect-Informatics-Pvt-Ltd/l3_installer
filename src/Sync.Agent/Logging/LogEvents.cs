using Microsoft.Extensions.Logging;

namespace Sync.Agent;

/// <summary>
/// Source-generated log messages for Sync.Agent.
///
/// WHY THESE ARE NOT PLAIN ILogger CALLS. Two reasons, and the second is the one that matters
/// for this product.
///
/// 1. Cost. The ILogger extension overloads take <c>params object?[]</c>, so every argument is
///    boxed and an array allocated whether or not the level is enabled. CA1873 flags this. The
///    generator emits an IsEnabled guard, so a disabled level costs a branch.
///
/// 2. Stable EventIds. An installer is diagnosed from a support bundle by someone who was not
///    there. A stable, documented EventId is what lets a support engineer grep a bundle for
///    "what happened at the junction flip" without knowing the message wording, and it survives
///    message text being reworded or translated. Treat an EventId as an interface: reuse is
///    forbidden, retirement is fine, renumbering breaks every runbook that cites it.
///
/// EventId ranges across the product:
///   1000-1099  Installer.Core        (state machine, mode detection)
///   2000-2099  Installer.Actions     prechecks
///   2100-2199  Installer.Actions     install
///   2200-2299  Installer.Actions     uninstall
///   2300-2399  Installer.Actions     topology
///   2400-2499  Installer.Actions     harness integration
///   2700-2799  SharedKernel          audit chain
///   2800-2899  SharedKernel          secret store
///   3000-3099  BackupRestore
///   4000-4099  SupportBundle
///   5000-5099  Sync.Agent
/// </summary>
internal static partial class LogEvents
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Delete event capture disabled. Skipping sync for {EntityType}/{EntityId}.")]
    public static partial void DeleteCaptureDisabled(ILogger logger, string entityType, string entityId);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Deletion sync event recorded: {EntityType}/{EntityId} by {DeletedBy}. Reason: {Reason}.")]
    public static partial void DeletionSyncRecorded(ILogger logger, string entityType, string entityId, string deletedBy, string reason);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Amendment event capture disabled. Skipping sync for {EntityType}/{EntityId}.")]
    public static partial void AmendmentCaptureDisabled(ILogger logger, string entityType, string entityId);

    [LoggerMessage(EventId = 5010, Level = LogLevel.Information, Message = "Duplicate event {EventId} - ACK without applying.")]
    public static partial void DuplicateEventAcked(ILogger logger, string eventId);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Information, Message = "Applied inbound event {EventId} (type: {Type}, seq: {Seq}).")]
    public static partial void InboundEventApplied(ILogger logger, string eventId, string type, long seq);

    [LoggerMessage(EventId = 5020, Level = LogLevel.Information, Message = "Outbox drain cycle: batch size {BatchSize}. (Implementation pending MySQL/Kafka integration).")]
    public static partial void OutboxDrainCycle(ILogger logger, int batchSize);

    [LoggerMessage(EventId = 5030, Level = LogLevel.Information, Message = "NLDR disconnected. Pending outbox events: {Count}. Business operations unaffected.")]
    public static partial void NldrDisconnected(ILogger logger, long count);
}
