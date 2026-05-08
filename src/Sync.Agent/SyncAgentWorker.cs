using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using Sync.Agent.Connectivity;
using Sync.Agent.Outbox;

namespace Sync.Agent;

/// <summary>
/// The ePACS Sync Agent — manages bidirectional data synchronization with NLDR.
/// Responsibilities:
/// 1. Outbox relay: MySQL → Kafka (local)
/// 2. Connectivity monitoring: probe NLDR, manage circuit breaker
/// 3. Outbound sync: Kafka → NLDR (when connected)
/// 4. Inbound processing: NLDR → local (commands, policy, master data)
/// 5. Reconciliation: nightly + on-demand
///
/// Business operations are NEVER blocked by sync failures.
/// MySQL outbox is the durable anchor — survives Kafka/network failures.
/// </summary>
public sealed class SyncAgentWorker : BackgroundService
{
    private readonly IOutboxRelay _outboxRelay;
    private readonly ConnectivityMonitor _connectivityMonitor;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ILogger<SyncAgentWorker> _logger;

    public SyncAgentWorker(
        IOutboxRelay outboxRelay,
        ConnectivityMonitor connectivityMonitor,
        IOptions<ServicesOptions> servicesOptions,
        ILogger<SyncAgentWorker> logger)
    {
        _outboxRelay = outboxRelay;
        _connectivityMonitor = connectivityMonitor;
        _servicesOptions = servicesOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ePACS Sync Agent starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Drain outbox to local Kafka (always, regardless of NLDR connectivity)
                await _outboxRelay.DrainAsync(cancellationToken: stoppingToken);

                // 2. Probe NLDR connectivity
                var connected = await _connectivityMonitor.ProbeAsync(stoppingToken);

                if (connected)
                {
                    // 3. Sync outbound events to NLDR
                    await SyncOutboundAsync(stoppingToken);

                    // 4. Process inbound from NLDR
                    await ProcessInboundAsync(stoppingToken);
                }
                else
                {
                    var pending = await _outboxRelay.GetPendingCountAsync(stoppingToken);
                    _logger.LogInformation(
                        "NLDR disconnected. Pending outbox events: {Count}. Business operations unaffected.",
                        pending);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync Agent cycle failed. Will retry on next interval.");
            }

            // Wait before next cycle (configurable interval)
            var intervalSeconds = _servicesOptions.Value.Sync.ChunkSizeBytes > 0 ? 30 : 60;
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }

        _logger.LogInformation("ePACS Sync Agent stopping.");
    }

    private Task SyncOutboundAsync(CancellationToken ct)
    {
        // TODO: Read from Kafka 'epacs.local.sync-ready' topic
        // Send to NLDR via ISyncTransport.SendBatchAsync
        // Update checkpoint on ACK
        _logger.LogInformation("Outbound sync cycle. (Implementation pending Kafka consumer integration).");
        return Task.CompletedTask;
    }

    private Task ProcessInboundAsync(CancellationToken ct)
    {
        // TODO: Receive from NLDR via ISyncTransport.ReceiveAsync
        // Process via IInboxProcessor
        // Update inbound checkpoint
        _logger.LogInformation("Inbound processing cycle. (Implementation pending NLDR integration).");
        return Task.CompletedTask;
    }
}
