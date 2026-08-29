using Microsoft.Extensions.Logging;

namespace SupportBundle;

/// <summary>
/// Source-generated log messages for SupportBundle.
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
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Collecting support bundle: {BundleName}.")]
    public static partial void BundleCollecting(ILogger logger, string bundleName);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Support bundle created: {ZipPath}.")]
    public static partial void BundleCreated(ILogger logger, string zipPath);
}
