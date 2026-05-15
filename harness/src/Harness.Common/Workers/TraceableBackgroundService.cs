#pragma warning disable CA1848 // Use LoggerMessage delegates — base class error logging is a thin adapter
using Harness.Common.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Common.Workers;

/// <summary>
/// Base class for all harness background services.
/// Mirrors <c>Intellect.Erp.Observability.Propagation.TraceableBackgroundService</c>.
/// Each work-item executes within a structured log scope so correlation IDs
/// are propagated automatically.
/// </summary>
public abstract class TraceableBackgroundService(ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // yield so the host starts without blocking

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[{Service}] Unhandled exception in work cycle — will retry",
                    GetType().Name);

                // Back off before retrying to avoid tight error loops
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Executes one work cycle (poll, process, sleep).
    /// Implementations should handle their own back-off when idle.
    /// </summary>
    protected abstract Task RunCycleAsync(CancellationToken ct);
}
