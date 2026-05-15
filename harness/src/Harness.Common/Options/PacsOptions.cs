namespace Harness.Common.Options;

/// <summary>Top-level PACS node configuration.</summary>
public sealed class PacsOptions
{
    public const string SectionName = "Pacs";

    /// <summary>E.g. <c>PACS-AP-0001</c>. Must match pattern <c>^PACS-[A-Z]{2}-\d{4}$</c>.</summary>
    public string PacsId   { get; set; } = string.Empty;
    public string Tenant   { get; set; } = "ePACS";
    /// <summary>Root path for data files, logs, and reconciliation reports.</summary>
    public string DataRoot { get; set; } = string.Empty;

    public IamOptions        Iam        { get; set; } = new();
    public GovernanceOptions Governance { get; set; } = new();
}

public sealed class IamOptions
{
    /// <summary>
    /// When false (local dev / test), identity is read from
    /// <c>X-Test-User</c> and <c>X-Test-Role</c> headers.
    /// </summary>
    public bool Enabled { get; set; }
}

public sealed class GovernanceOptions
{
    public int     BulkDeleteThreshold       { get; set; } = 10;
    public int     RequireBackupAgeHours     { get; set; } = 24;
    /// <summary>SHA-256 hex of the override token for bulk-delete above threshold.</summary>
    public string? OverrideTokenHashSha256   { get; set; }
}

/// <summary>Test harness feature flags.</summary>
public sealed class HarnessOptions
{
    public const string SectionName = "Harness";

    /// <summary>When true, TestControl routes are active and fault hooks fire.</summary>
    public bool   TestMode              { get; set; }
    /// <summary>Active deployment profile (Default, Multi-Pacs, Two-Laptop, Vm-Lab, Installer).</summary>
    public string Profile               { get; set; } = "Default";
    public bool   ScenarioPlayerEnabled { get; set; }
}

/// <summary>NLDR-side configuration.</summary>
public sealed class NldrOptions
{
    public const string SectionName = "Nldr";

    public string Tenant       { get; set; } = "ePACS";
    public string DataRoot     { get; set; } = string.Empty;
    public IamOptions Iam      { get; set; } = new();
}

/// <summary>UI-level settings for the operator console and dashboard.</summary>
public sealed class UiOptions
{
    public const string SectionName = "Ui";

    public PollingOptions Polling { get; set; } = new();
}

public sealed class PollingOptions
{
    /// <summary>Milliseconds between sync-status polls in the UI banner.</summary>
    public int StatusIntervalMs { get; set; } = 2000;
}
