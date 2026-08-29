using Microsoft.Extensions.Logging;

namespace Installer.Agent;

/// <summary>
/// Source-generated log messages for the always-on Installer Agent.
/// See <c>src/Installer.Actions/Logging/LogEvents.cs</c> for why these are not plain ILogger
/// calls and for the product-wide EventId map. This project owns <b>6000-6099</b>:
///   6001       agent lifecycle
///   6010-6019  disk monitoring
///   6020-6029  configuration drift
///   6030-6039  log rotation
///   6040-6049  file sync
///
/// The agent runs unattended for the life of the installation and is often the only thing that
/// logged anything before a support bundle was taken, so its EventIds are the ones most likely
/// to be cited in a runbook. Do not renumber them.
/// </summary>
internal static partial class LogEvents
{
    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "ePACS Installer Agent starting. Monitors: {Count}.")]
    public static partial void AgentStarting(ILogger logger, int count);

    [LoggerMessage(EventId = 6010, Level = LogLevel.Critical, Message = "DISK CRITICAL: Data volume {Volume} at {FreePercent}% free ({FreeGb:F1} GB). Threshold: {Threshold}%.")]
    public static partial void DiskCritical(ILogger logger, string volume, int freePercent, double freeGb, int threshold);

    [LoggerMessage(EventId = 6011, Level = LogLevel.Information, Message = "Disk space OK: {Volume} at {FreePercent}% free ({FreeGb:F1} GB).")]
    public static partial void DiskOk(ILogger logger, string volume, int freePercent, double freeGb);

    [LoggerMessage(EventId = 6020, Level = LogLevel.Information, Message = "Config drift baseline captured. {Count} files tracked.")]
    public static partial void DriftBaselineCaptured(ILogger logger, int count);

    [LoggerMessage(EventId = 6021, Level = LogLevel.Information, Message = "Config drift check passed. No drift detected in {Count} files.")]
    public static partial void DriftCheckPassed(ILogger logger, int count);

    [LoggerMessage(EventId = 6030, Level = LogLevel.Information, Message = "Log rotation complete. Deleted: {Deleted}, Compressed: {Compressed}.")]
    public static partial void LogRotationComplete(ILogger logger, int deleted, int compressed);

    [LoggerMessage(EventId = 6040, Level = LogLevel.Information, Message = "Starting file sync scan of {Dir}.")]
    public static partial void FileSyncScanStarting(ILogger logger, string dir);

    [LoggerMessage(EventId = 6041, Level = LogLevel.Information, Message = "Found {Count} files pending sync. Total size: {SizeMb:F1} MB.")]
    public static partial void FileSyncPending(ILogger logger, int count, double sizeMb);

    [LoggerMessage(EventId = 6042, Level = LogLevel.Information, Message = "Processing batch of {Count} files ({SizeMb:F1} MB).")]
    public static partial void FileSyncBatch(ILogger logger, int count, double sizeMb);

    [LoggerMessage(EventId = 6043, Level = LogLevel.Information, Message = "Would sync: {Path} ({Hash}, {SizeKb:F0} KB).")]
    public static partial void FileSyncWouldSync(ILogger logger, string path, string hash, double sizeKb);
}
