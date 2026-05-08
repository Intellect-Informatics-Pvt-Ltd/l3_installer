using Installer.Agent.Monitors;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Agent;

/// <summary>
/// The ePACS Installer Agent — an always-on worker service that orchestrates
/// health monitoring, disk space monitoring, configuration drift detection,
/// log rotation, and support bundle generation.
///
/// Each monitor runs on its own configurable interval.
/// Extends BackgroundService with resilient error handling (continues on failure).
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly IEnumerable<IMonitor> _monitors;
    private readonly IOptions<MonitoringOptions> _monitoringOptions;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IEnumerable<IMonitor> monitors,
        IOptions<MonitoringOptions> monitoringOptions,
        ILogger<Worker> logger)
    {
        _monitors = monitors;
        _monitoringOptions = monitoringOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ePACS Installer Agent starting. Monitors: {Count}.",
            _monitors.Count());

        // Track last execution time per monitor
        var lastExecution = new Dictionary<string, DateTime>();
        foreach (var monitor in _monitors)
        {
            lastExecution[monitor.Name] = DateTime.MinValue;
        }

        // Main loop — check each monitor's interval and execute if due
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            foreach (var monitor in _monitors)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                var elapsed = (now - lastExecution[monitor.Name]).TotalSeconds;
                if (elapsed < monitor.IntervalSeconds)
                {
                    continue;
                }

                try
                {
                    await monitor.ExecuteAsync(stoppingToken);
                    lastExecution[monitor.Name] = now;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return; // Graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Monitor {MonitorName} failed. Will retry on next interval.",
                        monitor.Name);
                    lastExecution[monitor.Name] = now; // Don't retry immediately
                }
            }

            // Sleep for the shortest monitor interval (minimum 10 seconds)
            var minInterval = _monitors.Any()
                ? Math.Max(10, _monitors.Min(m => m.IntervalSeconds))
                : 60;

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(minInterval, 30)), stoppingToken);
        }

        _logger.LogInformation("ePACS Installer Agent stopping.");
    }
}
