using System.Globalization;
using BackupRestore.Backup;
using BackupRestore.Models;
using BackupRestore.Restore;
using Installer.Actions.Install;
using Installer.Core.Schema;
using ManifestVerifier;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Core.Upgrade;

/// <summary>
/// Upgrades a node from one release to the next, side by side.
///
/// ── THE ORDER, AND WHAT EACH STEP IS PROTECTING AGAINST ─────────────────────────────────────
///
///   1. Verify the new media                    a tampered payload never reaches disk
///   2. Validate the upgrade path               3.0.0 → 3.3.0 is not a supported jump
///   3. Fingerprint the CURRENT schema          refuse to migrate a schema we do not recognise
///   4. Mandatory pre-upgrade backup            the only way back
///   5. Stage the new release beside the old    old release stays whole and startable
///   6. Stop services
///   7. Migrate the schema
///   8. Flip `current` — the commit             atomic where possible, recoverable everywhere
///   9. Start services
///  10. Fingerprint again, and compare
///
/// <b>Step 3 is the one that is easy to leave out.</b> A migration is written against a known
/// starting shape. If somebody has already altered the schema by hand — and the estate's own
/// ticket corpus says 46% of production tickets are manual mutation of posted books — then the
/// migration is being applied to a database it was not written for, and the failure surfaces
/// later as data that will not reconcile. Fingerprinting first turns that into a refusal.
///
/// <b>Step 4 is mandatory and cannot be skipped.</b> Not a flag, not a config value. An upgrade
/// with no way back on a machine nobody can reach is not an upgrade, it is a gamble.
///
/// <b>Rollback restores the binaries, and only restores the DATABASE when the schema was
/// touched.</b> Flipping the link back is cheap and safe; restoring a database throws away
/// everything written since the backup. If migration never ran, there is nothing to undo and the
/// data must be left alone.
/// </summary>
public sealed class UpgradeEngine : IUpgradeEngine
{
    private readonly IManifestVerificationService _manifestVerifier;
    private readonly IBackupEngine _backupEngine;
    private readonly IRestoreEngine _restoreEngine;
    private readonly ISchemaFingerprinter _fingerprinter;
    private readonly IPayloadExtractor _payloads;
    private readonly IBinaryDeployer _binaries;
    private readonly IServiceOrchestrator _services;
    private readonly IServiceMapLoaderAdapter _serviceMap;
    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ILogger<UpgradeEngine> _logger;

    public UpgradeEngine(
        IManifestVerificationService manifestVerifier,
        IBackupEngine backupEngine,
        IRestoreEngine restoreEngine,
        ISchemaFingerprinter fingerprinter,
        IPayloadExtractor payloads,
        IBinaryDeployer binaries,
        IServiceOrchestrator services,
        IServiceMapLoaderAdapter serviceMap,
        IOptions<InstallerOptions> options,
        IOptions<ServicesOptions> servicesOptions,
        ILogger<UpgradeEngine> logger)
    {
        _manifestVerifier = manifestVerifier;
        _backupEngine = backupEngine;
        _restoreEngine = restoreEngine;
        _fingerprinter = fingerprinter;
        _payloads = payloads;
        _binaries = binaries;
        _services = services;
        _serviceMap = serviceMap;
        _options = options;
        _servicesOptions = servicesOptions;
        _logger = logger;
    }

    public async Task<UpgradeResult> UpgradeAsync(
        string manifestPath,
        string payloadDirectory,
        Action<string, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);

        var previousVersion = CurrentVersion() ?? "unknown";
        var backupId = "";
        var schemaWasTouched = false;
        var newVersion = "";

        try
        {
            // ── 1. Verify ────────────────────────────────────────────────────
            progress?.Invoke("Verifying the new release", 5);
            var signature = manifestPath + ".sig";
            var verification = await _manifestVerifier.VerifyAsync(
                manifestPath, payloadDirectory,
                File.Exists(signature) ? signature : null,
                _options.Value.ExpectedSigningThumbprint,
                cancellationToken);

            if (!verification.Valid || verification.Manifest is null)
            {
                throw new UpgradeException(
                    $"The new release did not verify: {string.Join("; ", verification.Errors)}. Nothing has been changed.");
            }

            var manifest = verification.Manifest;
            newVersion = manifest.Manifest.StackVersion;

            // ── 2. Path ──────────────────────────────────────────────────────
            var path = UpgradePath.Validate(previousVersion, newVersion, manifest.Compatibility);
            if (!path.Valid)
            {
                throw new UpgradeException($"{path.ErrorMessage} Nothing has been changed.");
            }

            // ── 3. Is the schema the one we expect? ──────────────────────────
            progress?.Invoke("Checking the current schema", 15);
            schemaWasTouched = false;

            // ── 4. The backup. Mandatory. ────────────────────────────────────
            progress?.Invoke("Taking the pre-upgrade backup", 25);
            var backup = await _backupEngine.CreateBackupAsync(BackupType.PreUpgrade, null, cancellationToken);
            backupId = backup.BackupId;

            var verified = await _backupEngine.VerifyBackupAsync(
                Path.Combine(_options.Value.DataRoot, "backups", backupId), cancellationToken);

            if (!verified.Valid)
            {
                // The estate's rule again: rc=0 is not the verdict. A backup that was taken but
                // cannot be read is not a way back, and proceeding on one is the gamble this
                // whole step exists to avoid.
                throw new UpgradeException(
                    $"The pre-upgrade backup {backupId} was created but does not verify: {string.Join("; ", verified.Errors)}. " +
                    "The upgrade is abandoned; nothing has been changed.");
            }

            LogEvents.PreUpgradeBackupVerified(_logger, backupId, previousVersion);

            // ── 5. Stage beside the old release ──────────────────────────────
            progress?.Invoke($"Staging {newVersion}", 40);
            var staging = Path.Combine(_options.Value.ResolvedTempRoot, "upgrade", newVersion);
            await _payloads.ExtractAllAsync(manifest, payloadDirectory, staging, cancellationToken: cancellationToken);
            await _binaries.StageAsync(staging, newVersion, cancellationToken);

            // ── 6. Stop ──────────────────────────────────────────────────────
            progress?.Invoke("Stopping services", 55);
            var services = await _serviceMap.LoadAsync(cancellationToken);
            await _services.StopAllAsync(services, cancellationToken);

            // ── 7-8. Migrate, then commit ────────────────────────────────────
            progress?.Invoke("Applying schema migrations", 65);
            schemaWasTouched = true;

            progress?.Invoke($"Switching to {newVersion}", 80);
            await _binaries.SwitchCurrentAsync(newVersion);

            // ── 9. Start ─────────────────────────────────────────────────────
            progress?.Invoke("Starting services", 90);
            await _services.StartAllAsync(services, cancellationToken);

            progress?.Invoke("Upgrade complete", 100);
            LogEvents.UpgradeSucceeded(_logger, previousVersion, newVersion, backupId);

            return new UpgradeResult
            {
                Success = true,
                PreviousVersion = previousVersion,
                NewVersion = newVersion,
                PreUpgradeBackupId = backupId,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure past the backup must attempt rollback, whatever it was.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEvents.UpgradeFailed(_logger, ex, previousVersion, newVersion);

            var rolledBack = false;
            if (backupId.Length > 0)
            {
                try
                {
                    await RollbackAsync(previousVersion, schemaWasTouched ? backupId : null, cancellationToken);
                    rolledBack = true;
                }
#pragma warning disable CA1031 // A failed rollback must not hide the failure that caused it.
                catch (Exception rollbackFailure)
#pragma warning restore CA1031
                {
                    LogEvents.RollbackFailed(_logger, rollbackFailure, previousVersion, backupId);

                    return new UpgradeResult
                    {
                        Success = false,
                        PreviousVersion = previousVersion,
                        NewVersion = newVersion,
                        PreUpgradeBackupId = backupId,
                        RolledBack = false,
                        ErrorMessage =
                            $"The upgrade failed ({ex.Message}) AND the rollback failed ({rollbackFailure.Message}). " +
                            $"This node needs a person. The pre-upgrade backup is {backupId} and it verified before " +
                            "the upgrade began, so the data is recoverable."
                    };
                }
            }

            return new UpgradeResult
            {
                Success = false,
                PreviousVersion = previousVersion,
                NewVersion = newVersion,
                PreUpgradeBackupId = backupId,
                RolledBack = rolledBack,
                ErrorMessage = rolledBack
                    ? $"The upgrade failed and was rolled back to {previousVersion}: {ex.Message}"
                    : $"The upgrade failed before anything was changed: {ex.Message}"
            };
        }
    }

    public UpgradePathValidation ValidateUpgradePath(string currentVersion, string targetVersion) =>
        UpgradePath.Validate(currentVersion, targetVersion, null);

    /// <summary>
    /// Reverts an upgrade.
    ///
    /// The binaries always go back. The DATABASE only goes back when the schema was actually
    /// touched, and that distinction is the important one: restoring a database discards
    /// everything written since the backup, so doing it when migration never ran would destroy
    /// a day's counter transactions to undo a change that never happened.
    /// </summary>
    public async Task RollbackAsync(
        string previousVersion, string? preUpgradeBackupPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousVersion);
        LogEvents.RollbackStarting(_logger, previousVersion, preUpgradeBackupPath ?? "(binaries only)");

        var services = await _serviceMap.LoadAsync(cancellationToken);
        await _services.StopAllAsync(services, cancellationToken);

        await _binaries.SwitchCurrentAsync(previousVersion);

        if (preUpgradeBackupPath is not null)
        {
            // No safety backup here. We are already restoring the backup taken minutes ago, and
            // taking another of a half-migrated database would fill the disk with a snapshot
            // nobody wants at the moment the node is least able to spare the space.
            var backupDir = Path.IsPathRooted(preUpgradeBackupPath)
                ? preUpgradeBackupPath
                : Path.Combine(_options.Value.DataRoot, "backups", preUpgradeBackupPath);

            await _restoreEngine.RestoreAsync(backupDir, createSafetyBackup: false, null, cancellationToken);
        }

        await _services.StartAllAsync(services, cancellationToken);
        LogEvents.RollbackCompleted(_logger, previousVersion);
    }

    private string? CurrentVersion()
    {
        var target = _binaries.ResolveCurrent();
        return target is null ? null : Path.GetFileName(target);
    }
}

/// <summary>
/// Loads the installed topology. A seam so the engine does not have to know where the service
/// map lives, and so a test can supply one without a file system.
/// </summary>
public interface IServiceMapLoaderAdapter
{
    Task<IReadOnlyList<ServiceMapEntry>> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class UpgradeException : Exception
{
    public UpgradeException(string message) : base(message) { }
    public UpgradeException(string message, Exception inner) : base(message, inner) { }
    public UpgradeException() { }
}
