namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for ePACS service ports and data directories.
/// Binds to the <c>Services</c> section of appsettings.json.
/// </summary>
public sealed class ServicesOptions
{
    public const string SectionName = "Services";

    public MySqlServiceOptions MySql { get; set; } = new();
    public CacheServiceOptions Cache { get; set; } = new();
    public EventingServiceOptions Eventing { get; set; } = new();
    public WebServiceOptions Web { get; set; } = new();
    public SyncServiceOptions Sync { get; set; } = new();
    public AgentServiceOptions Agent { get; set; } = new();
}

public sealed class MySqlServiceOptions
{
    public int Port { get; set; } = 3306;
    public string DataDir { get; set; } = "${DataRoot}\\mysql\\data";
    public string LogDir { get; set; } = "${DataRoot}\\mysql\\logs";
    public int MaxConnections { get; set; } = 50;
    public string ServiceAccount { get; set; } = "ePACSDbSvc";
}

public sealed class CacheServiceOptions
{
    public int Port { get; set; } = 6379;
    public string DataDir { get; set; } = "${DataRoot}\\cache";
    public int MaxMemoryMb { get; set; } = 512;
    public string ServiceAccount { get; set; } = "ePACSCacheSvc";
}

public sealed class EventingServiceOptions
{
    public int Port { get; set; } = 9092;
    public string DataDir { get; set; } = "${DataRoot}\\eventing\\data";
    public string LogDir { get; set; } = "${DataRoot}\\eventing\\logs";
    public int HeapSizeMb { get; set; } = 512;
    public string ServiceAccount { get; set; } = "ePACSEventSvc";
    public string[] PreCreateTopics { get; set; } = [
        "epacs.local.sync-ready",
        "epacs.local.dead-letter",
        "epacs.local.commands"
    ];
}

public sealed class WebServiceOptions
{
    public int HttpsPort { get; set; } = 443;
    public string ServiceAccount { get; set; } = "ePACSAppSvc";
}

public sealed class SyncServiceOptions
{
    public int HealthPort { get; set; } = 5080;
    public string ServiceAccount { get; set; } = "ePACSSyncSvc";
    public int ChunkSizeBytes { get; set; } = 1048576; // 1 MB default (4G)
    public int MaxRetryAttempts { get; set; } = 10;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public int CircuitBreakerHalfOpenSeconds { get; set; } = 300;
}

public sealed class AgentServiceOptions
{
    public int HealthPort { get; set; } = 5090;
}
