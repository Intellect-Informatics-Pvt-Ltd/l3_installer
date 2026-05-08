using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Install;

/// <summary>
/// Creates the durable data root (D:\ePACSData\) with all required subdirectories.
/// Directory structure and paths are fully configurable via InstallerOptions and ServicesOptions.
/// ACL application is Windows-specific and handled separately.
/// </summary>
public sealed class DataRootInitializer : IDataRootInitializer
{
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ILogger<DataRootInitializer> _logger;

    public DataRootInitializer(
        IOptions<InstallerOptions> installerOptions,
        IOptions<ServicesOptions> servicesOptions,
        ILogger<DataRootInitializer> logger)
    {
        _installerOptions = installerOptions;
        _servicesOptions = servicesOptions;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = _installerOptions.Value.DataRoot;
        _logger.LogInformation("Initializing data root at {DataRoot}.", dataRoot);

        var directories = GetRequiredDirectories(dataRoot);

        foreach (var dir in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                _logger.LogInformation("Created directory: {Directory}.", dir);
            }
            else
            {
                _logger.LogInformation("Directory already exists: {Directory}.", dir);
            }
        }

        _logger.LogInformation("Data root initialization complete. Created {Count} directories.", directories.Count);
        return Task.CompletedTask;
    }

    private List<string> GetRequiredDirectories(string dataRoot)
    {
        return
        [
            dataRoot,
            // MySQL
            Path.Combine(dataRoot, "mysql", "data"),
            Path.Combine(dataRoot, "mysql", "logs"),
            // Cache (Garnet)
            Path.Combine(dataRoot, "cache"),
            // Eventing (Kafka)
            Path.Combine(dataRoot, "eventing", "data"),
            Path.Combine(dataRoot, "eventing", "logs"),
            // Application
            Path.Combine(dataRoot, "attachments"),
            Path.Combine(dataRoot, "keys"),
            Path.Combine(dataRoot, "backups"),
            Path.Combine(dataRoot, "sync"),
            // Logs (per-service)
            Path.Combine(dataRoot, "logs", "installer"),
            Path.Combine(dataRoot, "logs", "mysql"),
            Path.Combine(dataRoot, "logs", "cache"),
            Path.Combine(dataRoot, "logs", "eventing"),
            Path.Combine(dataRoot, "logs", "web"),
            Path.Combine(dataRoot, "logs", "sync"),
            Path.Combine(dataRoot, "logs", "agent"),
            // Config
            Path.Combine(dataRoot, "config"),
            // Temp (installer staging)
            Path.Combine(dataRoot, "temp"),
            // Installer state
            Path.Combine(dataRoot, "installer"),
        ];
    }
}
