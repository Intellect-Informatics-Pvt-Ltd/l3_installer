using Confluent.Kafka;
using Dapper;
using Harness.Common.Options;
using Harness.Common.Outbox;
using Harness.Common.TestHooks;
using Harness.Common.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pacs.SyncWorker.Kafka;
using System.Text.Json;

namespace Pacs.SyncWorker.Workers;

/// <summary>
/// Kafka consumer for <c>epacs.nldr.acks</c> and <c>epacs.nldr.commands</c>.
/// On receiving an ACK: marks the outbox row ACKED and advances the checkpoint (§12.3.3).
/// On receiving a NACK: marks the outbox row FAILED for retry.
/// </summary>
public sealed class InboundConsumerService(
    IServiceScopeFactory scopeFactory,
    IOptions<SyncOptions> syncOpts,
    IOptions<PacsOptions> pacsOpts,
    IOptions<MessagingOptions> msgOpts,
    IFaultInjector faultInjector,
    ILogger<InboundConsumerService> logger)
    : TraceableBackgroundService(logger)
{
    private IConsumer<string, string>? _consumer;
    private static readonly JsonSerializerOptions _json = new();

    protected override async Task RunCycleAsync(CancellationToken ct)
    {
        _consumer ??= BuildConsumer();

        var result = _consumer.Consume(TimeSpan.FromMilliseconds(200));
        if (result is null) return;

        try
        {
            var payload = JsonSerializer.Deserialize<AckPayload>(result.Message.Value, _json);
            if (payload is null) return;

            await ProcessAckAsync(payload, ct);
            _consumer.Commit(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InboundConsumer] Error processing message offset={Offset}", result.Offset);
        }
    }

    private async Task ProcessAckAsync(AckPayload ack, CancellationToken ct)
    {
        await faultInjector.FireAsync(FaultHook.BeforeAckUpdate, ct);

        await using var scope = scopeFactory.CreateAsyncScope();
        await using var db    = scope.ServiceProvider.GetRequiredService<MySqlConnection>();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        var newStatus = ack.AckStatus == "ACK" ? OutboxStatus.Acked : OutboxStatus.Failed;

        const string updateSql = """
            UPDATE sync_outbox
               SET status  = @newStatus,
                   ack_at  = NOW(6),
                   last_error = @nackReason
             WHERE event_id = @eventId
               AND pacs_id  = @pacsId
            """;
        await db.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            newStatus,
            nackReason = ack.NackReason,
            eventId    = ack.EventId,
            pacsId     = ack.PacsId
        }, tx, cancellationToken: ct));

        // Advance checkpoint if contiguous
        if (ack.AckStatus == "ACK")
        {
            const string checkpointSql = """
                UPDATE sync_checkpoints
                   SET last_acked_sequence = GREATEST(last_acked_sequence, @seq),
                       updated_at          = NOW(6)
                 WHERE pacs_id     = @pacsId
                   AND stream_name = 'pacs.outbound'
                   AND @seq = last_acked_sequence + 1
                """;
            await db.ExecuteAsync(new CommandDefinition(checkpointSql,
                new { pacsId = ack.PacsId, seq = ack.SequenceNo }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        await faultInjector.FireAsync(FaultHook.AfterAckUpdate, ct);

        logger.LogInformation(
            "[InboundConsumer] ACK processed: eventId={EventId} status={Status} seq={Seq}",
            ack.EventId, ack.AckStatus, ack.SequenceNo);
    }

    private IConsumer<string, string> BuildConsumer()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = msgOpts.Value.Kafka.BootstrapServers,
            GroupId          = $"pacs-ack-consumer-{pacsOpts.Value.PacsId}",
            AutoOffsetReset  = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        var c = new ConsumerBuilder<string, string>(config).Build();
        c.Subscribe(new[] { syncOpts.Value.AcksTopic, syncOpts.Value.CommandsTopic });
        logger.LogInformation("[InboundConsumer] Subscribed to {Acks}, {Cmds}",
            syncOpts.Value.AcksTopic, syncOpts.Value.CommandsTopic);
        return c;
    }

    public override void Dispose()
    {
        _consumer?.Close();
        _consumer?.Dispose();
        base.Dispose();
    }
}

internal sealed class AckPayload
{
    public string EventId    { get; init; } = string.Empty;
    public string PacsId     { get; init; } = string.Empty;
    public long   SequenceNo { get; init; }
    public string AckStatus  { get; init; } = "ACK";
    public string? NackReason { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}
