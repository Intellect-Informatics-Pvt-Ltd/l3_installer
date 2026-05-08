namespace Installer.Core.Upgrade;

/// <summary>
/// Orchestrates the side-by-side upgrade flow:
/// 1. Validate upgrade path (version compatibility)
/// 2. Create mandatory pre-upgrade backup
/// 3. Stage new binaries to releases/<new>/
/// 4. Run schema migrations (DbUp with checkpointing)
/// 5. Flip 'current' junction (atomic commit)
/// 6. Run health checks + smoke test
/// 7. On failure: rollback junction + restore pre-upgrade backup
///
/// All operations are checkpoint-persisted for power-cut recovery.
/// </summary>
public interface IUpgradeEngine
{
    /// <summary>
    /// Executes the full upgrade workflow.
    /// </summary>
    /// <param name="manifestPath">Path to the new release manifest.</param>
    /// <param name="payloadDirectory">Directory containing new version payloads.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upgrade result.</returns>
    Task<UpgradeResult> UpgradeAsync(
        string manifestPath,
        string payloadDirectory,
        Action<string, int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates whether an upgrade from the current version to the target version is allowed.
    /// </summary>
    /// <param name="currentVersion">Currently installed version.</param>
    /// <param name="targetVersion">Target version from the new manifest.</param>
    /// <returns>Validation result.</returns>
    UpgradePathValidation ValidateUpgradePath(string currentVersion, string targetVersion);

    /// <summary>
    /// Rolls back a failed upgrade by reverting the junction and optionally restoring the backup.
    /// </summary>
    /// <param name="previousVersion">Version to roll back to.</param>
    /// <param name="preUpgradeBackupPath">Path to the pre-upgrade backup (null = junction-only rollback).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RollbackAsync(string previousVersion, string? preUpgradeBackupPath = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an upgrade operation.
/// </summary>
public sealed record UpgradeResult
{
    public required bool Success { get; init; }
    public required string PreviousVersion { get; init; }
    public required string NewVersion { get; init; }
    public required string PreUpgradeBackupId { get; init; }
    public int MigrationsApplied { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public bool RolledBack { get; init; }
}

/// <summary>
/// Result of upgrade path validation.
/// </summary>
public sealed record UpgradePathValidation
{
    public required bool Valid { get; init; }
    public bool RequiresSideBySide { get; init; }
    public bool HasBreakingSchemaChange { get; init; }
    public string? ErrorMessage { get; init; }
}
