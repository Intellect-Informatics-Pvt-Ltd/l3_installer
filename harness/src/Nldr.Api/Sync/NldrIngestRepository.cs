using Dapper;
using System.Data;
using System.Text.Json;

namespace Nldr.Api.Sync;

public interface INldrIngestRepository
{
    /// <summary>Returns the last acknowledged sequence for the pacs_id on pacs.outbound stream.</summary>
    Task<long> GetLastAckedSequenceAsync(IDbConnection conn, IDbTransaction tx, string pacsId, CancellationToken ct);

    /// <summary>Checks if event_id already exists in received_event.</summary>
    Task<bool> EventExistsAsync(IDbConnection conn, IDbTransaction tx, string eventId, CancellationToken ct);

    /// <summary>Inserts a received_event row.</summary>
    Task InsertReceivedEventAsync(IDbConnection conn, IDbTransaction tx,
        string eventId, string pacsId, long sequenceNo,
        string changeType, string entityType, string entityId,
        string? payloadJson, string? beforeStateJson, string payloadHash,
        string applyStatus, string? rejectReason, string correlationId,
        CancellationToken ct);

    /// <summary>Upserts the business state in nldr_business_voucher or nldr_business_loan.</summary>
    Task ApplyBusinessStateAsync(IDbConnection conn, IDbTransaction tx,
        string entityType, string changeType, string entityId,
        JsonElement? payload, JsonElement? beforeState,
        string? deletionReason, string correlationId,
        CancellationToken ct);

    /// <summary>Enqueues an ACK in nldr_outbox for publishing by Nldr.SyncWorker.</summary>
    Task EnqueueAckAsync(IDbConnection conn, IDbTransaction tx,
        string eventId, string pacsId, long sequenceNo,
        string ackStatus, string? nackReason, string correlationId,
        string acksTopic, CancellationToken ct);

    /// <summary>Inserts gap rows into sequence_gap for any missing sequence numbers.</summary>
    Task InsertSequenceGapsAsync(IDbConnection conn, IDbTransaction tx,
        string pacsId, long fromExclusive, long toExclusive, CancellationToken ct);

    /// <summary>Advances last_acked_sequence in a pseudo-checkpoint for this NLDR side.</summary>
    Task UpdateNldrCheckpointAsync(IDbConnection conn, IDbTransaction tx,
        string pacsId, long ackedSequence, CancellationToken ct);
}

public sealed class NldrIngestRepository : INldrIngestRepository
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public async Task<long> GetLastAckedSequenceAsync(
        IDbConnection conn, IDbTransaction tx, string pacsId, CancellationToken ct)
    {
        const string sql = """
            SELECT COALESCE(MAX(sequence_no), 0)
              FROM received_event
             WHERE pacs_id = @pacsId AND apply_status = 'APPLIED'
            """;
        return await conn.QuerySingleAsync<long>(
            new CommandDefinition(sql, new { pacsId }, tx, cancellationToken: ct));
    }

    public async Task<bool> EventExistsAsync(
        IDbConnection conn, IDbTransaction tx, string eventId, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(1) FROM received_event WHERE event_id = @eventId";
        var count = await conn.QuerySingleAsync<int>(
            new CommandDefinition(sql, new { eventId }, tx, cancellationToken: ct));
        return count > 0;
    }

    public async Task InsertReceivedEventAsync(
        IDbConnection conn, IDbTransaction tx,
        string eventId, string pacsId, long sequenceNo,
        string changeType, string entityType, string entityId,
        string? payloadJson, string? beforeStateJson, string payloadHash,
        string applyStatus, string? rejectReason, string correlationId,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO received_event
                (event_id, pacs_id, sequence_no, change_type, entity_type, entity_id,
                 payload_json, before_state_json, payload_hash,
                 received_at, apply_status, reject_reason, correlation_id)
            VALUES
                (@eventId, @pacsId, @sequenceNo, @changeType, @entityType, @entityId,
                 @payloadJson, @beforeStateJson, @payloadHash,
                 NOW(6), @applyStatus, @rejectReason, @correlationId)
            ON DUPLICATE KEY UPDATE
                apply_status  = VALUES(apply_status),
                reject_reason = VALUES(reject_reason)
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            eventId, pacsId, sequenceNo, changeType, entityType, entityId,
            payloadJson, beforeStateJson, payloadHash,
            applyStatus, rejectReason, correlationId
        }, tx, cancellationToken: ct));
    }

    public async Task ApplyBusinessStateAsync(
        IDbConnection conn, IDbTransaction tx,
        string entityType, string changeType, string entityId,
        JsonElement? payload, JsonElement? beforeState,
        string? deletionReason, string correlationId,
        CancellationToken ct)
    {
        var table = entityType == "voucher" ? "nldr_business_voucher" : "nldr_business_loan";

        if (changeType == "DELETE")
        {
            var updateSql = table == "nldr_business_voucher"
                ? """
                  UPDATE nldr_business_voucher
                     SET is_deleted=1, deleted_at=NOW(6),
                         deletion_reason=@deletionReason,
                         deletion_correlation_id=@correlationId,
                         entity_state_version=entity_state_version+1
                   WHERE voucher_id=@entityId
                  """
                : """
                  UPDATE nldr_business_loan
                     SET is_deleted=1,
                         entity_state_version=entity_state_version+1
                   WHERE loan_app_id=@entityId
                  """;
            await conn.ExecuteAsync(new CommandDefinition(updateSql,
                new { entityId = long.Parse(entityId, System.Globalization.CultureInfo.InvariantCulture),
                      deletionReason, correlationId },
                tx, cancellationToken: ct));
            return;
        }

        if (payload is null) return;

        // For INSERT / UPDATE / AMENDMENT: upsert the business row.
        // We serialize the payload and pass individual fields.
        // This is intentionally simplified — full column mapping would be added in M5.
        if (table == "nldr_business_voucher")
        {
            const string upsert = """
                INSERT INTO nldr_business_voucher
                    (voucher_id, pacs_id, voucher_no, voucher_date, voucher_type, total_amount, is_deleted, entity_state_version)
                VALUES
                    (@voucherId, @pacsId, @voucherNo, @voucherDate, @voucherType, @totalAmount, 0, 1)
                ON DUPLICATE KEY UPDATE
                    voucher_no=VALUES(voucher_no), voucher_date=VALUES(voucher_date),
                    voucher_type=VALUES(voucher_type), total_amount=VALUES(total_amount),
                    entity_state_version=entity_state_version+1
                """;

            var root = payload.Value.TryGetProperty("after", out var after) ? after : payload.Value;

            await conn.ExecuteAsync(new CommandDefinition(upsert, new
            {
                voucherId   = long.Parse(entityId, System.Globalization.CultureInfo.InvariantCulture),
                pacsId      = TryGet(root, "pacsId"),
                voucherNo   = TryGet(root, "voucherNo"),
                voucherDate = TryGetDate(root, "voucherDate"),
                voucherType = TryGet(root, "voucherType"),
                totalAmount = TryGetDecimal(root, "totalAmount")
            }, tx, cancellationToken: ct));
        }
        else
        {
            const string upsert = """
                INSERT INTO nldr_business_loan
                    (loan_app_id, pacs_id, loan_app_no, member_no, member_name, requested_amount, status, is_deleted, entity_state_version)
                VALUES
                    (@loanAppId, @pacsId, @loanAppNo, @memberNo, @memberName, @requestedAmount, @status, 0, 1)
                ON DUPLICATE KEY UPDATE
                    status=VALUES(status), requested_amount=VALUES(requested_amount),
                    entity_state_version=entity_state_version+1
                """;

            var root = payload.Value.TryGetProperty("after", out var after) ? after : payload.Value;

            await conn.ExecuteAsync(new CommandDefinition(upsert, new
            {
                loanAppId       = long.Parse(entityId, System.Globalization.CultureInfo.InvariantCulture),
                pacsId          = TryGet(root, "pacsId"),
                loanAppNo       = TryGet(root, "loanAppNo"),
                memberNo        = TryGet(root, "memberNo"),
                memberName      = TryGet(root, "memberName") ?? "[redacted]",
                requestedAmount = TryGetDecimal(root, "requestedAmount"),
                status          = TryGet(root, "status") ?? "SUBMITTED"
            }, tx, cancellationToken: ct));
        }
    }

    public async Task EnqueueAckAsync(
        IDbConnection conn, IDbTransaction tx,
        string eventId, string pacsId, long sequenceNo,
        string ackStatus, string? nackReason, string correlationId,
        string acksTopic, CancellationToken ct)
    {
        // 1. Write ack_log
        const string logSql = """
            INSERT INTO ack_log (event_id, pacs_id, sequence_no, ack_status, nack_reason, acked_at, correlation_id)
            VALUES (@eventId, @pacsId, @sequenceNo, @ackStatus, @nackReason, NOW(6), @correlationId)
            """;
        await conn.ExecuteAsync(new CommandDefinition(logSql, new
        {
            eventId, pacsId, sequenceNo, ackStatus, nackReason, correlationId
        }, tx, cancellationToken: ct));

        // 2. Enqueue in nldr_outbox for Nldr.SyncWorker to publish
        var ackPayload = JsonSerializer.Serialize(new
        {
            eventId, pacsId, sequenceNo, ackStatus, nackReason, correlationId
        }, _json);

        const string outboxSql = """
            INSERT INTO nldr_outbox (pacs_id, event_id, event_type, topic, payload_json, status, created_at, correlation_id)
            VALUES (@pacsId, @outboxEventId, @eventType, @topic, @payload, 'PENDING', NOW(6), @correlationId)
            """;
        await conn.ExecuteAsync(new CommandDefinition(outboxSql, new
        {
            pacsId,
            outboxEventId = Guid.NewGuid().ToString("D"),
            eventType     = ackStatus == "ACK" ? "nldr.ack" : "nldr.nack",
            topic         = acksTopic,
            payload       = ackPayload,
            correlationId
        }, tx, cancellationToken: ct));
    }

    public async Task InsertSequenceGapsAsync(
        IDbConnection conn, IDbTransaction tx,
        string pacsId, long fromExclusive, long toExclusive, CancellationToken ct)
    {
        for (var seq = fromExclusive + 1; seq < toExclusive; seq++)
        {
            const string sql = """
                INSERT IGNORE INTO sequence_gap (pacs_id, missing_sequence, detected_at)
                VALUES (@pacsId, @seq, NOW(6))
                """;
            await conn.ExecuteAsync(new CommandDefinition(sql, new { pacsId, seq }, tx, cancellationToken: ct));
        }
    }

    public async Task UpdateNldrCheckpointAsync(
        IDbConnection conn, IDbTransaction tx,
        string pacsId, long ackedSequence, CancellationToken ct)
    {
        // Resolve any gaps that are now filled
        const string sql = """
            UPDATE sequence_gap
               SET resolved_at = NOW(6)
             WHERE pacs_id = @pacsId
               AND missing_sequence = @ackedSequence
               AND resolved_at IS NULL
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new { pacsId, ackedSequence }, tx, cancellationToken: ct));
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string? TryGet(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() : null;

    private static decimal TryGetDecimal(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetDecimal(out var d) ? d : 0m;

    private static DateOnly TryGetDate(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return DateOnly.MinValue;
        var s = v.GetString() ?? "";
        return DateOnly.TryParse(s, out var d) ? d : DateOnly.MinValue;
    }
}
