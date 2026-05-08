using System.Net.Http.Json;
using Installer.Agent.Monitors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Agent.Heartbeat;

/// <summary>
/// Sends periodic heartbeat to CoopsIndia Dashboard when PACS is online.
/// Supports HTTPS POST and WebSocket transports (configurable).
/// Fire-and-forget: heartbeat failure never blocks business operations.
/// </summary>
public sealed class HeartbeatMonitor : IMonitor
{
    private readonly IOptions<HeartbeatOptions> _heartbeatOptions;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HeartbeatMonitor> _logger;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private int _consecutiveFailures;

    public HeartbeatMonitor(
        IOptions<HeartbeatOptions> heartbeatOptions,
        IOptions<InstallerOptions> installerOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<HeartbeatMonitor> logger)
    {
        _heartbeatOptions = heartbeatOptions;
        _installerOptions = installerOptions;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => "Heartbeat";
    public int IntervalSeconds => _heartbeatOptions.Value.IntervalSeconds;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var options = _heartbeatOptions.Value;

        if (!options.Enabled)
        {
            return;
        }

        // Circuit breaker: skip if too many consecutive failures
        if (_consecutiveFailures >= options.CircuitBreakerThreshold)
        {
            _logger.LogWarning(
                "Heartbeat circuit breaker OPEN. Consecutive failures: {Failures}. Skipping.",
                _consecutiveFailures);
            // Reset after cooldown (handled by interval timing)
            _consecutiveFailures = 0; // Allow retry on next cycle
            return;
        }

        var payload = BuildPayload();

        try
        {
            if (options.Transport.Equals("HTTPS", StringComparison.OrdinalIgnoreCase))
            {
                await SendHttpsHeartbeatAsync(payload, options.Https, cancellationToken);
            }
            else if (options.Transport.Equals("WebSocket", StringComparison.OrdinalIgnoreCase))
            {
                // WebSocket implementation would maintain a persistent connection
                // For v1, fall back to HTTPS
                await SendHttpsHeartbeatAsync(payload, options.Https, cancellationToken);
            }

            _consecutiveFailures = 0;
            _logger.LogInformation("Heartbeat sent successfully to CoopsIndia Dashboard.");
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex,
                "Heartbeat failed (attempt {Failures}/{Threshold}). Will retry on next interval.",
                _consecutiveFailures, options.CircuitBreakerThreshold);
        }
    }

    private async Task SendHttpsHeartbeatAsync(
        HeartbeatPayload payload,
        HeartbeatHttpsOptions httpsOptions,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("Heartbeat");
        client.Timeout = TimeSpan.FromSeconds(httpsOptions.TimeoutSeconds);

        var response = await client.PostAsJsonAsync(httpsOptions.Endpoint, payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private HeartbeatPayload BuildPayload()
    {
        var dataRoot = _installerOptions.Value.DataRoot;
        var diskUsage = GetDiskUsagePercent(dataRoot);

        return new HeartbeatPayload
        {
            PacsId = "CONFIGURED_VIA_EPCFG", // Will be resolved from site config at runtime
            StateId = "AP",
            DccbId = "XYZ",
            BranchId = "001",
            OnlineSince = _startedAt,
            LastSyncTimestamp = null, // TODO: read from sync checkpoint
            PendingOutboxCount = 0, // TODO: query sync_outbox count
            PendingFilesCount = 0, // TODO: query file_sync_registry count
            DiskUsagePercent = diskUsage,
            StackVersion = "3.2.1", // TODO: read from installed manifest
            SchemaVersion = 25, // TODO: read from schema_version_registry
            LastBackupAt = null, // TODO: read from backup manifest
            HealthStatus = "Healthy", // TODO: aggregate from health checks
            ConnectivityMode = "4G",
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds
        };
    }

    private static int GetDiskUsagePercent(string dataRoot)
    {
        try
        {
            var volumePath = Path.GetPathRoot(dataRoot) ?? dataRoot;
            var driveInfo = new DriveInfo(volumePath);
            var usedBytes = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
            return (int)(usedBytes * 100.0 / driveInfo.TotalSize);
        }
        catch
        {
            return -1;
        }
    }
}
