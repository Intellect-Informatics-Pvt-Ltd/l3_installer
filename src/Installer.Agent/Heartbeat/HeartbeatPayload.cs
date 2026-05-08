using System.Text.Json.Serialization;

namespace Installer.Agent.Heartbeat;

/// <summary>
/// Payload sent to CoopsIndia Dashboard on each heartbeat.
/// Contains PACS identity, connectivity status, sync status, and health.
/// </summary>
public sealed record HeartbeatPayload
{
    [JsonPropertyName("pacs_id")]
    public required string PacsId { get; init; }

    [JsonPropertyName("state_id")]
    public required string StateId { get; init; }

    [JsonPropertyName("dccb_id")]
    public required string DccbId { get; init; }

    [JsonPropertyName("branch_id")]
    public required string BranchId { get; init; }

    [JsonPropertyName("online_since")]
    public required DateTimeOffset OnlineSince { get; init; }

    [JsonPropertyName("last_sync_timestamp")]
    public DateTimeOffset? LastSyncTimestamp { get; init; }

    [JsonPropertyName("pending_outbox_count")]
    public int PendingOutboxCount { get; init; }

    [JsonPropertyName("pending_files_count")]
    public int PendingFilesCount { get; init; }

    [JsonPropertyName("disk_usage_percent")]
    public int DiskUsagePercent { get; init; }

    [JsonPropertyName("stack_version")]
    public required string StackVersion { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("last_backup_at")]
    public DateTimeOffset? LastBackupAt { get; init; }

    [JsonPropertyName("health_status")]
    public required string HealthStatus { get; init; }

    [JsonPropertyName("connectivity_mode")]
    public string? ConnectivityMode { get; init; }

    [JsonPropertyName("uptime_seconds")]
    public long UptimeSeconds { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
