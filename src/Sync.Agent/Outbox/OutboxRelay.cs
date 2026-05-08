using Microsoft.Extensions.Logging;

namespace Sync.Agent.Outbox;

/// <summary>
/// Relays events from MySQL sync_outbox to local Kafka topic.
/// Pattern: SELECT ... FOR UPDATE SKIP LOCKED → publish to Kafka → UPDATE status.
/// If Kafka is down, events remain in outbox (MySQL is the durable anchor).
/// </summary>
public sealed class OutboxRelay : IOutboxRelay
{
    private readonly ILogger<OutboxRelay> _logger;
    private bool _kafkaAvailable = true;
    private int _consecutiveKafkaFailures;

    public OutboxRelay(ILogger<OutboxRelay> logger)
    {
        _logger = logger;
    }

    public Task<int> DrainAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (!_kafkaAvailable)
        {
            _logger.LogWarning("Kafka unavailable. Outbox drain skipped. Events remain in MySQL (safe).");
            // Attempt reconnect periodically
            _consecutiveKafkaFailures++;
            if (_consecutiveKafkaFailures > 10)
            {
                _kafkaAvailable = true; // Reset to retry
                _consecutiveKafkaFailures = 0;
            }
            return Task.FromResult(0);
        }

        // TODO: Actual MySQL query + Kafka publish implementation
        // Pseudocode:
        // 1. BEGIN TRANSACTION
        // 2. SELECT * FROM sync_outbox WHERE status='PENDING' ORDER BY priority, sequence_number
        //    LIMIT {batchSize} FOR UPDATE SKIP LOCKED
        // 3. For each event: publish to Kafka topic 'epacs.local.sync-ready'
        // 4. UPDATE sync_outbox SET status='RELAYED', relayed_at=NOW() WHERE id IN (...)
        // 5. COMMIT
        // On Kafka failure: ROLLBACK, mark _kafkaAvailable=false

        _logger.LogInformation("Outbox drain cycle: batch size {BatchSize}. (Implementation pending MySQL/Kafka integration).", batchSize);
        return Task.FromResult(0);
    }

    public Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        // TODO: SELECT COUNT(*) FROM sync_outbox WHERE status='PENDING'
        return Task.FromResult(0L);
    }
}
