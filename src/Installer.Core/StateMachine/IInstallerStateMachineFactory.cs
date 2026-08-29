using SharedKernel.Contracts;

namespace Installer.Core.StateMachine;

/// <summary>
/// Creates the state machine once the run's mode and target version are known.
///
/// WHY A FACTORY AND NOT A DI SINGLETON. <see cref="InstallerStateMachine"/> stamps the mode,
/// the target version and a fresh correlation id into its very first checkpoint, and a
/// checkpoint that says "Install" when the run is an uninstall is worse than no checkpoint: it
/// is what a recovery run reads to decide what to resume. None of those three values is known
/// at container-build time — the mode comes from <see cref="ModeDetector"/> and the version
/// from the verified manifest — so the machine cannot be constructed until the pipeline has
/// both.
///
/// Registering it as a singleton was caught by <c>ValidateOnBuild</c> the first time the CLI
/// ran: the container cannot supply an <c>InstallerMode</c>. That is the failure working as
/// intended, and it is why the CLI validates the graph before touching the machine.
/// </summary>
public interface IInstallerStateMachineFactory
{
    /// <param name="mode">The operation being performed, as resolved by ModeDetector.</param>
    /// <param name="targetVersion">
    /// Stack version being installed. Taken from the verified manifest, so a checkpoint can
    /// never name a version that was not cryptographically accounted for.
    /// </param>
    /// <param name="previousVersion">The version currently installed, or null on a fresh node.</param>
    IInstallerStateMachine Create(InstallerMode mode, string targetVersion, string? previousVersion = null);
}
