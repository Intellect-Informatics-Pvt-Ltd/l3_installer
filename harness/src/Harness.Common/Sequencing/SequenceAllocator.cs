using Dapper;
using System.Data;

namespace Harness.Common.Sequencing;

/// <summary>
/// Atomically allocates the next sequence number for a (pacsId, streamName) pair
/// inside the caller-supplied transaction (§8.3).
/// <para>
/// The UPDATE + read pattern is safe under InnoDB row-level locking — two
/// concurrent calls serialize on the <c>sync_sequence</c> row, guaranteeing
/// monotonic contiguous allocation (invariant I-4).
/// </para>
/// </summary>
public static class SequenceAllocator
{
    /// <summary>
    /// Increments <c>sync_sequence.next_sequence</c> by 1 and returns the
    /// allocated sequence number (<c>next_sequence - 1</c>).
    /// Must be called within an open <paramref name="tx"/>.
    /// </summary>
    public static async Task<long> GetNextAsync(
        IDbConnection conn,
        IDbTransaction tx,
        string pacsId,
        string streamName,
        CancellationToken ct = default)
    {
        const string update = """
            UPDATE sync_sequence
               SET next_sequence = next_sequence + 1,
                   updated_at    = NOW(6)
             WHERE pacs_id     = @pacsId
               AND stream_name = @streamName
            """;

        var affected = await conn.ExecuteAsync(
            new CommandDefinition(update, new { pacsId, streamName }, tx,
                cancellationToken: ct));

        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"sync_sequence row not found for pacs_id='{pacsId}', stream_name='{streamName}'. " +
                "Ensure V004__seed.sql has been applied.");
        }

        const string read = """
            SELECT next_sequence - 1
              FROM sync_sequence
             WHERE pacs_id     = @pacsId
               AND stream_name = @streamName
            """;

        var seq = await conn.QuerySingleAsync<long>(
            new CommandDefinition(read, new { pacsId, streamName }, tx,
                cancellationToken: ct));

        return seq;
    }
}
