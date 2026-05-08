namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for AUTO_INCREMENT seeding and ID space partitioning.
/// Each PACS gets a non-overlapping range of the BIGINT ID space.
/// Formula: Seed = pacsid × RangeSize
/// Binds to the <c>IdGeneration</c> section of appsettings.json.
/// </summary>
public sealed class IdGenerationOptions
{
    public const string SectionName = "IdGeneration";

    /// <summary>
    /// Range size per PACS. Each PACS gets this many IDs per table.
    /// Default: 10^10 (10 billion IDs per PACS per table — effectively infinite).
    /// </summary>
    public long RangeSize { get; set; } = 10_000_000_000L;

    /// <summary>
    /// Source of the PACS ID for seed computation.
    /// "epcfg" = read from .epcfg file, "config" = read from appsettings.
    /// </summary>
    public string PacsIdSource { get; set; } = "epcfg";

    /// <summary>
    /// Reserved ID range for NLDR/central (IDs below this are NLDR-originated).
    /// </summary>
    public IdRange NldrReservedRange { get; set; } = new();

    /// <summary>
    /// Whether to validate that the computed seed doesn't overlap with known PACS ranges.
    /// </summary>
    public bool ValidateNoOverlap { get; set; } = true;

    /// <summary>
    /// Path to the YAML file listing tables that require sequence-based ID generation.
    /// </summary>
    public string SequenceTablesPath { get; set; } = "config/sequence-tables.yaml";

    /// <summary>
    /// SourceId value for locally-created records.
    /// </summary>
    public int LocalSourceId { get; set; } = 1;

    /// <summary>
    /// SourceId value for NLDR-originated records.
    /// </summary>
    public int NldrSourceId { get; set; } = 2;

    /// <summary>
    /// SourceId value for legacy-migrated records.
    /// </summary>
    public int LegacySourceId { get; set; } = 3;

    /// <summary>
    /// Percentage of BIGINT max at which to alert (AUTO_INCREMENT approaching ceiling).
    /// </summary>
    public int AlertAtUsagePercent { get; set; } = 50;
}

/// <summary>
/// Represents a reserved ID range.
/// </summary>
public sealed class IdRange
{
    /// <summary>Minimum ID in the range (inclusive).</summary>
    public long Min { get; set; } = 1;

    /// <summary>Maximum ID in the range (inclusive).</summary>
    public long Max { get; set; } = 9_999_999_999L;
}
