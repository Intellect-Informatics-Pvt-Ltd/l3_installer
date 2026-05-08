namespace ManifestVerifier;

/// <summary>
/// Verifies SHA-256 hashes of payload files against the release manifest.
/// </summary>
public interface IHashVerifier
{
    /// <summary>
    /// Computes the SHA-256 hash of a file.
    /// </summary>
    /// <param name="filePath">Path to the file to hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Lowercase hex-encoded SHA-256 hash.</returns>
    Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a file's SHA-256 hash against an expected value.
    /// </summary>
    /// <param name="filePath">Path to the file to verify.</param>
    /// <param name="expectedHash">Expected lowercase hex-encoded SHA-256 hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the hash matches; false otherwise.</returns>
    Task<bool> VerifyAsync(string filePath, string expectedHash, CancellationToken cancellationToken = default);
}
