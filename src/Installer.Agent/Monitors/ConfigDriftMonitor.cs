using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Agent.Monitors;

/// <summary>
/// Detects configuration drift by comparing SHA-256 hashes of config files
/// against their expected values stored at install/upgrade time.
/// Does NOT auto-remediate — flags drift for operator review.
/// </summary>
public sealed class ConfigDriftMonitor : IMonitor
{
    private readonly IOptions<MonitoringOptions> _monitoringOptions;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly ILogger<ConfigDriftMonitor> _logger;

    // In-memory hash store (in production, this would be persisted to installation_registry)
    private readonly Dictionary<string, string> _expectedHashes = new(StringComparer.OrdinalIgnoreCase);

    public ConfigDriftMonitor(
        IOptions<MonitoringOptions> monitoringOptions,
        IOptions<InstallerOptions> installerOptions,
        ILogger<ConfigDriftMonitor> logger)
    {
        _monitoringOptions = monitoringOptions;
        _installerOptions = installerOptions;
        _logger = logger;
    }

    public string Name => "ConfigDrift";
    public int IntervalSeconds => _monitoringOptions.Value.DriftCheckIntervalSeconds;

    /// <summary>
    /// Registers a config file's expected hash (called after install/upgrade).
    /// </summary>
    public void RegisterExpectedHash(string filePath, string sha256Hash)
    {
        _expectedHashes[filePath] = sha256Hash;
    }

    /// <summary>
    /// Captures current hashes of all config files in the config directory.
    /// Call this after install/upgrade to establish the baseline.
    /// </summary>
    public async Task CaptureBaselineAsync(CancellationToken cancellationToken = default)
    {
        var configDir = Path.Combine(_installerOptions.Value.DataRoot, "config");
        if (!Directory.Exists(configDir))
        {
            return;
        }

        var configFiles = Directory.GetFiles(configDir, "*.*", SearchOption.AllDirectories);
        foreach (var file in configFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = await ComputeFileHashAsync(file, cancellationToken);
            _expectedHashes[file] = hash;
        }

        _logger.LogInformation("Config drift baseline captured. {Count} files tracked.", _expectedHashes.Count);
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (_expectedHashes.Count == 0)
        {
            _logger.LogInformation("No config baseline established. Skipping drift check.");
            return;
        }

        var driftDetected = false;

        foreach (var (filePath, expectedHash) in _expectedHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Config drift: File missing — {FilePath}.", filePath);
                driftDetected = true;
                continue;
            }

            var currentHash = await ComputeFileHashAsync(filePath, cancellationToken);
            if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Config drift detected: {FilePath}. Expected: {Expected}, Actual: {Actual}.",
                    filePath, expectedHash[..12] + "...", currentHash[..12] + "...");
                driftDetected = true;
            }
        }

        if (!driftDetected)
        {
            _logger.LogInformation("Config drift check passed. No drift detected in {Count} files.",
                _expectedHashes.Count);
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
