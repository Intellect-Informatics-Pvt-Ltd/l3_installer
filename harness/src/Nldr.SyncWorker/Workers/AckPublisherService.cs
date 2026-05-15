using Confluent.Kafka;
using Dapper;
using Harness.Common.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Nldr.SyncWorker.Kafka;

namespace Nldr.SyncWorker.Workers;

/// <summary>
/// Drains <c>nldr_outbox</c> rows and publishes ACK/NACK messages to Kafka.
/// This closes the loop: Nldr.Api enqueues ACKs → AckPublisherService publishes → Pacs.SyncWorker.InboundConsumer marks outbox ACKED.
/// </summary>
public sealed class AckPublisherService(
    IServiceScopeFactory scopeFactory,
    INldrKafkaProducer producer,
    ILogger<AckPublisherService> logger)
    : TraceableBackgroundService(logger)
{
    protected override async Task RunCycleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var db    = scope.ServiceProvider.GetRequiredService<MySqlConnection>();
        await db.OpenAsync(ct);

        const string selectSql = """
            SELECT outbox_id, pacs_id, event_id, event_type, topic, payload_json, correlation_id
              FROM nldr_outbox
             WHERE status = 'PENDING'
             ORDER BY created_at ASC
             LIMIT 50
             FOR UPDATE SKIP LOCKED
            """;

        await using var tx = await db.BeginTransactionAsync(ct);

        var rows = (await db.QueryAsync<NldrOutboxRow>(
            new CommandDefinition(selectSql, null, tx, cancellationToken: ct))).AsList();

        if (rows.Count == 0)
        {
            await tx.CommitAsync(ct);
            await Task.Delay(500, ct);
            return;
        }

        // Mark in-flight
        foreach (var row in rows)
        {
            await db.ExecuteAsync(
                new CommandDefinition("UPDATE nldr_outbox SET status='PUBLISHED', published_at=NOW(6) WHERE outbox_id=@id",
                new { id = row.OutboxId }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);

        foreach (var row in rows)
        {
            try
            {
                await producer.PublishRawAsync(row.Topic, row.PacsId, row.PayloadJson ?? "{}", ct);
                logger.LogInformation("[AckPublisher] Published {EventType} for {PacsId}", row.EventType, row.PacsId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[AckPublisher] Failed to publish outbox_id={Id}", row.OutboxId);
                await db.ExecuteAsync(
                    "UPDATE nldr_outbox SET status='FAILED' WHERE outbox_id=@id",
                    new { id = row.OutboxId });
            }
        }
    }
}

internal sealed class NldrOutboxRow
{
    public long    OutboxId      { get; init; }
    public string  PacsId        { get; init; } = string.Empty;
    public string  EventId       { get; init; } = string.Empty;
    public string  EventType     { get; init; } = string.Empty;
    public string  Topic         { get; init; } = string.Empty;
    public string? PayloadJson   { get; init; }
    public string  CorrelationId { get; init; } = string.Empty;
}

/// <summary>Options for the Nldr.SyncWorker.</summary>
public sealed class NldrWorkerOptions
{
    public const string SectionName = "Nldr";
    public string Tenant   { get; set; } = "ePACS";
    public string DataRoot { get; set; } = string.Empty;
}
