namespace Installer.Actions.Install;

/// <summary>
/// Deploys extracted binaries to the releases directory and manages the 'current' junction.
/// Implements the side-by-side release pattern: releases/<version>/ with a junction at 'current'.
/// </summary>
public interface IBinaryDeployer
{
    /// <summary>
    /// Deploys staged binaries to a versioned release directory and creates/updates the 'current' junction.
    /// </summary>
    /// <param name="stagingDirectory">Directory containing extracted payloads.</param>
    /// <param name="version">The version being deployed (used as directory name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeployAsync(string stagingDirectory, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places a release beside the others without pointing <c>current</c> at it — the staging
    /// half of a side-by-side upgrade, so the old release stays whole and startable until the
    /// commit.
    /// </summary>
    Task StageAsync(string stagingDirectory, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the 'current' junction to point to a specific release version.
    /// This is the atomic commit point for upgrades.
    /// </summary>
    /// <param name="version">The version to switch to.</param>
    /// <summary>
    /// Points <c>current</c> at a release, atomically where the platform allows and recoverably
    /// where it does not. The commit step of an upgrade; see the implementation's remarks.
    /// </summary>
    Task SwitchCurrentAsync(string version);

    /// <summary>Finishes a switch a power cut interrupted. Returns the version completed, or null.</summary>
    Task<string?> TryCompleteInterruptedSwitchAsync(CancellationToken cancellationToken = default);

    /// <summary>Where <c>current</c> points, or null if it is absent.</summary>
    string? ResolveCurrent();
}
