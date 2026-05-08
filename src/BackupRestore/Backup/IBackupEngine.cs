using BackupRestore.Models;

namespace BackupRestore.Backup;

/// <summary>
/// Orchestrates backup creation: MySQL dump, attachments, config, keys, sync state.
/// All backup operations are atomic (write-then-rename) and verified before completion.
/// </summary>
public interface IBackupEngine
{
    /// <summary>
    /// Creates a full backup package.
    /// </summary>
    /// <param name="backupType">Type of backup (pre-upgrade, daily, weekly, manual, pre-restore).</param>
    /// <param name="progress">Optional progress callback (step name, percent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backup manifest describing the created package.</returns>
    Task<BackupManifest> CreateBackupAsync(
        BackupType backupType,
        Action<string, int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies an existing backup package (checksum, manifest signature, dump readability).
    /// </summary>
    /// <param name="backupPath">Path to the backup package directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    Task<BackupVerificationResult> VerifyBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the backup target (path exists, writable, sufficient space).
    /// </summary>
    /// <param name="estimatedSizeBytes">Estimated backup size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    Task<BackupTargetValidation> ValidateTargetAsync(
        long estimatedSizeBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of backup verification.
/// </summary>
public sealed record BackupVerificationResult
{
    public required bool Valid { get; init; }
    public bool ChecksumVerified { get; init; }
    public bool ManifestSignatureValid { get; init; }
    public bool DumpReadable { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Result of backup target validation.
/// </summary>
public sealed record BackupTargetValidation
{
    public required bool Valid { get; init; }
    public required string TargetPath { get; init; }
    public double FreeSpaceGb { get; init; }
    public double RequiredSpaceGb { get; init; }
    public bool SameVolumeAsData { get; init; }
    public string? ErrorMessage { get; init; }
}
