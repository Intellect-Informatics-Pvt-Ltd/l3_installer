using Microsoft.Extensions.Logging;

namespace Nldr.SyncWorker;

/// <summary>
/// Source-generated log messages for Nldr.SyncWorker.
///
/// Same rationale as the installer's LogEvents classes: the ILogger extension overloads box
/// every argument and allocate a params array whether or not the level is enabled (CA1873),
/// and a stable EventId is what lets a diagnosis reference a message without depending on its
/// wording. Harness EventIds live in the 7000-7999 band so they can never be confused with an
/// installer event in a combined log.
/// </summary>
internal static partial class LogEvents
{
    [LoggerMessage(EventId = 7201, Level = LogLevel.Information, Message = "[AckPublisher] Published {EventType} for {PacsId}")]
    public static partial void AckPublished(ILogger logger, string eventType, string pacsId);
}
