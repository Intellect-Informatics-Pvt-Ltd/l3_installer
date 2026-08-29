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

    /// <summary>
    /// The application services this payload contains, keyed by service name.
    ///
    /// WHY A DICTIONARY AND NOT MORE NAMED PROPERTIES. Until 2026-08-29 this class had exactly
    /// six fixed properties — MySql, Cache, Eventing, Web, Sync, Agent — and `Web` was a single
    /// entry representing "the application". That shape can describe the stand-in payload in
    /// `harness/`. It cannot describe L2-R2, which is **26 services** (25 middleware plus the
    /// ERPClient UI, per `ops/ansible/group_vars/all.yml`), each with its own port, its own
    /// service account and its own start order.
    ///
    /// A fixed shape was therefore a hard ceiling on the whole bundling intent: no amount of
    /// work elsewhere could have pointed the chassis at the real stack while the configuration
    /// model could only name one application.
    ///
    /// The infrastructure services above stay named, because each genuinely has different
    /// settings — a buffer pool is not a heap size is not a chunk size. Only the application
    /// tier is homogeneous enough to be a collection.
    ///
    /// Case-insensitive on purpose: configuration keys arrive from JSON, environment variables
    /// and a service map, and `l3_FAS` / `l3_fas` naming the same service in two of them is a
    /// mistake nobody would find quickly.
    /// </summary>
    public Dictionary<string, ApplicationServiceOptions> Applications { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One application service in the payload. Deliberately small: anything that belongs to the
/// service's own configuration lives in its appsettings, not here. This carries only what the
/// INSTALLER needs to place, register and address it.
/// </summary>
public sealed class ApplicationServiceOptions
{
    /// <summary>The HTTP port it binds. Substituted into templates as ${Service:&lt;name&gt;:Port}.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Start order, mirroring the estate's own tiering: master-data services must be listening
    /// before the modules that resolve masters through them, or the first requests after a
    /// restart fail in ways that look like data problems. Shutdown walks this backwards.
    /// </summary>
    public int StartOrder { get; set; } = 30;

    /// <summary>Windows service account. Least privilege; not LocalSystem for an app service.</summary>
    public string ServiceAccount { get; set; } = "ePACSAppSvc";

    /// <summary>
    /// Health endpoint path, if the service has one.
    ///
    /// Nullable because in L2-R2 today most do not: 7 of 26 services expose any health endpoint
    /// and **none** expose the `/health/live` and `/health/ready` this installer's service map
    /// is written against. A null here means the installer cannot gate on readiness for that
    /// service and must say so rather than assume it is up.
    /// </summary>
    public string? HealthPath { get; set; }
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
