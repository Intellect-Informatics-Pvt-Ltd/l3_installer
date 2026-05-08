namespace SharedKernel.Contracts;

/// <summary>
/// Represents the installer state machine checkpoint.
/// Written to disk (fsync'd) on every state transition for power-cut recovery.
/// </summary>
public sealed record InstallationState
{
    /// <summary>Current state of the installer state machine.</summary>
    public required InstallerPhase Phase { get; init; }

    /// <summary>Sub-phase within the current phase (e.g., payload index during extraction).</summary>
    public string? SubPhase { get; init; }

    /// <summary>Mode of operation (Install, Upgrade, Repair, Backup, Restore, Uninstall).</summary>
    public required InstallerMode Mode { get; init; }

    /// <summary>Stack version being installed or upgraded to.</summary>
    public required string TargetVersion { get; init; }

    /// <summary>Previously installed version (null for fresh install).</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>Timestamp when this checkpoint was written.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Correlation ID for the current operation.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Process ID that wrote this checkpoint (for stale lock detection).</summary>
    public int ProcessId { get; init; }

    /// <summary>Additional context data for the current phase.</summary>
    public Dictionary<string, string>? Context { get; init; }
}

/// <summary>
/// Phases of the installer state machine.
/// </summary>
public enum InstallerPhase
{
    Load,
    Verify,
    Precheck,
    Install,
    Upgrade,
    Repair,
    Backup,
    Restore,
    Uninstall,
    PreBackup,
    Migrate,
    Commit,
    Health,
    Smoke,
    Success,
    Failed,
    Recovery,
    SupportBundle,
    Rollback
}

/// <summary>
/// Modes of installer operation.
/// </summary>
public enum InstallerMode
{
    Install,
    Upgrade,
    Repair,
    Backup,
    Restore,
    Uninstall,
    Hotfix
}
