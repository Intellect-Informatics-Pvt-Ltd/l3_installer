using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Agent.Monitors;

/// <summary>
/// Enforces log rotation policy: deletes logs beyond retention, compresses old logs.
/// All retention periods and thresholds are configurable via LogRotationOptions.
/// </summary>
public sealed class LogRotationMonitor : IMonitor
{
    private readonly IOptions<LogRotationOptions> _rotationOptions;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly ILogger<LogRotationMonitor> _logger;

    public LogRotationMonitor(
        IOptions<LogRotationOptions> rotationOptions,
        IOptions<InstallerOptions> installerOptions,
        ILogger<LogRotationMonitor> logger)
    {
        _rotationOptions = rotationOptions;
        _installerOptions = installerOptions;
        _logger = logger;
    }

    public string Name => "LogRotation";
    public int IntervalSeconds => 86400; // Daily (actual scheduling uses RotationTime from config)

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var logsRoot = Path.Combine(_installerOptions.Value.DataRoot, "logs");
        var options = _rotationOptions.Value;

        if (!Directory.Exists(logsRoot))
        {
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        var totalDeleted = 0;
        var totalCompressed = 0;

        // Process each service log directory
        foreach (var serviceDir in Directory.GetDirectories(logsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var serviceName = Path.GetFileName(serviceDir);
            var retentionDays = GetRetentionDays(serviceName, options);

            var logFiles = Directory.GetFiles(serviceDir, "*.json")
                .Concat(Directory.GetFiles(serviceDir, "*.log"))
                .ToList();

            foreach (var logFile in logFiles)
            {
                var fileAge = now - File.GetLastWriteTimeUtc(logFile);

                // Delete if beyond retention
                if (fileAge.TotalDays > retentionDays)
                {
                    try
                    {
                        File.Delete(logFile);
                        totalDeleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete expired log: {File}.", logFile);
                    }
                    continue;
                }

                // Compress if older than CompressAfterDays and not already compressed
                if (fileAge.TotalDays > options.CompressAfterDays && !logFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        CompressFile(logFile);
                        totalCompressed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to compress log: {File}.", logFile);
                    }
                }
            }

            // Also clean up .gz files beyond retention
            foreach (var gzFile in Directory.GetFiles(serviceDir, "*.gz"))
            {
                var fileAge = now - File.GetLastWriteTimeUtc(gzFile);
                if (fileAge.TotalDays > retentionDays)
                {
                    try { File.Delete(gzFile); totalDeleted++; }
                    catch { /* best effort */ }
                }
            }
        }

        if (totalDeleted > 0 || totalCompressed > 0)
        {
            _logger.LogInformation("Log rotation complete. Deleted: {Deleted}, Compressed: {Compressed}.",
                totalDeleted, totalCompressed);
        }

        return Task.CompletedTask;
    }

    private static int GetRetentionDays(string serviceName, LogRotationOptions options)
    {
        return serviceName.ToLowerInvariant() switch
        {
            "mysql" => options.MySqlRetentionDays,
            "audit" => options.AuditRetentionDays,
            _ => options.ApplicationRetentionDays
        };
    }

    private static void CompressFile(string filePath)
    {
        var compressedPath = filePath + ".gz";
        using var sourceStream = File.OpenRead(filePath);
        using var destStream = File.Create(compressedPath);
        using var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal);
        sourceStream.CopyTo(gzipStream);

        // Delete original after successful compression
        File.Delete(filePath);
    }
}
