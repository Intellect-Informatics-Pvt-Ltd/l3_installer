using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Installer.Core.Schema;

/// <summary>
/// Captures a fingerprint from MySQL's <c>INFORMATION_SCHEMA</c>.
///
/// The queries are the estate's own, from <c>build/verify-baseline-ddl.py</c>'s
/// <c>live_schema()</c> and <c>live_shapes()</c> — including the reason it reads
/// INFORMATION_SCHEMA rather than <c>SHOW CREATE TABLE</c>: the server has already normalised
/// the values, so <c>COLUMN_TYPE</c> is canonical, <c>COLUMN_DEFAULT</c> is unquoted, and
/// <c>STATISTICS</c> yields index columns in <c>SEQ_IN_INDEX</c> order. Reading them differently
/// here would produce a second opinion about the same database.
///
/// Every identifier is lower-cased, as the estate does — see <see cref="ISchemaFingerprinter"/>
/// for why that matters more on a Windows node than it does online.
/// </summary>
public sealed class MySqlSchemaFingerprinter : ISchemaFingerprinter
{
    private readonly ILogger<MySqlSchemaFingerprinter> _logger;

    public MySqlSchemaFingerprinter(ILogger<MySqlSchemaFingerprinter> logger) => _logger = logger;

    public async Task<SchemaFingerprint> CaptureAsync(
        string connectionString, string databaseName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = await ReadTablesAsync(connection, databaseName, cancellationToken);
        var views = await ReadViewsAsync(connection, databaseName, cancellationToken);
        var columns = await ReadColumnsAsync(connection, databaseName, cancellationToken);
        var indexes = await ReadIndexesAsync(connection, databaseName, cancellationToken);
        var foreignKeys = await ReadForeignKeysAsync(connection, databaseName, cancellationToken);

        var assembled = tables.ToDictionary(
            t => t.Key,
            t => new TableFingerprint
            {
                Name = t.Key,
                Engine = t.Value.Engine,
                Collation = t.Value.Collation,
                Columns = columns.TryGetValue(t.Key, out var c) ? c : new Dictionary<string, ColumnFingerprint>(StringComparer.Ordinal),
                Indexes = indexes.TryGetValue(t.Key, out var i) ? i.Indexes : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                PrimaryKey = indexes.TryGetValue(t.Key, out var p) ? p.PrimaryKey : [],
                ForeignKeys = foreignKeys.TryGetValue(t.Key, out var f) ? f : []
            },
            StringComparer.Ordinal);

        var fingerprint = new SchemaFingerprint
        {
            DatabaseName = databaseName,
            StackVersion = "",
            CapturedAt = DateTimeOffset.UtcNow,
            Tables = assembled,
            Views = views,
            FingerprintHash = ComputeHash(assembled, views)
        };

        LogEvents.SchemaCaptured(_logger, databaseName, fingerprint.TableCount, fingerprint.ColumnCount, fingerprint.FingerprintHash[..16]);
        return fingerprint;
    }

    public SchemaDriftReport Compare(
        SchemaFingerprint expected, SchemaFingerprint actual, IReadOnlyDictionary<string, string>? acknowledged = null) =>
        SchemaDriftComparer.Compare(expected, actual, acknowledged);

    private static async Task<Dictionary<string, (string Engine, string Collation)>> ReadTablesAsync(
        MySqlConnection connection, string db, CancellationToken ct)
    {
        const string sql = "SELECT TABLE_NAME, ENGINE, TABLE_COLLATION FROM information_schema.TABLES " +
                           "WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE'";

        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, sql, db, ct))
        {
            result[Lower(row, 0)] = (row.IsDBNull(1) ? "" : row.GetString(1), row.IsDBNull(2) ? "" : row.GetString(2));
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadViewsAsync(MySqlConnection connection, string db, CancellationToken ct)
    {
        const string sql = "SELECT TABLE_NAME FROM information_schema.TABLES " +
                           "WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'VIEW'";

        var result = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, sql, db, ct))
        {
            result.Add(Lower(row, 0));
        }
        return result;
    }

    private static async Task<Dictionary<string, Dictionary<string, ColumnFingerprint>>> ReadColumnsAsync(
        MySqlConnection connection, string db, CancellationToken ct)
    {
        const string sql = "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT " +
                           "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db " +
                           "ORDER BY TABLE_NAME, ORDINAL_POSITION";

        var result = new Dictionary<string, Dictionary<string, ColumnFingerprint>>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, sql, db, ct))
        {
            var table = Lower(row, 0);
            var column = Lower(row, 1);

            if (!result.TryGetValue(table, out var columns))
            {
                columns = new Dictionary<string, ColumnFingerprint>(StringComparer.Ordinal);
                result[table] = columns;
            }

            columns[column] = new ColumnFingerprint
            {
                Name = column,
                // NOT lower-cased: COLUMN_TYPE is a value, not an identifier, and MySQL already
                // returns it canonically. Folding it would also hide real differences in ENUM
                // member spelling.
                Type = row.IsDBNull(2) ? "" : row.GetString(2),
                IsNullable = row.GetString(3).Equals("YES", StringComparison.OrdinalIgnoreCase),
                // A typed null, so "no default at all" stays distinct from "defaults to NULL"
                // without the sentinel the estate's text-based reader needs.
                Default = row.IsDBNull(4) ? null : row.GetString(4)
            };
        }
        return result;
    }

    private static async Task<Dictionary<string, (Dictionary<string, IReadOnlyList<string>> Indexes, IReadOnlyList<string> PrimaryKey)>>
        ReadIndexesAsync(MySqlConnection connection, string db, CancellationToken ct)
    {
        // SEQ_IN_INDEX ordering is load-bearing, not tidiness: composite index column ORDER
        // decides which prefix lookups the index can serve.
        const string sql = "SELECT TABLE_NAME, INDEX_NAME, COLUMN_NAME FROM information_schema.STATISTICS " +
                           "WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX";

        var accumulator = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, sql, db, ct))
        {
            var table = Lower(row, 0);
            var index = Lower(row, 1);
            var column = Lower(row, 2);

            if (!accumulator.TryGetValue(table, out var indexes))
            {
                indexes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                accumulator[table] = indexes;
            }

            if (!indexes.TryGetValue(index, out var columns))
            {
                columns = [];
                indexes[index] = columns;
            }

            columns.Add(column);
        }

        return accumulator.ToDictionary(
            t => t.Key,
            t =>
            {
                // PRIMARY is pulled out and compared separately: it is the one index whose
                // identity is structural rather than nominal.
                var primary = t.Value.TryGetValue("primary", out var pk) ? pk : [];
                var others = t.Value
                    .Where(kv => kv.Key != "primary")
                    .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
                return (others, (IReadOnlyList<string>)primary);
            },
            StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, HashSet<string>>> ReadForeignKeysAsync(
        MySqlConnection connection, string db, CancellationToken ct)
    {
        const string sql = "SELECT TABLE_NAME, CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS " +
                           "WHERE TABLE_SCHEMA = @db AND CONSTRAINT_TYPE = 'FOREIGN KEY'";

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, sql, db, ct))
        {
            var table = Lower(row, 0);
            if (!result.TryGetValue(table, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                result[table] = keys;
            }
            keys.Add(Lower(row, 1));
        }
        return result;
    }

    private static async IAsyncEnumerable<MySqlDataReader> QueryAsync(
        MySqlConnection connection, string sql, string db,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        // Parameterised. The schema name arrives from configuration rather than from a user,
        // but a database name concatenated into a query is a habit that ends up somewhere it
        // matters.
        command.Parameters.AddWithValue("@db", db);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return reader;
        }
    }

    private static string Lower(MySqlDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? "" : row.GetString(ordinal).ToLowerInvariant();

    /// <summary>
    /// A stable hash over the whole shape.
    ///
    /// Everything is sorted before hashing, so the value depends on the schema and not on the
    /// order the server happened to return rows in. Without that, the hash changes between two
    /// captures of an unchanged database and the cheap "has anything moved?" check it exists
    /// for becomes a permanent false alarm.
    /// </summary>
    private static string ComputeHash(IReadOnlyDictionary<string, TableFingerprint> tables, IEnumerable<string> views)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;

        foreach (var table in tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append(c, $"T:{table.Name}|{table.Engine}|{table.Collation}\n");

            foreach (var col in table.Columns.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                sb.Append(c, $"  C:{col.Name}|{col.Type}|{(col.IsNullable ? "NULL" : "NOTNULL")}|{col.Default ?? ""}\n");
            }

            sb.Append(c, $"  PK:{string.Join(",", table.PrimaryKey)}\n");

            foreach (var idx in table.Indexes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                sb.Append(c, $"  I:{idx.Key}|{string.Join(",", idx.Value)}\n");
            }

            foreach (var fk in table.ForeignKeys.Order(StringComparer.Ordinal))
            {
                sb.Append(c, $"  F:{fk}\n");
            }
        }

        foreach (var view in views.Order(StringComparer.Ordinal))
        {
            sb.Append(c, $"V:{view}\n");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
