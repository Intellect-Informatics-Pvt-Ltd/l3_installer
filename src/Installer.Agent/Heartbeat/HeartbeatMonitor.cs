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
public sealed partial class HeartbeatMonitor : IMonitor
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
        if (payload is null)
        {
            return;
        }

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

    /// <summary>
    /// Identity from the site pack the install kept at <c>&lt;DataRoot&gt;/config/site.epcfg</c>,
    /// never a literal: until 2026-09-13 this reported <c>StateId="AP", DccbId="XYZ"</c> for every
    /// node on earth. A node with no site pack sends nothing at all - a heartbeat that says
    /// "AP" for a Gujarat society is a lie the dashboard would believe.
    /// </summary>
    private HeartbeatPayload? BuildPayload()
    {
        var dataRoot = _installerOptions.Value.DataRoot;
        var sitePath = Path.Combine(dataRoot, "config", "site.epcfg");
        if (!File.Exists(sitePath))
        {
            LogNoSitePack(_logger, sitePath);
            return null;
        }

        string pacsId, stateCode, district;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sitePath), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            pacsId = root.TryGetProperty("pacs_id", out var p) ? p.GetString() ?? "" : "";
            stateCode = root.TryGetProperty("state_code", out var st) ? st.GetString() ?? "" : "";
            district = root.TryGetProperty("district_code", out var d) ? d.GetString() ?? "" : "";
        }
        catch (System.Text.Json.JsonException ex)
        {
            LogBadSitePack(_logger, sitePath, ex.Message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(pacsId) || string.IsNullOrWhiteSpace(stateCode))
        {
            LogBadSitePack(_logger, sitePath, "pacs_id or state_code is empty");
            return null;
        }

        var diskUsage = GetDiskUsagePercent(dataRoot);
        var ledger = ReadPackLedger(Path.Combine(dataRoot, "sync", "pack-ledger.json"));

        return new HeartbeatPayload
        {
            PacsId = pacsId,
            StateId = stateCode,
            DccbId = district,
            BranchId = "",
            OnlineSince = _startedAt,
            LastSyncTimestamp = ledger.lastProduced,
            PendingOutboxCount = 0,
            PendingFilesCount = 0,
            DiskUsagePercent = diskUsage,
            StackVersion = ReadInstalledVersion(),
            SchemaVersion = 0,
            LastBackupAt = null,
            HealthStatus = "Live",
            ConnectivityMode = "unknown",
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds
        };
    }

    /// <summary>The last ledger pack this node produced, from the pack ledger (ADR-0011); null when none.</summary>
    private static (DateTimeOffset? lastProduced, long lastSeq) ReadPackLedger(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, 0);
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Produced", out var produced) && produced.TryGetProperty("Ledger", out var ledger))
            {
                var at = ledger.TryGetProperty("At", out var atEl) && atEl.ValueKind == System.Text.Json.JsonValueKind.String ? atEl.GetDateTimeOffset() : (DateTimeOffset?)null;
                var seq = ledger.TryGetProperty("LastSeq", out var seqEl) ? seqEl.GetInt64() : 0;
                return (at, seq);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // A heartbeat must not fail because the ledger is mid-write; the next one reads it.
        }

        return (null, 0);
    }

    /// <summary>The version 'current' points at, by its directory name; "unknown" rather than a literal.</summary>
    private string ReadInstalledVersion()
    {
        try
        {
            var current = Path.Combine(_installerOptions.Value.BinaryRoot, "current");
            var info = new DirectoryInfo(current);
            var target = info.LinkTarget ?? (info.Exists ? info.FullName : null);
            return target is null ? "unknown" : Path.GetFileName(target.TrimEnd('/', '\\'));
        }
        catch (IOException)
        {
            return "unknown";
        }
    }

    [LoggerMessage(EventId = 6110, Level = LogLevel.Warning, Message = "No site pack at {Path}; no heartbeat is sent, because a heartbeat with a guessed identity would be believed.")]
    private static partial void LogNoSitePack(ILogger logger, string path);

    [LoggerMessage(EventId = 6111, Level = LogLevel.Error, Message = "Site pack at {Path} is unusable ({Why}); no heartbeat is sent.")]
    private static partial void LogBadSitePack(ILogger logger, string path, string why);

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
