using System.Security.Cryptography;

namespace ManifestVerifier;

/// <summary>
/// SHA-256 hash computation and verification for payload files.
/// Uses streaming to handle large files without loading into memory.
/// </summary>
public sealed class HashVerifier : IHashVerifier
{
    private const int BufferSize = 81920; // 80 KB buffer for streaming hash

    /// <inheritdoc />
    public async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found for hash computation: {filePath}", filePath);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string filePath, string expectedHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);

        var actualHash = await ComputeHashAsync(filePath, cancellationToken);
        return string.Equals(actualHash, expectedHash.ToLowerInvariant(), StringComparison.Ordinal);
    }
}
