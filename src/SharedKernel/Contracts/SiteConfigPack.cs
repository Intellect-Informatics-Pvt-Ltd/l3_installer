namespace SharedKernel.Contracts;

/// <summary>
/// Represents a signed Site Configuration Pack (.epcfg) that provides
/// site-specific configuration for a PACS node.
/// Distributed out-of-band (USB, courier) and verified by the installer.
/// </summary>
public sealed record SiteConfigPack
{
    public required string Signature { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public required string PacsId { get; init; }
    public required string StateCode { get; init; }
    public string? DistrictCode { get; init; }
    public string Language { get; init; } = "en";
    public required string DataRoot { get; init; }
    public string? BinaryRoot { get; init; }
    public string? NldrEndpoint { get; init; }
    public string? NldrClientCertThumbprint { get; init; }
    public string[] BackupTargets { get; init; } = [];
    public BackupScheduleConfig? BackupSchedule { get; init; }
    public LogRetentionConfig? LogRetentionDays { get; init; }
    public int AttachmentQuotaGb { get; init; } = 50;
    public SiteCoordinatesConfig? SiteCoordinates { get; init; }
    public MonitoringConfig? Monitoring { get; init; }
    public ServicePortsConfig? Services { get; init; }
    public TraceabilityConfig? Traceability { get; init; }
    public string? OverrideToken { get; init; }
}

public sealed record BackupScheduleConfig
{
    public string Daily { get; init; } = "02:00";
    public string WeeklyDay { get; init; } = "Sunday";
    public string WeeklyTime { get; init; } = "03:00";
}

public sealed record LogRetentionConfig
{
    public int Application { get; init; } = 30;
    public int Audit { get; init; } = 90;
    public int MySql { get; init; } = 7;
}

public sealed record SiteCoordinatesConfig
{
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public int AccuracyMeters { get; init; }
    public string? Source { get; init; }
}

public sealed record MonitoringConfig
{
    public int HealthPollIntervalSeconds { get; init; } = 60;
    public int DiskCheckIntervalSeconds { get; init; } = 900;
    public int DriftCheckIntervalSeconds { get; init; } = 3600;
    public int CertExpiryCheckIntervalSeconds { get; init; } = 21600;
    public int ClockDriftCheckIntervalSeconds { get; init; } = 1800;
}

public sealed record ServicePortsConfig
{
    public int MysqlPort { get; init; } = 3306;
    public int CachePort { get; init; } = 6379;
    public int EventingPort { get; init; } = 9092;
    public int WebHttpsPort { get; init; } = 443;
}

public sealed record TraceabilityConfig
{
    public Dictionary<string, AnomalyRuleConfig>? AnomalyRules { get; init; }
}

public sealed record AnomalyRuleConfig
{
    public required string Mode { get; init; }
    public string? Severity { get; init; }
    public string? BusinessHoursStart { get; init; }
    public string? BusinessHoursEnd { get; init; }
    public int? BaselineSeedDays { get; init; }
    public double? SpikeThresholdSigma { get; init; }
}
