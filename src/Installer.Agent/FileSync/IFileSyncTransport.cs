namespace Installer.Agent.FileSync;

/// <summary>
/// Transport abstraction for file synchronization.
/// Implementations: HTTPS multipart upload, SFTP.
/// Selected via configuration — both options available.
/// </summary>
public interface IFileSyncTransport
{
    /// <summary>Transport name for logging.</summary>
    string Name { get; }

    /// <summary>
    /// Uploads a file to the remote destination.
    /// </summary>
    /// <param name="localPath">Local file path.</param>
    /// <param name="remotePath">Remote destination path (relative).</param>
    /// <param name="contentHash">SHA-256 hash for verification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if upload succeeded.</returns>
    Task<bool> UploadAsync(string localPath, string remotePath, string contentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file from the remote source.
    /// </summary>
    /// <param name="remotePath">Remote file path.</param>
    /// <param name="localPath">Local destination path.</param>
    /// <param name="expectedHash">Expected SHA-256 hash for verification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded and hash verified.</returns>
    Task<bool> DownloadAsync(string remotePath, string localPath, string expectedHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists files available for download (inbound sync).
    /// </summary>
    /// <param name="remotePath">Remote directory to list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of remote file entries.</returns>
    Task<IReadOnlyList<RemoteFileEntry>> ListRemoteFilesAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests connectivity to the remote endpoint.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if connection is available.</returns>
    Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a file available on the remote endpoint for download.
/// </summary>
public sealed record RemoteFileEntry
{
    public required string Path { get; init; }
    public required string ContentHash { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastModified { get; init; }
}
