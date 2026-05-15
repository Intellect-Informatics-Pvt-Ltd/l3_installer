using Dapper;
using System.Data;

namespace Harness.Common.Inbox;

/// <summary>Inbox row status values.</summary>
public static class InboxStatus
{
    public const string Received  = "RECEIVED";
    public const string Applied   = "APPLIED";
    public const string Duplicate = "DUPLICATE";
    public const string Rejected  = "REJECTED";
}

/// <summary>
/// Inserts a row into <c>sync_inbox</c> or detects a duplicate (I-3).
/// Called inside the NLDR ingest pipeline step 10.
/// </summary>
public static class SyncInboxStore
{
    /// <summary>
    /// Attempts to insert an inbox row.
    /// Returns <see cref="InboxStatus.Duplicate"/> when a row with the same
    /// <paramref name="eventId"/> already exists; otherwise returns
    /// <see cref="InboxStatus.Received"/>.
    /// </summary>
    public static async Task<string> TryInsertAsync(
        IDbConnection conn,
        IDbTransaction tx,
        string eventId,
        string sourceSystem,
        long?  sequenceNo,
        string payloadHash,
        string idempotencyKey,
        string correlationId,
        CancellationToken ct = default)
    {
        // Check for duplicate by event_id
        const string check = """
            SELECT COUNT(1)
              FROM sync_inbox
             WHERE event_id = @eventId
            """;

        var count = await conn.QuerySingleAsync<int>(
            new CommandDefinition(check, new { eventId }, tx, cancellationToken: ct));

        if (count > 0)
            return InboxStatus.Duplicate;

        const string insert = """
            INSERT INTO sync_inbox
                (source_system, event_id, sequence_no, payload_hash,
                 idempotency_key, status, received_at, correlation_id)
            VALUES
                (@sourceSystem, @eventId, @sequenceNo, @payloadHash,
                 @idempotencyKey, 'RECEIVED', NOW(6), @correlationId)
            """;

        await conn.ExecuteAsync(new CommandDefinition(insert, new
        {
            sourceSystem,
            eventId,
            sequenceNo,
            payloadHash,
            idempotencyKey,
            correlationId
        }, tx, cancellationToken: ct));

        return InboxStatus.Received;
    }

    /// <summary>Marks an inbox row as APPLIED or REJECTED.</summary>
    public static async Task UpdateStatusAsync(
        IDbConnection conn,
        IDbTransaction tx,
        string eventId,
        string status,
        string? rejectReason = null,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE sync_inbox
               SET status        = @status,
                   reject_reason = @rejectReason,
                   applied_at    = CASE WHEN @status = 'APPLIED' THEN NOW(6) ELSE NULL END
             WHERE event_id = @eventId
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { eventId, status, rejectReason }, tx, cancellationToken: ct));
    }
}
