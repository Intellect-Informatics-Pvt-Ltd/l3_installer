namespace Installer.Core.Schema;

/// <summary>
/// Captures a live database's shape and compares it with what the release expects.
///
/// ── WHY THE VOCABULARY BELOW IS NOT NEW ─────────────────────────────────────────────────────
///
/// The L2-R2 estate already answers this question for the online estate:
/// <c>build/verify-baseline-ddl.py</c> compares <c>db/stable_baseline_ddl.sql</c> against a live
/// database and reports findings under keys like <c>live-missing-column:t.c</c>, and
/// <c>db/known-drift.txt</c> is the register of drift the team has consciously decided to live
/// with. <c>build/schema_shape.py</c> states the rule this follows:
///
///   <i>"Those two answers have to be THE SAME ANSWER. If each had its own copy of 'what is a
///   column type' … the first symptom would be a baseline whose header swears there are no
///   divergences while the verifier fails on nine — which is worse than either check alone,
///   because it teaches the reader to disbelieve both."</i>
///
/// So this emits the estate's own keys, reads the estate's own <c>known-drift.txt</c>, and a DBA
/// comparing an installer drift report with a <c>verify-baseline-ddl.py</c> report sees one
/// vocabulary rather than two.
///
/// ── ON TABLE-NAME CASE ──────────────────────────────────────────────────────────────────────
///
/// Every identifier is lower-cased before comparison, exactly as the estate's <c>live_schema()</c>
/// does. This matters more here than there: a Windows node runs
/// <c>lower_case_table_names=1</c> (ADR-0010 and the F3 notes explain why it must), so it STORES
/// identifiers folded. A naive fingerprint would report every mixed-case identifier in the
/// baseline as drift on every Windows node, on every run. Folding both sides makes case
/// invisible — which is correct, because case is not a schema difference the application can
/// observe.
/// </summary>
public interface ISchemaFingerprinter
{
    /// <summary>Captures the current shape from <c>INFORMATION_SCHEMA</c>.</summary>
    Task<SchemaFingerprint> CaptureAsync(string connectionString, string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares two fingerprints. Pure: it returns findings and decides nothing, so the caller
    /// chooses whether to block — the same separation <c>shape_diffs()</c> keeps in the estate.
    /// </summary>
    /// <param name="acknowledged">
    /// Drift keys from <c>db/known-drift.txt</c>. A key listed there is reported as
    /// ACKNOWLEDGED rather than counted against the verdict, because someone has already decided
    /// to live with it. An entry that matches nothing is reported too — a stale exemption is a
    /// standing pre-approval for a finding that no longer exists, which is worse than none.
    /// </param>
    SchemaDriftReport Compare(
        SchemaFingerprint expected,
        SchemaFingerprint actual,
        IReadOnlyDictionary<string, string>? acknowledged = null);
}

/// <summary>
/// A point-in-time snapshot of a database's shape.
///
/// Read from <c>INFORMATION_SCHEMA</c> rather than <c>SHOW CREATE TABLE</c>, for the reason the
/// estate gives: the server has already normalised the values. <c>COLUMN_TYPE</c> is the
/// canonical spelling, <c>COLUMN_DEFAULT</c> is unquoted, and <c>STATISTICS</c> yields index
/// columns in <c>SEQ_IN_INDEX</c> order.
/// </summary>
public sealed record SchemaFingerprint
{
    public required string DatabaseName { get; init; }
    public required string StackVersion { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Base tables, keyed by lower-cased name.</summary>
    public required IReadOnlyDictionary<string, TableFingerprint> Tables { get; init; }

    /// <summary>Views, lower-cased. Compared for presence only: a view's body is not schema the application binds to in the way a column is.</summary>
    public required IReadOnlyCollection<string> Views { get; init; }

    public int TableCount => Tables.Count;
    public int ViewCount => Views.Count;
    public int ColumnCount => Tables.Values.Sum(t => t.Columns.Count);
    public int IndexCount => Tables.Values.Sum(t => t.Indexes.Count);
    public int ForeignKeyCount => Tables.Values.Sum(t => t.ForeignKeys.Count);

    /// <summary>
    /// A rollup over everything below. Lets "has anything at all changed?" be answered by
    /// comparing two strings, which is what the Installer Agent's periodic check wants — the
    /// full comparison is only worth running once this says yes.
    /// </summary>
    public required string FingerprintHash { get; init; }
}

public sealed record TableFingerprint
{
    public required string Name { get; init; }
    public required string Engine { get; init; }
    public required string Collation { get; init; }

    /// <summary>Columns, keyed by lower-cased name.</summary>
    public required IReadOnlyDictionary<string, ColumnFingerprint> Columns { get; init; }

    /// <summary>Index name (lower-cased) → its columns in <c>SEQ_IN_INDEX</c> order.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Indexes { get; init; }

    /// <summary>Primary key columns in order, or empty when the table has none.</summary>
    public required IReadOnlyList<string> PrimaryKey { get; init; }

    public required IReadOnlyCollection<string> ForeignKeys { get; init; }
}

public sealed record ColumnFingerprint
{
    public required string Name { get; init; }

    /// <summary>The canonical <c>COLUMN_TYPE</c>, e.g. <c>decimal(18,2)</c> or <c>varchar(20)</c>.</summary>
    public required string Type { get; init; }

    public required bool IsNullable { get; init; }

    /// <summary>
    /// <c>COLUMN_DEFAULT</c>, or null for no default at all.
    ///
    /// "No default" and "defaults to NULL" are different columns. The estate had to invent a
    /// sentinel for this because `mysql -N` prints a SQL NULL and the string 'NULL'
    /// identically; reading through a typed client avoids that, and the distinction is kept.
    /// </summary>
    public required string? Default { get; init; }
}

/// <summary>
/// What differs, in the estate's own key vocabulary, with nothing decided.
/// </summary>
public sealed record SchemaDriftReport
{
    public required IReadOnlyList<DriftFinding> Findings { get; init; }

    /// <summary>
    /// Keys supplied as acknowledged that matched no finding.
    ///
    /// Reported because `known-drift.txt` says why: *"A STALE ENTRY IS NOT HARMLESS
    /// HOUSEKEEPING. It is a standing, pre-approved exemption for a finding that no longer
    /// exists — so the day that drift comes back, the gate greets it with ACKNOWLEDGED and
    /// exits 0."*
    /// </summary>
    public required IReadOnlyList<string> StaleAcknowledgements { get; init; }

    public IEnumerable<DriftFinding> Unacknowledged => Findings.Where(f => !f.IsAcknowledged);

    public bool HasDrift => Unacknowledged.Any();

    /// <summary>The worst unacknowledged severity, or <see cref="DriftSeverity.None"/>.</summary>
    public DriftSeverity Severity => Unacknowledged.Any()
        ? Unacknowledged.Max(f => f.Severity)
        : DriftSeverity.None;
}

public sealed record DriftFinding
{
    /// <summary>The estate's drift key, e.g. <c>live-missing-column:fa_voucher.narration</c>.</summary>
    public required string Key { get; init; }

    public required DriftSeverity Severity { get; init; }

    /// <summary>Written for a DBA, not a developer.</summary>
    public required string Message { get; init; }

    /// <summary>Listed in <c>known-drift.txt</c>; reported, not counted.</summary>
    public bool IsAcknowledged { get; init; }

    /// <summary>Why it was acknowledged, from the register.</summary>
    public string? AcknowledgementReason { get; init; }
}

/// <summary>
/// How much a difference matters. Ordered, so <c>Max()</c> gives the worst.
///
/// The estate's own tooling does NOT classify — it reports findings and lets a human decide.
/// The installer has to decide, because it runs unattended on a node with nobody to ask, so it
/// classifies conservatively: anything that could change what a query returns is Breaking.
/// </summary>
public enum DriftSeverity
{
    None = 0,

    /// <summary>The node has something extra. An index somebody added, a table from an older release. Upgrade proceeds.</summary>
    Benign = 1,

    /// <summary>Real but not correctness-affecting: a missing index, a changed default. Upgrade proceeds, loudly.</summary>
    Compatible = 2,

    /// <summary>A missing table or column, a changed type, a changed primary key. The upgrade would run against a schema it was not built for. Blocked.</summary>
    Breaking = 3
}
