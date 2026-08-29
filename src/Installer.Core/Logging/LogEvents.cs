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
}
