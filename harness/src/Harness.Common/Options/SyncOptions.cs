namespace Harness.Common.Options;

/// <summary>Top-level sync configuration (§14.1).</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public OutboxOptions    Outbox      { get; set; } = new();
    public RetryOptions     Retry       { get; set; } = new();
    public CircuitOptions   Circuit     { get; set; } = new();
    public HeartbeatOptions Heartbeat   { get; set; } = new();
    public FileOptions      File        { get; set; } = new();
    public PriorityOptions  Priority    { get; set; } = new();
    public ClockDriftOptions ClockDrift { get; set; } = new();

    public string OutboundTopic   { get; set; } = "epacs.pacs.outbound";
    public string AcksTopic       { get; set; } = "epacs.nldr.acks";
    public string CommandsTopic   { get; set; } = "epacs.nldr.commands";
    public string HeartbeatTopic  { get; set; } = "epacs.pacs.heartbeat";
    public string DeadletterTopic { get; set; } = "epacs.deadletter";

    /// <summary>When true, also POST to NLDR HTTP ingest in addition to Kafka.</summary>
    public bool    UseHttpTransport { get; set; }
    public string? NldrIngestUrl   { get; set; }
}

public sealed class OutboxOptions
{
    /// <summary>Milliseconds to sleep between outbox polls when idle.</summary>
    public int PollIntervalMs { get; set; } = 500;
    public int BatchSize { get; set; } = 50;
    /// <summary>Seconds before a stale IN_FLIGHT row is released by LockReaper.</summary>
    public int ProcessingLockTimeoutSeconds { get; set; } = 120;
    /// <summary>Days before ACKed rows are pruned to archive table.</summary>
    public int OutboxRetentionDays { get; set; } = 90;
    /// <summary>Retry count after which a row is moved to DEADLETTER.</summary>
    public int QuarantineAfterAttempts { get; set; } = 10;
}

public sealed class RetryOptions
{
    public int  MaxAttempts       { get; set; } = 7;
    public int  BaseDelayMs       { get; set; } = 2000;
    public int  MaxDelayMs        { get; set; } = 60000;
    public double JitterFactor    { get; set; } = 0.2;
    public bool RespectRetryAfter { get; set; } = true;
}

public sealed class CircuitOptions
{
    /// <summary>Consecutive failures before circuit opens.</summary>
    public int FailureThreshold   { get; set; } = 5;
    public int OpenDurationSeconds { get; set; } = 60;
    public int HalfOpenProbeCount { get; set; } = 1;
}

public sealed class HeartbeatOptions
{
    public int IntervalSeconds     { get; set; } = 30;
    /// <summary>Window in which NLDR must ACK a heartbeat for the node to be "Online".</summary>
    public int OnlineWindowSeconds { get; set; } = 90;
}

public sealed class FileOptions
{
    public int    ChunkSizeBytes         { get; set; } = 262_144;
    public int    MaxConcurrentChunks    { get; set; } = 4;
    public string StagingPath            { get; set; } = "${DataRoot}/files/staging";
    public string QueuePath              { get; set; } = "${DataRoot}/files/queue";
    public int    MaxFileSizeMb          { get; set; } = 50;
    public int    SmallFileThresholdKb   { get; set; } = 1024;
}

public sealed class PriorityOptions
{
    public int VoucherDefault  { get; set; } = 10;
    public int LoanAmendment   { get; set; } = 20;
    public int LoanDefault     { get; set; } = 30;
    public int FileSmall       { get; set; } = 50;
    public int FileLarge       { get; set; } = 80;
    public int Heartbeat       { get; set; } = 200;
}

public sealed class ClockDriftOptions
{
    /// <summary>Warn when drift exceeds this threshold (seconds).</summary>
    public int MaxAllowedSeconds { get; set; } = 30;
    /// <summary>Pause outbound sync when drift exceeds this threshold (seconds).</summary>
    public int BlockingSeconds   { get; set; } = 300;
}
