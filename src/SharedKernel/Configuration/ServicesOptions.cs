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

    /// <summary>
    /// Raised from the previous default of 50 to 2000, measured against the estate.
    ///
    /// `ops/ansible/group_vars/all.yml` records the arithmetic and calls it "the number that
    /// surprises people": every L2-R2 service ships `Max Pool Size=2000`, and there are 26 of
    /// them - a theoretical ceiling of 52,000 client connections against a MySQL whose own
    /// default max_connections is 151. Under load the symptom is "Too many connections", which
    /// reads as a database outage rather than as a pool-sizing decision nobody made. A
    /// single-node PACS will not reach 26 x 2000, but 50 is not survivable either.
    /// </summary>
    public int MaxConnections { get; set; } = 2000;

    public string ServiceAccount { get; set; } = "ePACSDbSvc";

    /// <summary>Bytes for the InnoDB buffer pool. Sized for the 8 GB reference machine.</summary>
    public long InnodbBufferPoolBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Durability. Both of these are 1 because a PACS node loses power - that is the design
    /// premise of this whole product, not an edge case. innodb_flush_log_at_trx_commit=1 and
    /// sync_binlog=1 mean a committed voucher is on the platter before the caller is told it
    /// committed. They cost throughput and they are not negotiable for a books-of-account
    /// database with no UPS.
    /// </summary>
    public bool StrictDurability { get; set; } = true;

    /// <summary>
    /// The database the ERP uses. Named here rather than derived, because the schema is
    /// imposed into it and imposing into the wrong one is not reversible.
    /// </summary>
    public string DatabaseName { get; set; } = "epacs";

    /// <summary>Least-privilege account the application connects as.</summary>
    public string ApplicationUser { get; set; } = "epacs_app";

    /// <summary>
    /// The account the service map's health check authenticates as
    /// (`mysqladmin ping --user=healthcheck`). It is granted USAGE and nothing else: a probe
    /// account that can read business data is a credential sitting in a service definition.
    /// </summary>
    public string HealthCheckUser { get; set; } = "healthcheck";
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
