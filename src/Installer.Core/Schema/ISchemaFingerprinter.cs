namespace Installer.Core.Schema;

/// <summary>
/// Captures and compares database schema fingerprints for DDL drift detection.
/// Used before upgrades to detect if the on-disk schema has been modified outside the installer.
/// </summary>
public interface ISchemaFingerprinter
{
    /// <summary>
    /// Captures the current schema fingerprint from INFORMATION_SCHEMA.
    /// </summary>
    /// <param name="connectionString">MySQL connection string.</param>
    /// <param name="databaseName">Database name to fingerprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captured schema fingerprint.</returns>
    Task<SchemaFingerprint> CaptureAsync(string connectionString, string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares two fingerprints and produces a drift report.
    /// </summary>
    /// <param name="expected">Expected fingerprint (from last install/upgrade).</param>
    /// <param name="actual">Current fingerprint (captured now).</param>
    /// <returns>Drift report with classified differences.</returns>
    SchemaDriftReport Compare(SchemaFingerprint expected, SchemaFingerprint actual);
}

/// <summary>
/// Represents a point-in-time snapshot of the database schema.
/// </summary>
public sealed record SchemaFingerprint
{
    public required string DatabaseName { get; init; }
    public required string StackVersion { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required int TableCount { get; init; }
    public required int ViewCount { get; init; }
    public required int ForeignKeyCount { get; init; }
    public required int IndexCount { get; init; }
    public required string FingerprintHash { get; init; }
    public IReadOnlyList<TableFingerprint> Tables { get; init; } = [];
}

/// <summary>
/// Fingerprint of a single table.
/// </summary>
public sealed record TableFingerprint
{
    public required string TableName { get; init; }
    public required string Engine { get; init; }
    public required string Charset { get; init; }
    public required string Collation { get; init; }
    public required int ColumnCount { get; init; }
    public required int IndexCount { get; init; }
    public required int ForeignKeyCount { get; init; }
    public required string ColumnHash { get; init; } // Hash of all column definitions
}

/// <summary>
/// Report of schema drift between expected and actual fingerprints.
/// </summary>
public sealed record SchemaDriftReport
{
    public required bool HasDrift { get; init; }
    public required DriftSeverity Severity { get; init; }
    public IReadOnlyList<string> AddedTables { get; init; } = [];
    public IReadOnlyList<string> MissingTables { get; init; } = [];
    public IReadOnlyList<string> ModifiedTables { get; init; } = [];
    public IReadOnlyList<string> Details { get; init; } = [];
}

/// <summary>
/// Severity classification of schema drift.
/// </summary>
public enum DriftSeverity
{
    /// <summary>No drift detected.</summary>
    None,

    /// <summary>Drift is benign (extra indexes, extra tables). Upgrade can proceed.</summary>
    Benign,

    /// <summary>Drift is compatible (additive columns). Upgrade can proceed with warning.</summary>
    Compatible,

    /// <summary>Drift is breaking (missing columns, changed types). Upgrade blocked.</summary>
    Breaking
}
