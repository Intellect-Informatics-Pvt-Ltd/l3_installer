using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Generates the harness appsettings.Production.json from .epcfg site config.
/// Produces a complete configuration overlay that each harness EXE loads at startup.
///
/// Output location: {DataRoot}\config\harness\appsettings.Production.json
///
/// See docs/adr/ADR-0007-harness-native-deployment.md and
/// docs/test-harness/00-design-overview.md §14.3 for the design rationale.
/// </summary>
public sealed class HarnessConfigGenerator : IHarnessConfigGenerator
{
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ILogger<HarnessConfigGenerator> _logger;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public HarnessConfigGenerator(
        IOptions<InstallerOptions> installerOptions,
        IOptions<ServicesOptions> servicesOptions,
        ILogger<HarnessConfigGenerator> logger)
    {
        _installerOptions = installerOptions;
        _servicesOptions = servicesOptions;
        _logger = logger;
    }

    public async Task GenerateAsync(
        SiteConfigPack siteConfig,
        string outputPath,
        bool demoMode = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Generating harness config for PACS {PacsId} (demo={Demo}) at {Path}.",
            siteConfig.PacsId, demoMode, outputPath);

        var config = BuildConfigDocument(siteConfig, demoMode);
        var json = config.ToJsonString(WriteOptions);

        // Ensure output directory exists
        var directory = Path.GetDirectoryName(outputPath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic write (write-then-rename)
        var tempPath = outputPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, outputPath, overwrite: true);

        _logger.LogInformation("Harness config generated successfully at {Path}.", outputPath);
    }

    private JsonObject BuildConfigDocument(SiteConfigPack siteConfig, bool demoMode)
    {
        var opts = _installerOptions.Value;
        var svc = _servicesOptions.Value;

        var mysqlPort = siteConfig.Services?.MysqlPort ?? svc.MySql.Port;
        var cachePort = siteConfig.Services?.CachePort ?? svc.Cache.Port;
        var eventingPort = siteConfig.Services?.EventingPort ?? svc.Eventing.Port;
        var dataRoot = siteConfig.DataRoot ?? opts.DataRoot;

        var doc = new JsonObject
        {
            ["Pacs"] = new JsonObject
            {
                ["PacsId"] = siteConfig.PacsId,
                ["DataRoot"] = dataRoot,
                ["Tenant"] = siteConfig.StateCode
            },
            ["Harness"] = new JsonObject
            {
                ["TestMode"] = demoMode, // demos get TestMode=true for fault injection
                ["Profile"] = demoMode ? "Demo" : "Installer"
            },
            ["ConnectionStrings"] = BuildConnectionStrings(siteConfig, mysqlPort, cachePort),
            ["Messaging"] = new JsonObject
            {
                ["Kafka"] = new JsonObject
                {
                    ["BootstrapServers"] = $"127.0.0.1:{eventingPort}",
                    ["SecurityProtocol"] = "Plaintext"
                }
            },
            ["Sync"] = BuildSyncConfig(dataRoot),
            ["Observability"] = BuildObservabilityConfig(dataRoot, siteConfig.PacsId),
            ["ErrorHandling"] = new JsonObject
            {
                ["CatalogFiles"] = new JsonArray("config/error-catalog/harness.yaml"),
                ["SuppressStackTraceInProduction"] = true
            }
        };

        // NLDR endpoint configuration
        if (demoMode)
        {
            // Demo mode: NLDR runs on the same machine
            doc["Nldr"] = new JsonObject
            {
                ["IngestUrl"] = "http://127.0.0.1:5201/api/sync/ingest",
                ["FileUploadUrl"] = "http://127.0.0.1:5201/api/files"
            };
        }
        else if (siteConfig.NldrEndpoint is not null)
        {
            // Production: NLDR is a remote central server
            doc["Nldr"] = new JsonObject
            {
                ["IngestUrl"] = $"{siteConfig.NldrEndpoint}/api/sync/ingest",
                ["FileUploadUrl"] = $"{siteConfig.NldrEndpoint}/api/files"
            };
        }

        return doc;
    }

    private static JsonObject BuildConnectionStrings(
        SiteConfigPack siteConfig, int mysqlPort, int cachePort)
    {
        // Connection strings use placeholder passwords — the installer's credential
        // manager replaces these with actual generated passwords during install.
        var pacsDb = string.Create(CultureInfo.InvariantCulture,
            $"Server=127.0.0.1;Port={mysqlPort};Database=epacs_pacs;User=epacs_app;" +
            $"Password=${{PACS_DB_PASSWORD}};SslMode=None;AllowPublicKeyRetrieval=true;" +
            $"ConnectionTimeout=10;DefaultCommandTimeout=30");

        var nldrDb = string.Create(CultureInfo.InvariantCulture,
            $"Server=127.0.0.1;Port={mysqlPort};Database=epacs_nldr;User=epacs_app;" +
            $"Password=${{NLDR_DB_PASSWORD}};SslMode=None;AllowPublicKeyRetrieval=true;" +
            $"ConnectionTimeout=10;DefaultCommandTimeout=30");

        var redis = $"127.0.0.1:{cachePort},abortConnect=false,connectTimeout=5000,syncTimeout=3000";

        return new JsonObject
        {
            ["PacsDb"] = pacsDb,
            ["NldrDb"] = nldrDb,
            ["PacsRedis"] = redis,
            ["NldrRedis"] = $"{redis},defaultDatabase=1"
        };
    }

    private static JsonObject BuildSyncConfig(string dataRoot)
    {
        return new JsonObject
        {
            ["Outbox"] = new JsonObject
            {
                ["PollIntervalMs"] = 500,
                ["BatchSize"] = 50,
                ["ProcessingLockTimeoutSeconds"] = 120
            },
            ["Retry"] = new JsonObject
            {
                ["MaxAttempts"] = 7,
                ["BaseDelayMs"] = 2000,
                ["MaxDelayMs"] = 60000,
                ["JitterFactor"] = 0.2
            },
            ["Circuit"] = new JsonObject
            {
                ["FailureThreshold"] = 5,
                ["OpenDurationSeconds"] = 60,
                ["HalfOpenProbeCount"] = 1
            },
            ["Heartbeat"] = new JsonObject
            {
                ["IntervalSeconds"] = 30
            },
            ["File"] = new JsonObject
            {
                ["ChunkSizeBytes"] = 262144,
                ["MaxConcurrentChunks"] = 4,
                ["StagingPath"] = Path.Combine(dataRoot, "files", "staging"),
                ["QueuePath"] = Path.Combine(dataRoot, "files", "queue"),
                ["MaxFileSizeMb"] = 50
            },
            ["OutboundTopic"] = "epacs.pacs.outbound",
            ["AckTopic"] = "epacs.nldr.acks",
            ["CommandTopic"] = "epacs.nldr.commands",
            ["HeartbeatTopic"] = "epacs.pacs.heartbeat",
            ["DeadLetterTopic"] = "epacs.deadletter"
        };
    }

    private static JsonObject BuildObservabilityConfig(string dataRoot, string pacsId)
    {
        return new JsonObject
        {
            ["ApplicationName"] = $"epacs-harness-{pacsId.ToLowerInvariant()}",
            ["ModuleName"] = "Harness",
            ["Environment"] = "Production",
            ["Sinks"] = new JsonObject
            {
                ["Console"] = new JsonObject { ["Enabled"] = false },
                ["File"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["Path"] = Path.Combine(dataRoot, "logs", "harness", "harness-.json"),
                    ["RollingInterval"] = "Day",
                    ["RetainedFileCountLimit"] = 30,
                    ["FileSizeLimitBytes"] = 104857600
                }
            }
        };
    }
}
