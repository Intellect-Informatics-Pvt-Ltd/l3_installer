using Microsoft.Extensions.Logging;

namespace Pacs.SyncWorker;

/// <summary>
/// Source-generated log messages for Pacs.SyncWorker.
///
/// Same rationale as the installer's LogEvents classes: the ILogger extension overloads box
/// every argument and allocate a params array whether or not the level is enabled (CA1873),
/// and a stable EventId is what lets a diagnosis reference a message without depending on its
/// wording. Harness EventIds live in the 7000-7999 band so they can never be confused with an
/// installer event in a combined log.
/// </summary>
internal static partial class LogEvents
{
    [LoggerMessage(EventId = 7101, Level = LogLevel.Information, Message = "[InboundConsumer] Subscribed to {Acks}, {Cmds}")]
    public static partial void SubscribedToTopics(ILogger logger, string acks, string cmds);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Information, Message = "[InboundConsumer] ACK processed: eventId={EventId} status={Status} seq={Seq}")]
    public static partial void AckProcessed(ILogger logger, string eventId, string status, long seq);

    [LoggerMessage(EventId = 7110, Level = LogLevel.Information, Message = "[OutboundRelay] Published seq={Seq} eventId={EventId}")]
    public static partial void EnvelopePublished(ILogger logger, long seq, string eventId);
}
