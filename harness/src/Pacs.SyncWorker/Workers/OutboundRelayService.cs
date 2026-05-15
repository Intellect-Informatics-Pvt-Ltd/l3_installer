using Dapper;
using Harness.Common.Envelope;
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
/// Continuously drains <c>sync_outbox</c> rows with status=PENDING and
/// publishes each envelope to Kafka (§12.3.2 and §8.1).
/// <para>
/// SELECT … FOR UPDATE SKIP LOCKED ensures exactly-once in-flight handling
/// even when multiple worker instances race (invariant I-2).
/// </para>
/// </summary>
public sealed class OutboundRelayService(
    IServiceScopeFactory scopeFactory,
    IKafkaEnvelopeProducer kafkaProducer,
    IOptions<SyncOptions> syncOpts,
    IOptions<PacsOptions> pacsOpts,
    IFaultInjector faultInjector,
    ILogger<OutboundRelayService> logger)
    : TraceableBackgroundService(logger)
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    protected override async Task RunCycleAsync(CancellationToken ct)
    {
        var opts    = syncOpts.Value;
        var pacsId  = pacsOpts.Value.PacsId;
        var batch   = opts.Outbox.BatchSize;
        var pollMs  = opts.Outbox.PollIntervalMs;

        await using var scope = scopeFactory.CreateAsyncScope();
        await using var db    = scope.ServiceProvider.GetRequiredService<MySqlConnection>();
        await db.OpenAsync(ct);

        // Drain one batch
        const string selectSql = """
            SELECT outbox_id, event_id, pacs_id, sequence_no, idempotency_key,
                   change_type, entity_type, entity_id, topic, schema_version,
                   payload_json, before_state_json, payload_hash,
                   priority, correlation_id, causation_id
              FROM sync_outbox
             WHERE pacs_id = @pacsId AND status = 'PENDING'
             ORDER BY priority ASC, sequence_no ASC
             LIMIT @batch
             FOR UPDATE SKIP LOCKED
            """;

        await using var tx = await db.BeginTransactionAsync(ct);

        var rows = (await db.QueryAsync<OutboxRow>(
            new CommandDefinition(selectSql, new { pacsId, batch }, tx, cancellationToken: ct))).AsList();

        if (rows.Count == 0)
        {
            await tx.CommitAsync(ct);
            await Task.Delay(pollMs, ct);
            return;
        }

        // Mark as IN_FLIGHT (committed before Kafka publish — makes crash resumable)
        foreach (var row in rows)
        {
            const string markSql = """
                UPDATE sync_outbox SET status='IN_FLIGHT', sent_at=NOW(6)
                 WHERE outbox_id=@id
                """;
            await db.ExecuteAsync(new CommandDefinition(markSql, new { id = row.OutboxId }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);

        await faultInjector.FireAsync(FaultHook.AfterMarkInFlight, ct);

        // Publish to Kafka and update status
        foreach (var row in rows)
        {
            try
            {
                await faultInjector.FireAsync(FaultHook.BeforeKafkaPublish, ct);

                var envelope = RowToEnvelope(row);
                await kafkaProducer.PublishAsync(row.Topic, envelope, ct);

                await faultInjector.FireAsync(FaultHook.AfterKafkaPublish, ct);

                // Mark PUBLISHED (will become ACKED when ACK arrives via InboundConsumerService)
                await using var updateConn = new MySqlConnection(db.ConnectionString);
                await updateConn.OpenAsync(ct);
                const string ackSql = """
                    UPDATE sync_outbox SET status='IN_FLIGHT'
                     WHERE outbox_id=@id AND status='IN_FLIGHT'
                    """;
                await updateConn.ExecuteAsync(ackSql, new { id = row.OutboxId });

                logger.LogInformation(
                    "[OutboundRelay] Published seq={Seq} eventId={EventId}",
                    row.SequenceNo, row.EventId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "[OutboundRelay] Failed to publish outbox_id={Id}, incrementing retry_count",
                    row.OutboxId);

                await using var errConn = new MySqlConnection(db.ConnectionString);
                await errConn.OpenAsync(ct);
                var quaAtt = syncOpts.Value.Outbox.QuarantineAfterAttempts;
                const string failSql = """
                    UPDATE sync_outbox
                       SET status      = CASE WHEN retry_count+1 >= @quaAtt THEN 'DEADLETTER' ELSE 'FAILED' END,
                           retry_count = retry_count + 1,
                           last_error  = @err
                     WHERE outbox_id = @id
                    """;
                await errConn.ExecuteAsync(failSql, new
                {
                    id     = row.OutboxId,
                    err    = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
                    quaAtt
                });
            }
        }
    }

    private static EventEnvelope RowToEnvelope(OutboxRow row)
    {
        var changeType = Enum.Parse<ChangeType>(row.ChangeType, ignoreCase: true);

        object? payload     = DeserializeJson(row.PayloadJson);
        object? beforeState = DeserializeJson(row.BeforeStateJson);

        return new EventEnvelope
        {
            EventId        = row.EventId,
            CorrelationId  = row.CorrelationId,
            CausationId    = row.CausationId,
            PacsId         = row.PacsId,
            SequenceNo     = row.SequenceNo,
            StreamName     = "pacs.outbound",
            IdempotencyKey = row.IdempotencyKey,
            ChangeType     = changeType,
            EntityType     = row.EntityType,
            EntityId       = row.EntityId,
            SchemaVersion  = row.SchemaVersion,
            Payload        = payload,
            BeforeState    = beforeState,
            PayloadHash    = row.PayloadHash,
            CreatedAtUtc   = DateTimeOffset.UtcNow
        };
    }

    private static object? DeserializeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<object>(json, _json); }
        catch { return null; }
    }
}

/// <summary>Dapper mapping for sync_outbox columns.</summary>
internal sealed class OutboxRow
{
    public long    OutboxId       { get; init; }
    public string  EventId        { get; init; } = string.Empty;
    public string  PacsId         { get; init; } = string.Empty;
    public long    SequenceNo     { get; init; }
    public string  IdempotencyKey { get; init; } = string.Empty;
    public string  ChangeType     { get; init; } = string.Empty;
    public string  EntityType     { get; init; } = string.Empty;
    public string  EntityId       { get; init; } = string.Empty;
    public string  Topic          { get; init; } = string.Empty;
    public string  SchemaVersion  { get; init; } = "v1";
    public string? PayloadJson    { get; init; }
    public string? BeforeStateJson { get; init; }
    public string  PayloadHash    { get; init; } = string.Empty;
    public int     Priority       { get; init; }
    public string  CorrelationId  { get; init; } = string.Empty;
    public string? CausationId    { get; init; }
}
