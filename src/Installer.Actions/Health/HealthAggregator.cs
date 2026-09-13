using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Installer.Actions.Database;
using Installer.Actions.Install;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Actions.Health;

/// <summary>
/// What the node can prove about each service after start, in the service map's own terms.
///
/// THREE VERDICTS, NOT TWO. A service whose map entry carries an <c>http</c> check is
/// <b>Healthy</b> when the endpoint answers the expected status, and <b>Failed</b> otherwise. A
/// service whose entry carries only a <c>tcp</c> check — 20 of the 27 L2-R2 services today,
/// because they map no health route (G31) — can be shown to be <b>Listening</b> and nothing
/// more, and the verdict says so rather than upgrading "accepted a connection" to "healthy".
/// The install succeeds on Listening; it does not succeed on Failed; and the summary counts
/// each verdict separately so the number of services that are merely listening is a line in
/// the install log, not a discovery three weeks later.
///
/// Each check is retried across a window (the map's <c>timeout_seconds</c>, at least the
/// configured floor), because a .NET service that has been started is not yet listening — it is
/// JIT-compiling, opening a connection pool and warming a cache. The window is per service and
/// the checks run in parallel, so 27 services cost one window, not 27.
/// </summary>
public sealed class HealthAggregator : IHealthAggregator
{
    private readonly IProcessRunner _runner;
    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ISiteTokenSource _site;
    private readonly ILogger<HealthAggregator> _logger;
    private readonly HttpClient _http;
    private readonly TimeSpan _pollInterval;

    public HealthAggregator(
        IProcessRunner runner,
        IOptions<InstallerOptions> options,
        IOptions<ServicesOptions> services,
        ISiteTokenSource site,
        ILogger<HealthAggregator> logger,
        HttpClient? http = null,
        TimeSpan? pollInterval = null)
    {
        _runner = runner;
        _options = options;
        _services = services;
        _site = site;
        _logger = logger;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public async Task<HealthReport> VerifyAsync(IReadOnlyList<ServiceMapEntry> services, TimeSpan minimumWindow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var tokens = InstallerTokenMap.Merge(InstallerTokenMap.BuildInfrastructure(_options.Value, _services.Value), _site.Tokens);

        var tasks = services.Select(s => CheckWithRetryAsync(s, tokens, minimumWindow, cancellationToken)).ToList();
        var verdicts = await Task.WhenAll(tasks);

        var report = new HealthReport { Verdicts = verdicts };
        LogEvents.HealthSummary(_logger, report.Healthy, report.Listening, report.Failed, services.Count);
        return report;
    }

    private async Task<ServiceHealthVerdict> CheckWithRetryAsync(
        ServiceMapEntry service, IReadOnlyDictionary<string, string> tokens, TimeSpan minimumWindow, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(Math.Max(service.HealthCheck.TimeoutSeconds, minimumWindow.TotalSeconds));
        var clock = Stopwatch.StartNew();
        ServiceHealthVerdict last;
        do
        {
            last = await CheckOnceAsync(service, tokens, ct);
            if (last.State != HealthState.Failed)
            {
                LogEvents.HealthVerdict(_logger, service.Name, last.State, last.Detail);
                return last with { Elapsed = clock.Elapsed };
            }

            await Task.Delay(_pollInterval, ct);
        }
        while (clock.Elapsed < window);

        LogEvents.HealthVerdict(_logger, service.Name, last.State, last.Detail);
        return last with { Elapsed = clock.Elapsed };
    }

    private async Task<ServiceHealthVerdict> CheckOnceAsync(ServiceMapEntry service, IReadOnlyDictionary<string, string> tokens, CancellationToken ct)
    {
        var hc = service.HealthCheck;
        string Resolve(string? s) => s is null ? "" : InstallerTokenMap.Resolve(s, tokens, $"health check of {service.Name}");

        try
        {
            switch (hc.Type.ToLowerInvariant())
            {
                case "http":
                {
                    var url = Resolve(hc.Url);
                    using var response = await _http.GetAsync(url, ct);
                    var status = (int)response.StatusCode;
                    return status == hc.ExpectedStatus
                        ? new ServiceHealthVerdict(service.Name, HealthState.Healthy, $"{url} answered {status}")
                        : new ServiceHealthVerdict(service.Name, HealthState.Failed, $"{url} answered {status}, expected {hc.ExpectedStatus}");
                }

                case "tcp":
                {
                    var host = string.IsNullOrWhiteSpace(hc.Host) ? "127.0.0.1" : Resolve(hc.Host);
                    var port = int.Parse(Resolve(hc.Port), CultureInfo.InvariantCulture);
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Math.Min(hc.TimeoutSeconds, 5))));
                    await client.ConnectAsync(host, port, timeout.Token);
                    return new ServiceHealthVerdict(service.Name, HealthState.Listening,
                        $"{host}:{port} accepts connections. The service maps no health route, so this is LISTENING, not known healthy (G31).");
                }

                case "command":
                {
                    var exe = Resolve(hc.Command).Replace('\\', '/');
                    var args = Resolve(hc.Arguments);
                    var result = await _runner.RunAsync(exe, args, cancellationToken: ct);
                    return result.ExitCode == hc.SuccessExitCode
                        ? new ServiceHealthVerdict(service.Name, HealthState.Healthy, $"`{Path.GetFileName(exe)} {args}` exited {result.ExitCode}")
                        : new ServiceHealthVerdict(service.Name, HealthState.Failed, $"`{Path.GetFileName(exe)} {args}` exited {result.ExitCode}, expected {hc.SuccessExitCode}: {result.CombinedOutput.Trim()}");
                }

                default:
                    return new ServiceHealthVerdict(service.Name, HealthState.Failed, $"unknown health check type '{hc.Type}' - the map is wrong, not the service");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ServiceHealthVerdict(service.Name, HealthState.Failed, "timed out");
        }
        catch (HttpRequestException ex)
        {
            return new ServiceHealthVerdict(service.Name, HealthState.Failed, ex.Message);
        }
        catch (SocketException ex)
        {
            return new ServiceHealthVerdict(service.Name, HealthState.Failed, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // An unresolved token in the check itself: the map is wrong. Said so, not retried.
            return new ServiceHealthVerdict(service.Name, HealthState.Failed, ex.Message);
        }
    }
}
