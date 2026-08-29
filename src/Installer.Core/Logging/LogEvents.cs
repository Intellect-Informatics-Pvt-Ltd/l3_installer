using Microsoft.Extensions.Logging;

using SharedKernel.Contracts;
namespace Installer.Core;

/// <summary>
/// Source-generated log messages for Installer.Core.
///
/// WHY THESE ARE NOT PLAIN ILogger CALLS. Two reasons, and the second is the one that matters
/// for this product.
///
/// 1. Cost. The ILogger extension overloads take <c>params object?[]</c>, so every argument is
///    boxed and an array allocated whether or not the level is enabled. CA1873 flags this. The
///    generator emits an IsEnabled guard, so a disabled level costs a branch.
///
/// 2. Stable EventIds. An installer is diagnosed from a support bundle by someone who was not
///    there. A stable, documented EventId is what lets a support engineer grep a bundle for
///    "what happened at the junction flip" without knowing the message wording, and it survives
///    message text being reworded or translated. Treat an EventId as an interface: reuse is
///    forbidden, retirement is fine, renumbering breaks every runbook that cites it.
///
/// EventId ranges across the product:
///   1000-1099  Installer.Core        (state machine, mode detection)
///   2000-2099  Installer.Actions     prechecks
///   2100-2199  Installer.Actions     install
///   2200-2299  Installer.Actions     uninstall
///   2300-2399  Installer.Actions     topology
///   2400-2499  Installer.Actions     harness integration
///   2700-2799  SharedKernel          audit chain
///   2800-2899  SharedKernel          secret store
///   3000-3099  BackupRestore
///   4000-4099  SupportBundle
///   5000-5099  Sync.Agent
/// </summary>
internal static partial class LogEvents
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "State transition: {PreviousPhase} -> {NextPhase} (SubPhase: {SubPhase}, Mode: {Mode})")]
    public static partial void StateTransition(ILogger logger, InstallerPhase previousPhase, InstallerPhase nextPhase, string subPhase, InstallerMode mode);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Previous run ended in {Phase}. No recovery needed.")]
    public static partial void NoRecoveryNeeded(ILogger logger, InstallerPhase phase);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "Existing installation detected at {Path}. Mode: Upgrade.")]
    public static partial void ExistingInstallationDetected(ILogger logger, string path);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Requested mode {Mode} validated against installation state.")]
    public static partial void RequestedModeValidated(ILogger logger, InstallerMode mode);

    // ── Pipeline and site config: 1020-1049 ─────────────────────────────────
    [LoggerMessage(EventId = 1020, Level = LogLevel.Warning,
        Message = "Resuming an incomplete run from phase {Phase} (correlation {CorrelationId}).")]
    public static partial void PipelineResuming(ILogger logger, InstallerPhase phase, string correlationId);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Information,
        Message = "Pipeline succeeded. Mode: {Mode}, version: {Version}.")]
    public static partial void PipelineSucceeded(ILogger logger, InstallerMode mode, string version);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Error,
        Message = "Pipeline failed in mode {Mode}.")]
    public static partial void PipelineFailed(ILogger logger, Exception exception, InstallerMode mode);

    [LoggerMessage(EventId = 1023, Level = LogLevel.Error,
        Message = "Could not write the failure checkpoint for {ErrorCode}. The original failure is the one to act on.")]
    public static partial void CheckpointWriteFailed(ILogger logger, Exception exception, string errorCode);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Information,
        Message = "Site config loaded: PACS {PacsId}, state {StateCode}, from {Path}.")]
    public static partial void SiteConfigLoaded(ILogger logger, string pacsId, string stateCode, string path);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Information,
        Message = "Site config {Path} carries a signature. NOTE: presence only - cryptographic verification is not implemented (tasks.md 7.9).")]
    public static partial void SiteConfigSignaturePresent(ILogger logger, string path);

    [LoggerMessage(EventId = 1032, Level = LogLevel.Warning,
        Message = "Site config {Path} is UNSIGNED and was accepted because --allow-unsigned-config was passed. Never use this on an installation.")]
    public static partial void SiteConfigUnsignedAccepted(ILogger logger, string path);

    // ── Concurrency guard: 1040-1049 ────────────────────────────────────────
    [LoggerMessage(EventId = 1040, Level = LogLevel.Information,
        Message = "Installer lock acquired: {Path}.")]
    public static partial void InstallerLockAcquired(ILogger logger, string path);

    [LoggerMessage(EventId = 1041, Level = LogLevel.Warning,
        Message = "Installer lock at {Path} is held by another process ({Holder}). Refusing to run a second installer.")]
    public static partial void InstallerLockUnavailable(ILogger logger, string path, string holder);

    [LoggerMessage(EventId = 1042, Level = LogLevel.Information,
        Message = "Installer lock released: {Path}.")]
    public static partial void InstallerLockReleased(ILogger logger, string path);

    [LoggerMessage(EventId = 1050, Level = LogLevel.Critical,
        Message = "Refusing to initialise the database: {Reason}")]
    public static partial void DatabaseBootstrapRefused(ILogger logger, string reason);

    // Schema fingerprinting: 1060-1069
    [LoggerMessage(EventId = 1060, Level = LogLevel.Information,
        Message = "Schema captured from {Database}: {Tables} table(s), {Columns} column(s), fingerprint {Hash}...")]
    public static partial void SchemaCaptured(ILogger logger, string database, int tables, int columns, string hash);

    // Upgrade: 1070-1089
    [LoggerMessage(EventId = 1070, Level = LogLevel.Information,
        Message = "Pre-upgrade backup {BackupId} taken and VERIFIED before upgrading from {Version}.")]
    public static partial void PreUpgradeBackupVerified(ILogger logger, string backupId, string version);

    [LoggerMessage(EventId = 1071, Level = LogLevel.Information,
        Message = "Upgrade succeeded: {From} -> {To}. Pre-upgrade backup {BackupId} retained.")]
    public static partial void UpgradeSucceeded(ILogger logger, string from, string to, string backupId);

    [LoggerMessage(EventId = 1072, Level = LogLevel.Error,
        Message = "Upgrade from {From} to {To} failed.")]
    public static partial void UpgradeFailed(ILogger logger, Exception exception, string from, string to);

    [LoggerMessage(EventId = 1075, Level = LogLevel.Warning,
        Message = "Rolling back to {Version}, restoring {Backup}.")]
    public static partial void RollbackStarting(ILogger logger, string version, string backup);

    [LoggerMessage(EventId = 1076, Level = LogLevel.Information,
        Message = "Rollback to {Version} complete.")]
    public static partial void RollbackCompleted(ILogger logger, string version);

    [LoggerMessage(EventId = 1077, Level = LogLevel.Critical,
        Message = "ROLLBACK FAILED after a failed upgrade to {Version}. This node needs a person. Backup: {Backup}.")]
    public static partial void RollbackFailed(ILogger logger, Exception exception, string version, string backup);
}
