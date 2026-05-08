using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Install;

/// <summary>
/// Deploys binaries using the side-by-side release pattern.
/// Layout: C:\Program Files\ePACS\releases\<version>\ with 'current' as a directory junction.
/// </summary>
public sealed class BinaryDeployer : IBinaryDeployer
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<BinaryDeployer> _logger;

    public BinaryDeployer(IOptions<InstallerOptions> options, ILogger<BinaryDeployer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task DeployAsync(string stagingDirectory, string version, CancellationToken cancellationToken = default)
    {
        var releasesPath = _options.Value.ReleasesPath;
        var versionPath = Path.Combine(releasesPath, version);

        _logger.LogInformation("Deploying version {Version} to {Path}.", version, versionPath);

        // Ensure releases directory exists
        if (!Directory.Exists(releasesPath))
        {
            Directory.CreateDirectory(releasesPath);
        }

        // If version directory already exists (retry after power-cut), remove it
        if (Directory.Exists(versionPath))
        {
            _logger.LogWarning("Version directory already exists (possible retry). Removing: {Path}.", versionPath);
            Directory.Delete(versionPath, recursive: true);
        }

        // Copy staging to release directory
        await Task.Run(() => CopyDirectory(stagingDirectory, versionPath), cancellationToken);

        _logger.LogInformation("Binaries deployed to {Path}.", versionPath);

        // Create/update the 'current' junction
        await SwitchCurrentAsync(version);
    }

    public Task SwitchCurrentAsync(string version)
    {
        var junctionPath = _options.Value.CurrentJunctionPath;
        var targetPath = Path.Combine(_options.Value.ReleasesPath, version);

        if (!Directory.Exists(targetPath))
        {
            throw new DirectoryNotFoundException($"Release directory not found: {targetPath}");
        }

        _logger.LogInformation("Switching 'current' junction to version {Version}.", version);

        // Remove existing junction if present
        if (Directory.Exists(junctionPath))
        {
            // On Windows, Directory.Delete removes junctions without following them
            Directory.Delete(junctionPath, recursive: false);
        }

        // Create directory junction (symlink on non-Windows for dev/test)
        Directory.CreateSymbolicLink(junctionPath, targetPath);

        _logger.LogInformation("Junction '{Junction}' now points to '{Target}'.", junctionPath, targetPath);
        return Task.CompletedTask;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(destination, relativePath);
            var destDir = Path.GetDirectoryName(destFile);

            if (destDir is not null && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(file, destFile, overwrite: true);
        }
    }
}
