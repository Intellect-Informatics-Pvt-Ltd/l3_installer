using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Extracts verified payloads from the installer archive to the staging directory.
/// Supports resumable extraction (tracks progress for power-cut recovery).
/// </summary>
public interface IPayloadExtractor
{
    /// <summary>
    /// Extracts all payloads to the staging directory.
    /// </summary>
    /// <param name="manifest">The verified release manifest.</param>
    /// <param name="sourceDirectory">Directory containing the payload archives.</param>
    /// <param name="stagingDirectory">Target directory for extraction.</param>
    /// <param name="progress">Optional progress callback (payload index, total, name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExtractAllAsync(
        ReleaseManifest manifest,
        string sourceDirectory,
        string stagingDirectory,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default);
}
