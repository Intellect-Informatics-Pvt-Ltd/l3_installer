namespace Installer.Actions.Install;

/// <summary>
/// Creates the durable data root directory structure with correct NTFS ACLs.
/// All paths are sourced from configuration — zero hardcoding.
/// </summary>
public interface IDataRootInitializer
{
    /// <summary>
    /// Creates the data root and all required subdirectories with appropriate ACLs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
