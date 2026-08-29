using FluentAssertions;
using Installer.Core.Schema;
using Microsoft.Extensions.Logging.Abstractions;

namespace Installer.UnitTests;

/// <summary>
/// Fingerprint capture against a REAL MySQL, when one is available.
///
/// The comparison logic is covered by <c>SchemaDriftTests</c> without a database. This covers
/// the part that cannot be: whether the INFORMATION_SCHEMA queries actually return what the
/// comparison expects. Those are different failures — a comparison can be perfect over a
/// fingerprint that captured the wrong thing, and nothing else would notice.
///
/// SKIPS when no server is configured, rather than failing. Set EPACS_TEST_MYSQL to a connection
/// string to run it, e.g. in CI:
///
///   EPACS_TEST_MYSQL="Server=127.0.0.1;Port=13399;Database=epacs;Uid=root;Pwd=fp"
///
/// A skipped test is honest; a test that silently passes because it did nothing is not, so the
/// skip reason names the variable.
/// </summary>
public sealed class SchemaFingerprintLiveTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("EPACS_TEST_MYSQL");
    private static bool Available => !string.IsNullOrWhiteSpace(ConnectionString);

    private static MySqlSchemaFingerprinter Fingerprinter =>
        new(NullLogger<MySqlSchemaFingerprinter>.Instance);

    private static async Task<SchemaFingerprint> CaptureAsync() =>
        await Fingerprinter.CaptureAsync(ConnectionString!, "epacs");

    [SkippableFact]
    public async Task Captures_tables_columns_indexes_keys_and_views()
    {
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var fp = await CaptureAsync();

        fp.Tables.Should().ContainKey("fa_voucher");
        fp.Tables.Should().ContainKey("fa_voucherdetails");
        fp.Views.Should().Contain("v_voucher_summary");

        var voucher = fp.Tables["fa_voucher"];
        voucher.Engine.Should().Be("InnoDB");
        voucher.Columns.Should().ContainKeys("id", "pacsid", "amount", "narration", "spare");
    }

    [SkippableFact]
    public async Task Identifiers_are_lower_cased_so_a_folding_node_produces_no_spurious_drift()
    {
        // The column is declared `PacsId`. A Windows node runs lower_case_table_names=1 and
        // stores identifiers folded; if the fingerprint kept case, every mixed-case identifier
        // in the estate would read as drift on every Windows node on every run.
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var fp = await CaptureAsync();

        fp.Tables["fa_voucher"].Columns.Should().ContainKey("pacsid");
        fp.Tables["fa_voucher"].Columns.Should().NotContainKey("PacsId");
    }

    [SkippableFact]
    public async Task Column_types_nullability_and_defaults_come_back_canonically()
    {
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var cols = (await CaptureAsync()).Tables["fa_voucher"].Columns;

        cols["amount"].Type.Should().Be("decimal(18,2)", "COLUMN_TYPE is MySQL's own canonical spelling");
        cols["amount"].IsNullable.Should().BeFalse();
        cols["amount"].Default.Should().Be("0.00");

        cols["narration"].IsNullable.Should().BeTrue();
        cols["narration"].Default.Should().BeNull("no default at all is not the same as defaulting to NULL");
    }

    [SkippableFact]
    public async Task Composite_primary_key_order_is_preserved()
    {
        // Load-bearing: key ORDER decides which prefix lookups the index serves.
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var fp = await CaptureAsync();

        fp.Tables["fa_voucher"].PrimaryKey.Should().ContainInOrder("id", "pacsid");
    }

    [SkippableFact]
    public async Task Indexes_carry_their_columns_in_SEQ_IN_INDEX_order_and_exclude_the_primary()
    {
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var voucher = (await CaptureAsync()).Tables["fa_voucher"];

        voucher.Indexes.Should().ContainKey("idx_voucher_pacs");
        voucher.Indexes["idx_voucher_pacs"].Should().ContainInOrder("pacsid", "amount");
        voucher.Indexes.Should().NotContainKey("primary", "the primary key is compared separately");
    }

    [SkippableFact]
    public async Task Foreign_keys_are_captured()
    {
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var fp = await CaptureAsync();

        fp.Tables["fa_voucherdetails"].ForeignKeys.Should().Contain("fk_vd_voucher");
        fp.ForeignKeyCount.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task The_fingerprint_hash_is_stable_across_captures_of_an_unchanged_database()
    {
        // Without ordering the hash inputs, the value changes between two captures of the same
        // database — and the cheap "has anything moved?" check becomes a permanent false alarm.
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var first = await CaptureAsync();
        var second = await CaptureAsync();

        second.FingerprintHash.Should().Be(first.FingerprintHash);
    }

    [SkippableFact]
    public async Task A_capture_compared_with_itself_reports_no_drift()
    {
        // The end-to-end statement: real capture in, real comparison out, nothing invented.
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var fp = await CaptureAsync();

        var report = Fingerprinter.Compare(fp, fp);

        report.HasDrift.Should().BeFalse();
        report.Severity.Should().Be(DriftSeverity.None);
    }

    // ── End to end: capture, mutate, capture, compare ────────────────────────

    [SkippableFact]
    public async Task Detects_a_real_mutation_end_to_end()
    {
        // The whole chain against a real server: capture a shape, change the database the way a
        // half-applied migration or a DBA would, capture again, and check the comparison names
        // exactly what changed — in the estate's own drift-key vocabulary.
        //
        // Works on its OWN table, created and dropped here, so it cannot disturb the shared
        // fixture other tests assert against. (Mutating the shared schema by hand is how the
        // first attempt at this broke three unrelated tests.)
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var table = "drift_probe_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync($@"
            CREATE TABLE {table} (
              id BIGINT NOT NULL PRIMARY KEY,
              amount DECIMAL(18,2) NOT NULL DEFAULT 0.00,
              narration VARCHAR(500) NULL,
              KEY idx_probe (amount)
            ) ENGINE=InnoDB");

        try
        {
            var before = await CaptureAsync();

            await ExecuteAsync($"ALTER TABLE {table} MODIFY COLUMN amount DECIMAL(20,4) NOT NULL DEFAULT 0.0000");
            await ExecuteAsync($"ALTER TABLE {table} DROP COLUMN narration");
            await ExecuteAsync($"ALTER TABLE {table} DROP INDEX idx_probe");
            await ExecuteAsync($"ALTER TABLE {table} ADD COLUMN mandatory_new INT NOT NULL");

            var after = await CaptureAsync();

            after.FingerprintHash.Should().NotBe(before.FingerprintHash,
                "the cheap 'has anything moved?' check must notice");

            var report = Fingerprinter.Compare(before, after);
            var keys = report.Unacknowledged.Select(f => f.Key).ToList();

            keys.Should().Contain($"live-shape-type:{table}.amount");
            keys.Should().Contain($"live-missing-column:{table}.narration");
            keys.Should().Contain($"live-missing-index:{table}.idx_probe");
            keys.Should().Contain($"live-extra-column:{table}.mandatory_new");

            // A NOT NULL column with no default is breaking, not additive: every INSERT the
            // release issues omits it and fails with ERROR 1364.
            report.Unacknowledged.Single(f => f.Key == $"live-extra-column:{table}.mandatory_new")
                  .Severity.Should().Be(DriftSeverity.Breaking);

            report.Severity.Should().Be(DriftSeverity.Breaking);
        }
        finally
        {
            await ExecuteAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    [SkippableFact]
    public async Task Acknowledged_drift_from_the_register_does_not_block()
    {
        Skip.IfNot(Available, "EPACS_TEST_MYSQL is not set");

        var table = "ack_probe_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync($"CREATE TABLE {table} (id INT PRIMARY KEY) ENGINE=InnoDB");

        try
        {
            var before = await CaptureAsync();
            await ExecuteAsync($"DROP TABLE {table}");
            var after = await CaptureAsync();

            var blocked = Fingerprinter.Compare(before, after);
            blocked.Severity.Should().Be(DriftSeverity.Breaking);

            var register = KnownDriftRegister.Parse($"live-missing-table:{table}\tTD-999 deliberately removed");
            var allowed = Fingerprinter.Compare(before, after, register);

            allowed.HasDrift.Should().BeFalse();
            allowed.Findings.Should().ContainSingle(f => f.IsAcknowledged);
        }
        finally
        {
            await ExecuteAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnector.MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlConnector.MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
