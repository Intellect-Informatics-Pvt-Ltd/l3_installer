using SharedKernel.Contracts;

namespace Installer.Core.StateMachine;

/// <summary>
/// The installer state machine that manages phase transitions with
/// checkpoint persistence for power-cut recovery.
/// </summary>
public interface IInstallerStateMachine
{
    /// <summary>Gets the current state of the installer.</summary>
    InstallationState CurrentState { get; }

    /// <summary>Gets whether the state machine is in a terminal state (Success or Failed).</summary>
    bool IsTerminal { get; }

    /// <summary>
    /// Transitions to the next phase, persisting a checkpoint to disk.
    /// </summary>
    /// <param name="nextPhase">The phase to transition to.</param>
    /// <param name="subPhase">Optional sub-phase identifier.</param>
    /// <param name="context">Optional context data for the phase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TransitionAsync(
        InstallerPhase nextPhase,
        string? subPhase = null,
        Dictionary<string, string>? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to recover from a previous incomplete run.
    /// Returns the phase to resume from, or null if no recovery is needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state to resume from, or null if clean start.</returns>
    Task<InstallationState?> TryRecoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the state machine as successfully completed.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the state machine as failed with an error.
    /// </summary>
    /// <param name="errorCode">The error code from the error catalog.</param>
    /// <param name="errorMessage">Human-readable error message.</param>
    Task FailAsync(string errorCode, string errorMessage, CancellationToken cancellationToken = default);
}
