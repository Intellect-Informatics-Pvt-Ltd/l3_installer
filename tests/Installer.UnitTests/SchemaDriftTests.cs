using FluentAssertions;
using Installer.Core.Schema;

namespace Installer.UnitTests;

/// <summary>
/// Schema drift detection — W7.
///
/// The capture is mechanical; the CLASSIFICATION is the judgement, so that is what these cover.
/// The installer has to decide unattended, on a node with nobody to ask, which is why it
/// classifies conservatively where the estate's own tooling reports and lets a human choose.
///
/// The keys asserted below are the estate's own, from db/known-drift.txt. A DBA comparing an
/// installer drift report with a verify-baseline-ddl.py report must see one vocabulary.
/// </summary>
public sealed class SchemaDriftTests
{
    private static ColumnFingerprint Col(string name, string type = "int", bool nullable = true, string? def = null) =>
        new() { Name = name, Type = type, IsNullable = nullable, Default = def };

    private static TableFingerprint Table(
        string name,
        IEnumerable<ColumnFingerprint>? columns = null,
        IReadOnlyList<string>? pk = null,
        Dictionary<string, IReadOnlyList<string>>? indexes = null,
        IEnumerable<string>? fks = null) => new()
    {
        Name = name,
        Engine = "InnoDB",
        Collation = "utf8mb4_0900_ai_ci",
        Columns = (columns ?? [Col("id")]).ToDictionary(c => c.Name, c => c, StringComparer.Ordinal),
        Indexes = indexes ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        PrimaryKey = pk ?? ["id"],
        ForeignKeys = (fks ?? []).ToHashSet(StringComparer.Ordinal)
    };

    private static SchemaFingerprint Print(params TableFingerprint[] tables) => new()
    {
        DatabaseName = "epacs",
        StackVersion = "3.3.0",
        CapturedAt = DateTimeOffset.UnixEpoch,
        Tables = tables.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal),
        Views = [],
        FingerprintHash = "x"
    };

    private static SchemaDriftReport Compare(
        SchemaFingerprint expected, SchemaFingerprint actual, IReadOnlyDictionary<string, string>? ack = null) =>
        new MySqlSchemaFingerprinter(Microsoft.Extensions.Logging.Abstractions.NullLogger<MySqlSchemaFingerprinter>.Instance)
            .Compare(expected, actual, ack);

    // ── No drift ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_identical_schema_reports_nothing()
    {
        var report = Compare(Print(Table("fa_voucher")), Print(Table("fa_voucher")));

        report.HasDrift.Should().BeFalse();
        report.Severity.Should().Be(DriftSeverity.None);
        report.Findings.Should().BeEmpty();
    }

    // ── Presence ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_missing_table_is_breaking_and_uses_the_estates_key()
    {
        var report = Compare(Print(Table("fa_voucher"), Table("fa_voucherdetails")), Print(Table("fa_voucher")));

        report.Severity.Should().Be(DriftSeverity.Breaking);
        report.Findings.Should().ContainSingle(f => f.Key == "live-missing-table:fa_voucherdetails");
    }

    [Fact]
    public void An_extra_table_is_benign()
    {
        // Almost always a table from an older release. The application does not read it, so it
        // cannot change behaviour — but it is still reported.
        var report = Compare(Print(Table("fa_voucher")), Print(Table("fa_voucher"), Table("old_thing")));

        report.Severity.Should().Be(DriftSeverity.Benign);
        report.Findings.Should().ContainSingle(f => f.Key == "live-extra-table:old_thing");
    }

    [Fact]
    public void A_missing_column_is_breaking()
    {
        var report = Compare(
            Print(Table("fa_voucher", [Col("id"), Col("narration", "varchar(500)")])),
            Print(Table("fa_voucher", [Col("id")])));

        report.Severity.Should().Be(DriftSeverity.Breaking);
        report.Findings.Should().ContainSingle(f => f.Key == "live-missing-column:fa_voucher.narration");
    }

    [Fact]
    public void An_extra_nullable_column_is_benign()
    {
        var report = Compare(
            Print(Table("fa_voucher", [Col("id")])),
            Print(Table("fa_voucher", [Col("id"), Col("spare", nullable: true)])));

        report.Severity.Should().Be(DriftSeverity.Benign);
    }

    [Fact]
    public void An_extra_NOT_NULL_column_with_no_default_is_BREAKING()
    {
        // The subtle one. An extra column looks additive, but a NOT NULL column with no default
        // cannot be ignored: every INSERT this release issues omits it and fails with ERROR
        // 1364. That is a breaking difference wearing the costume of an additive one, and it is
        // the shape a half-applied migration leaves behind.
        var report = Compare(
            Print(Table("fa_voucher", [Col("id")])),
            Print(Table("fa_voucher", [Col("id"), Col("mandatory", "int", nullable: false, def: null)])));

        report.Severity.Should().Be(DriftSeverity.Breaking);
        report.Findings.Should().ContainSingle(f => f.Key == "live-extra-column:fa_voucher.mandatory");
        report.Findings.Single(f => f.Key == "live-extra-column:fa_voucher.mandatory")
              .Message.Should().Contain("1364");
    }

    [Fact]
    public void An_extra_NOT_NULL_column_WITH_a_default_is_benign()
    {
        var report = Compare(
            Print(Table("fa_voucher", [Col("id")])),
            Print(Table("fa_voucher", [Col("id"), Col("flag", "int", nullable: false, def: "0")])));

        report.Severity.Should().Be(DriftSeverity.Benign);
    }

    // ── Shape ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_changed_column_type_is_breaking_even_when_it_looks_like_a_widening()
    {
        // This estate has the scar: a money column that became varchar through a merge
        // tie-break, and Dapper silently rounding a decimal to int because the C# property had
        // not moved with the column. An unattended installer is the wrong place to adjudicate
        // whether a particular type change is safe.
        var report = Compare(
            Print(Table("fa_voucher", [Col("amount", "decimal(18,2)")])),
            Print(Table("fa_voucher", [Col("amount", "decimal(20,2)")])));

        report.Severity.Should().Be(DriftSeverity.Breaking);
        report.Findings.Should().ContainSingle(f => f.Key == "live-shape-type:fa_voucher.amount");
    }

    [Fact]
    public void A_changed_nullability_is_breaking_in_both_directions()
    {
        var tighter = Compare(
            Print(Table("t", [Col("c", "int", nullable: true)])),
            Print(Table("t", [Col("c", "int", nullable: false)])));
        var looser = Compare(
            Print(Table("t", [Col("c", "int", nullable: false)])),
            Print(Table("t", [Col("c", "int", nullable: true)])));

        tighter.Severity.Should().Be(DriftSeverity.Breaking);
        looser.Severity.Should().Be(DriftSeverity.Breaking);
    }

    [Fact]
    public void A_changed_default_is_only_compatible()
    {
        var report = Compare(
            Print(Table("t", [Col("c", "int", nullable: true, def: "0")])),
            Print(Table("t", [Col("c", "int", nullable: true, def: "1")])));

        report.Severity.Should().Be(DriftSeverity.Compatible);
        report.Findings.Should().ContainSingle(f => f.Key == "live-shape-default:t.c");
    }

    [Fact]
    public void No_default_and_a_default_of_NULL_are_different_columns()
    {
        // The estate needed a sentinel for this because `mysql -N` prints a SQL NULL and the
        // string 'NULL' identically. Reading through a typed client keeps them apart, and the
        // distinction has to survive into the comparison.
        var report = Compare(
            Print(Table("t", [Col("c", "int", nullable: true, def: null)])),
            Print(Table("t", [Col("c", "int", nullable: true, def: "NULL")])));

        report.Findings.Should().ContainSingle(f => f.Key == "live-shape-default:t.c");
    }

    [Fact]
    public void A_nullability_change_does_not_also_report_a_default_change()
    {
        // Reporting both is noise: a nullability change already implies the default moved.
        var report = Compare(
            Print(Table("t", [Col("c", "int", nullable: true, def: null)])),
            Print(Table("t", [Col("c", "int", nullable: false, def: "0")])));

        report.Findings.Should().ContainSingle();
        report.Findings.Single().Key.Should().Be("live-shape-null:t.c");
    }

    // ── Keys and indexes ─────────────────────────────────────────────────────

    [Fact]
    public void A_reordered_primary_key_is_breaking()
    {
        // Not pedantry. Composite key ORDER decides which prefix lookups the index serves, so a
        // reordered key silently turns indexed reads into full scans — the estate measured 18
        // of 47 unwind reads doing exactly that, and they lock the table FOR UPDATE.
        var report = Compare(
            Print(Table("t", [Col("a"), Col("b")], pk: ["a", "b"])),
            Print(Table("t", [Col("a"), Col("b")], pk: ["b", "a"])));

        report.Severity.Should().Be(DriftSeverity.Breaking);
        report.Findings.Should().ContainSingle(f => f.Key == "live-shape-pk:t");
    }

    [Fact]
    public void A_missing_index_is_compatible_not_benign()
    {
        // Queries still return the right answer, so not breaking. But an unwind read without
        // its index scans the table FOR UPDATE and locks it for the life of a correction, so
        // not benign either.
        var idx = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["idx_td166_src"] = ["src_id"] };
        var report = Compare(
            Print(Table("t", [Col("src_id")], indexes: idx)),
            Print(Table("t", [Col("src_id")])));

        report.Severity.Should().Be(DriftSeverity.Compatible);
        report.Findings.Should().ContainSingle(f => f.Key == "live-missing-index:t.idx_td166_src");
    }

    [Fact]
    public void Same_index_name_over_different_columns_is_its_own_finding()
    {
        // The confusing one: everything reports the index as present.
        var expected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["ix_lookup"] = ["a", "b"] };
        var actual = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["ix_lookup"] = ["b", "a"] };

        var report = Compare(
            Print(Table("t", [Col("a"), Col("b")], indexes: expected)),
            Print(Table("t", [Col("a"), Col("b")], indexes: actual)));

        report.Findings.Should().ContainSingle(f => f.Key == "live-shape-index:t.ix_lookup");
    }

    [Fact]
    public void Foreign_keys_are_compared_both_ways()
    {
        var missing = Compare(
            Print(Table("t", fks: ["fk_a"])),
            Print(Table("t")));
        var extra = Compare(
            Print(Table("t")),
            Print(Table("t", fks: ["fk_b"])));

        missing.Findings.Should().ContainSingle(f => f.Key == "live-missing-fk:t.fk_a");
        extra.Findings.Should().ContainSingle(f => f.Key == "live-extra-fk:t.fk_b");
        extra.Severity.Should().Be(DriftSeverity.Compatible,
            "an unexpected constraint can reject writes this release considers valid");
    }

    // ── The acknowledged-drift register ──────────────────────────────────────

    [Fact]
    public void An_acknowledged_finding_is_reported_but_does_not_count()
    {
        var ack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["live-missing-table:fa_voucherdetails"] = "TD-100: known, dispositioned"
        };

        var report = Compare(Print(Table("fa_voucher"), Table("fa_voucherdetails")), Print(Table("fa_voucher")), ack);

        report.HasDrift.Should().BeFalse("someone has already decided to live with it");
        report.Severity.Should().Be(DriftSeverity.None);
        report.Findings.Should().ContainSingle(f => f.IsAcknowledged);
        report.Findings.Single().AcknowledgementReason.Should().Contain("TD-100");
    }

    [Fact]
    public void A_stale_acknowledgement_is_reported()
    {
        // known-drift.txt: "A STALE ENTRY IS NOT HARMLESS HOUSEKEEPING. It is a standing,
        // pre-approved exemption for a finding that no longer exists — so the day that drift
        // comes back, the gate greets it with ACKNOWLEDGED and exits 0."
        var ack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["live-missing-table:something_long_since_fixed"] = "TD-59"
        };

        var report = Compare(Print(Table("t")), Print(Table("t")), ack);

        report.StaleAcknowledgements.Should().ContainSingle().Which.Should().Be("live-missing-table:something_long_since_fixed");
    }

    [Fact]
    public void Acknowledging_one_finding_does_not_hide_another()
    {
        var ack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["live-missing-table:a"] = "known"
        };

        var report = Compare(Print(Table("a"), Table("b")), Print(), ack);

        report.HasDrift.Should().BeTrue();
        report.Unacknowledged.Should().ContainSingle(f => f.Key == "live-missing-table:b");
    }

    // ── The register file format ─────────────────────────────────────────────

    [Fact]
    public void The_register_parses_the_estates_own_format()
    {
        var register = KnownDriftRegister.Parse(
            "# a comment\n" +
            "\n" +
            "column:bdp_inputdatamaster.actualamount\tTD-100 l3_BDP: the module declares this\n" +
            "live-missing-index:t.i\tTD-42 deliberate\n");

        register.Should().HaveCount(2);
        register["live-missing-index:t.i"].Should().Be("TD-42 deliberate");
    }

    [Fact]
    public void A_register_entry_with_no_stated_reason_is_ignored()
    {
        // The register is "deliberately tedious to add to": an exemption with no reason defeats
        // the point, so it is skipped rather than honoured.
        KnownDriftRegister.Parse("live-missing-table:t\n").Should().BeEmpty();
    }

    [Fact]
    public async Task The_estates_real_register_parses()
    {
        // Contract test against db/known-drift.txt in the L2-R2 workspace. If its format ever
        // changes, the installer must find out here rather than by silently honouring nothing.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;
        var register = Path.Combine(dir!.Parent!.FullName, "db", "known-drift.txt");

        if (!File.Exists(register))
        {
            return; // the installer repo can be cloned without the workspace around it
        }

        var entries = await KnownDriftRegister.LoadAsync(register);

        entries.Should().NotBeEmpty("the estate's register has live entries; parsing zero would mean the format moved");
        entries.Keys.Should().OnlyContain(k => k.Contains(':', StringComparison.Ordinal));
    }

    [Fact]
    public void The_rendered_report_leads_with_the_verdict_and_names_stale_entries()
    {
        var ack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["live-extra-table:old"] = "TD-1 fine",
            ["live-missing-table:gone"] = "TD-2 stale"
        };
        var report = Compare(Print(Table("t")), Print(Table("t"), Table("old")), ack);

        var text = KnownDriftRegister.Render(report);

        text.Should().Contain("Verdict: None");
        text.Should().Contain("ACKNOWLEDGED");
        text.Should().Contain("STALE ACKNOWLEDGEMENTS");
        text.Should().Contain("live-missing-table:gone");
    }
}
