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
    /// Switches the 'current' junction to point to a specific release version.
    /// This is the atomic commit point for upgrades.
    /// </summary>
    /// <param name="version">The version to switch to.</param>
    Task SwitchCurrentAsync(string version);
}
