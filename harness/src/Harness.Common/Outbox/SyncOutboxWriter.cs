using Dapper;
using Harness.Common.Envelope;
using System.Data;
using System.Text.Json;

namespace Harness.Common.Outbox;

/// <summary>Outbox row status values matching the DB ENUM.</summary>
public static class OutboxStatus
{
    public const string Pending    = "PENDING";
    public const string InFlight   = "IN_FLIGHT";
    public const string Acked      = "ACKED";
    public const string Failed     = "FAILED";
    public const string Deadletter = "DEADLETTER";
}

/// <summary>
/// Inserts a row into <c>sync_outbox</c> within the caller's transaction (I-2).
/// This is the only place that physically writes to the outbox table from
/// business-logic code.
/// </summary>
public static class SyncOutboxWriter
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    /// <summary>
    /// Inserts the envelope into <c>sync_outbox</c> with <c>status='PENDING'</c>.
    /// <paramref name="conn"/> and <paramref name="tx"/> must be the same connection
    /// and transaction used for the business write so both commit or roll back together.
    /// </summary>
    public static async Task WriteAsync(
        IDbConnection conn,
        IDbTransaction tx,
        EventEnvelope envelope,
        int priority,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO sync_outbox
                (pacs_id, sequence_no, event_id, idempotency_key, change_type,
                 entity_type, entity_id, topic, schema_version,
                 payload_json, before_state_json, payload_hash,
                 priority, status, retry_count,
                 created_at, correlation_id, causation_id)
            VALUES
                (@pacsId, @sequenceNo, @eventId, @idempotencyKey, @changeType,
                 @entityType, @entityId, @topic, @schemaVersion,
                 @payloadJson, @beforeStateJson, @payloadHash,
                 @priority, 'PENDING', 0,
                 NOW(6), @correlationId, @causationId)
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            pacsId          = envelope.PacsId,
            sequenceNo      = envelope.SequenceNo,
            eventId         = envelope.EventId,
            idempotencyKey  = envelope.IdempotencyKey,
            changeType      = envelope.ChangeType.ToString(),
            entityType      = envelope.EntityType,
            entityId        = envelope.EntityId,
            topic           = $"epacs.{envelope.StreamName}",
            schemaVersion   = envelope.SchemaVersion,
            payloadJson     = envelope.Payload is null
                ? null
                : JsonSerializer.Serialize(envelope.Payload, _json),
            beforeStateJson = envelope.BeforeState is null
                ? null
                : JsonSerializer.Serialize(envelope.BeforeState, _json),
            payloadHash     = envelope.PayloadHash,
            priority,
            correlationId   = envelope.CorrelationId,
            causationId     = envelope.CausationId
        }, tx, cancellationToken: ct));
    }
}
