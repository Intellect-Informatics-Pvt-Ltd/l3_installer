namespace Installer.Actions.Prechecks;

/// <summary>
/// Result of a single precheck validation.
/// </summary>
public sealed record PrecheckResult
{
    /// <summary>Unique identifier for this check (e.g., "OS_VERSION", "DISK_SPACE").</summary>
    public required string CheckId { get; init; }

    /// <summary>Human-readable name of the check.</summary>
    public required string Name { get; init; }

    /// <summary>Severity of the result: Pass, Warning, or Block.</summary>
    public required PrecheckSeverity Severity { get; init; }

    /// <summary>Whether this check passed.</summary>
    public bool Passed => Severity == PrecheckSeverity.Pass;

    /// <summary>Whether this check blocks installation.</summary>
    public bool Blocking => Severity == PrecheckSeverity.Block;

    /// <summary>Operator-friendly message describing the result.</summary>
    public required string Message { get; init; }

    /// <summary>Technical details for support bundle (may contain system info).</summary>
    public string? TechnicalDetail { get; init; }

    /// <summary>Error code from the error catalog (null if passed).</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Severity levels for precheck results.
/// </summary>
public enum PrecheckSeverity
{
    /// <summary>Check passed — no issues.</summary>
    Pass,

    /// <summary>Check produced a warning — installation can proceed but operator should be aware.</summary>
    Warning,

    /// <summary>Check failed — installation is blocked until resolved.</summary>
    Block
}

/// <summary>
/// Aggregated result of all prechecks.
/// </summary>
public sealed record PrecheckSuiteResult
{
    /// <summary>All individual check results.</summary>
    public required IReadOnlyList<PrecheckResult> Results { get; init; }

    /// <summary>Whether all checks passed (no blocking failures).</summary>
    public bool CanProceed => !Results.Any(r => r.Blocking);

    /// <summary>Count of passed checks.</summary>
    public int PassedCount => Results.Count(r => r.Severity == PrecheckSeverity.Pass);

    /// <summary>Count of warning checks.</summary>
    public int WarningCount => Results.Count(r => r.Severity == PrecheckSeverity.Warning);

    /// <summary>Count of blocking checks.</summary>
    public int BlockingCount => Results.Count(r => r.Severity == PrecheckSeverity.Block);
}
