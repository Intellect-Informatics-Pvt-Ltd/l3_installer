using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Installer.Actions.Install;

/// <summary>
/// Runs HTTP health checks against all harness services after installation.
/// Each service must respond 200 on its /health/ready endpoint within the timeout.
///
/// The smoke test retries each service up to 3 times with a 5-second delay between
/// attempts to allow for service startup time.
/// </summary>
public sealed class HarnessSmokeTest : IHarnessSmokeTest
{
    private readonly ILogger<HarnessSmokeTest> _logger;
    private readonly HttpClient _httpClient;

    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// PACS-side health endpoints (always checked).
    /// </summary>
    private static readonly (string Name, string Url)[] PacsEndpoints =
    [
        ("ePACS.Harness.FasApi", "http://127.0.0.1:5101/health/ready"),
        ("ePACS.Harness.LoansApi", "http://127.0.0.1:5102/health/ready"),
        ("ePACS.Harness.SyncWorker", "http://127.0.0.1:5103/health/ready"),
        ("ePACS.Harness.OperatorUi", "http://127.0.0.1:5301/health/live"),
    ];

    /// <summary>
    /// NLDR-side health endpoints (checked only in demo mode).
    /// </summary>
    private static readonly (string Name, string Url)[] NldrEndpoints =
    [
        ("ePACS.Harness.NldrApi", "http://127.0.0.1:5201/health/ready"),
        ("ePACS.Harness.NldrSyncWorker", "http://127.0.0.1:5203/health/ready"),
        ("ePACS.Harness.NldrDashboard", "http://127.0.0.1:5401/health/live"),
    ];

    public HarnessSmokeTest(ILogger<HarnessSmokeTest> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = RequestTimeout };
    }

    public async Task<HarnessSmokeResult> RunAsync(bool demoMode = false, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var endpoints = demoMode
            ? PacsEndpoints.Concat(NldrEndpoints).ToArray()
            : PacsEndpoints;

        _logger.LogInformation(
            "Running harness smoke test against {Count} services (demo={Demo}).",
            endpoints.Length, demoMode);

        var results = new List<ServiceSmokeStatus>();

        foreach (var (name, url) in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await CheckServiceAsync(name, url, cancellationToken);
            results.Add(status);

            var icon = status.Healthy ? "✓" : "✗";
            _logger.LogInformation(
                "  {Icon} {Service}: {Status} ({ResponseMs}ms)",
                icon, name, status.Healthy ? "healthy" : status.Error ?? "unhealthy",
                (int)status.ResponseTime.TotalMilliseconds);
        }

        sw.Stop();
        var allHealthy = results.All(r => r.Healthy);

        if (allHealthy)
        {
            _logger.LogInformation("Harness smoke test PASSED in {Duration}ms.", (int)sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            var failed = results.Where(r => !r.Healthy).Select(r => r.ServiceName);
            _logger.LogError(
                "Harness smoke test FAILED. Unhealthy services: {Services}.",
                string.Join(", ", failed));
        }

        return new HarnessSmokeResult
        {
            AllHealthy = allHealthy,
            Services = results,
            Duration = sw.Elapsed
        };
    }

    private async Task<ServiceSmokeStatus> CheckServiceAsync(
        string name, string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await _httpClient.GetAsync(url, ct);
                sw.Stop();

                if ((int)response.StatusCode == 200)
                {
                    return new ServiceSmokeStatus
                    {
                        ServiceName = name,
                        HealthUrl = url,
                        Healthy = true,
                        HttpStatus = (int)response.StatusCode,
                        ResponseTime = sw.Elapsed
                    };
                }

                if (attempt < MaxRetries)
                {
                    _logger.LogDebug(
                        "  {Service} returned {Status} on attempt {Attempt}/{Max}. Retrying...",
                        name, (int)response.StatusCode, attempt, MaxRetries);
                    await Task.Delay(RetryDelay, ct);
                }
                else
                {
                    return new ServiceSmokeStatus
                    {
                        ServiceName = name,
                        HealthUrl = url,
                        Healthy = false,
                        HttpStatus = (int)response.StatusCode,
                        Error = $"HTTP {(int)response.StatusCode}",
                        ResponseTime = sw.Elapsed
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                sw.Stop();

                if (attempt < MaxRetries)
                {
                    _logger.LogDebug(
                        "  {Service} unreachable on attempt {Attempt}/{Max}: {Error}. Retrying...",
                        name, attempt, MaxRetries, ex.Message);
                    await Task.Delay(RetryDelay, ct);
                }
                else
                {
                    return new ServiceSmokeStatus
                    {
                        ServiceName = name,
                        HealthUrl = url,
                        Healthy = false,
                        Error = ex.Message,
                        ResponseTime = sw.Elapsed
                    };
                }
            }
        }

        // Should not reach here, but satisfy compiler
        return new ServiceSmokeStatus
        {
            ServiceName = name,
            HealthUrl = url,
            Healthy = false,
            Error = "Max retries exceeded"
        };
    }
}
