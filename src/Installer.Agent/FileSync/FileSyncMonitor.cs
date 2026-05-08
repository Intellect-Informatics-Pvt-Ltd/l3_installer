using System.Security.Cryptography;
using Installer.Agent.Monitors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Agent.FileSync;

/// <summary>
/// Monitors the attachments directory for new/modified files and syncs them
/// to NLDR using content-hash-based deduplication.
///
/// Feature is enable/disable controlled via configuration.
/// When disabled, files accumulate locally and the registry tracks them.
/// When enabled, the backlog drains automatically.
/// </summary>
public sealed class FileSyncMonitor : IMonitor
{
    private readonly IOptions<FileSyncOptions> _fileSyncOptions;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly ILogger<FileSyncMonitor> _logger;

    public FileSyncMonitor(
        IOptions<FileSyncOptions> fileSyncOptions,
        IOptions<InstallerOptions> installerOptions,
        ILogger<FileSyncMonitor> logger)
    {
        _fileSyncOptions = fileSyncOptions;
        _installerOptions = installerOptions;
        _logger = logger;
    }

    public string Name => "FileSync";
    public int IntervalSeconds => _fileSyncOptions.Value.ScanIntervalSeconds;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var options = _fileSyncOptions.Value;

        if (!options.Enabled)
        {
            _logger.LogInformation("File sync is disabled. Skipping scan cycle.");
            return;
        }

        var attachmentsDir = Path.Combine(_installerOptions.Value.DataRoot, "attachments");

        if (!Directory.Exists(attachmentsDir))
        {
            _logger.LogInformation("Attachments directory does not exist. Nothing to sync.");
            return;
        }

        _logger.LogInformation("Starting file sync scan of {Dir}.", attachmentsDir);

        // Scan for files that need syncing
        var filesToSync = await ScanForPendingFilesAsync(attachmentsDir, cancellationToken);

        if (filesToSync.Count == 0)
        {
            _logger.LogInformation("No pending files to sync.");
            return;
        }

        _logger.LogInformation("Found {Count} files pending sync. Total size: {SizeMb:F1} MB.",
            filesToSync.Count,
            filesToSync.Sum(f => f.SizeBytes) / (1024.0 * 1024.0));

        // Respect batch size limit
        var batchSizeLimit = options.MaxUploadBatchSizeMb * 1024L * 1024L;
        var batch = new List<FileSyncEntry>();
        long batchSize = 0;

        foreach (var file in filesToSync.OrderBy(f => f.SizeBytes)) // Small files first (photos before reports)
        {
            if (batchSize + file.SizeBytes > batchSizeLimit)
            {
                break;
            }

            batch.Add(file);
            batchSize += file.SizeBytes;
        }

        _logger.LogInformation("Processing batch of {Count} files ({SizeMb:F1} MB).",
            batch.Count, batchSize / (1024.0 * 1024.0));

        // TODO: Upload via IFileSyncTransport (HTTPS or SFTP based on config)
        // For now, log what would be synced
        foreach (var file in batch)
        {
            _logger.LogInformation("Would sync: {Path} ({Hash}, {SizeKb:F0} KB).",
                file.RelativePath, file.ContentHash[..12] + "...", file.SizeBytes / 1024.0);
        }
    }

    private async Task<List<FileSyncEntry>> ScanForPendingFilesAsync(string attachmentsDir, CancellationToken ct)
    {
        var pending = new List<FileSyncEntry>();
        var files = Directory.GetFiles(attachmentsDir, "*.*", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(filePath);

            // Skip very large files during peak hours
            if (IsPeakHours() && fileInfo.Length > _fileSyncOptions.Value.MaxHashFileSizeMbDuringPeakHours * 1024L * 1024L)
            {
                continue;
            }

            var contentHash = await ComputeFileHashAsync(filePath, ct);
            var relativePath = Path.GetRelativePath(attachmentsDir, filePath);

            // TODO: Check against file_sync_registry in MySQL
            // For now, treat all files as pending
            pending.Add(new FileSyncEntry
            {
                LocalPath = filePath,
                RelativePath = relativePath,
                ContentHash = contentHash,
                SizeBytes = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc
            });
        }

        return pending;
    }

    private bool IsPeakHours()
    {
        var now = DateTime.Now.TimeOfDay;
        var options = _fileSyncOptions.Value;

        if (TimeSpan.TryParse(options.PeakHoursStart, out var start) &&
            TimeSpan.TryParse(options.PeakHoursEnd, out var end))
        {
            return now >= start && now <= end;
        }

        return false;
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

/// <summary>
/// Represents a file entry pending synchronization.
/// </summary>
internal sealed record FileSyncEntry
{
    public required string LocalPath { get; init; }
    public required string RelativePath { get; init; }
    public required string ContentHash { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModified { get; init; }
}
