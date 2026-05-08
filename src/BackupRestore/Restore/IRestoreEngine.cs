using BackupRestore.Models;

namespace BackupRestore.Restore;

/// <summary>
/// Orchestrates restore from a verified backup package.
/// Follows BRD 13.5 restore workflow: verify → safety backup → stop → restore → validate → start.
/// </summary>
public interface IRestoreEngine
{
    /// <summary>
    /// Restores from a backup package.
    /// </summary>
    /// <param name="backupPath">Path to the backup package directory.</param>
    /// <param name="createSafetyBackup">Whether to create a pre-restore safety backup (default: true).</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Restore result.</returns>
    Task<RestoreResult> RestoreAsync(
        string backupPath,
        bool createSafetyBackup = true,
        Action<string, int>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a restore operation.
/// </summary>
public sealed record RestoreResult
{
    public required bool Success { get; init; }
    public required string BackupId { get; init; }
    public string? SafetyBackupId { get; init; }
    public required DateTimeOffset RestoredAt { get; init; }
    public bool RequiresReconciliation { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? ErrorMessage { get; init; }
}
