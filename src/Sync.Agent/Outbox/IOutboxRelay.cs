namespace Sync.Agent.Outbox;

/// <summary>
/// Relays events from the MySQL sync_outbox table to the local Kafka topic.
/// Uses SELECT ... FOR UPDATE SKIP LOCKED for active-active drain support.
/// Gracefully handles Kafka unavailability — business operations are never blocked.
/// </summary>
public interface IOutboxRelay
{
    /// <summary>
    /// Drains pending outbox events to Kafka.
    /// </summary>
    /// <param name="batchSize">Maximum events to process per cycle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of events relayed.</returns>
    Task<int> DrainAsync(int batchSize = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current outbox depth (pending events not yet relayed).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of pending outbox events.</returns>
    Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default);
}
