namespace SharedKernel.Configuration;

/// <summary>
/// Configuration for bidirectional file/attachment synchronization.
/// Feature is enable/disable controlled — when disabled, files accumulate locally.
/// Binds to the <c>FileSync</c> section of appsettings.json.
/// </summary>
public sealed class FileSyncOptions
{
    public const string SectionName = "FileSync";

    /// <summary>Whether file sync is enabled. Controllable via ePACS application UI.</summary>
    public bool Enabled { get; set; }

    /// <summary>Interval between file scan cycles in seconds.</summary>
    public int ScanIntervalSeconds { get; set; } = 300;

    /// <summary>Maximum upload batch size per sync cycle in MB.</summary>
    public int MaxUploadBatchSizeMb { get; set; } = 50;

    /// <summary>Transport configuration.</summary>
    public FileSyncTransportOptions Transport { get; set; } = new();

    /// <summary>Deduplication settings.</summary>
    public FileSyncDeduplicationOptions Deduplication { get; set; } = new();

    /// <summary>Inbound (NLDR → PACS) sync settings.</summary>
    public FileSyncInboundOptions Inbound { get; set; } = new();

    /// <summary>Maximum file size in MB to hash during peak hours (larger files deferred).</summary>
    public int MaxHashFileSizeMbDuringPeakHours { get; set; } = 50;

    /// <summary>Peak hours definition (HH:mm format).</summary>
    public string PeakHoursStart { get; set; } = "09:00";

    /// <summary>Peak hours end (HH:mm format).</summary>
    public string PeakHoursEnd { get; set; } = "18:00";
}

public sealed class FileSyncTransportOptions
{
    /// <summary>Primary transport: "HTTPS" or "SFTP".</summary>
    public string Primary { get; set; } = "HTTPS";

    /// <summary>Fallback transport (used if primary fails). Null = no fallback.</summary>
    public string? Fallback { get; set; } = "SFTP";

    /// <summary>HTTPS multipart upload configuration.</summary>
    public HttpsTransportOptions Https { get; set; } = new();

    /// <summary>SFTP transport configuration.</summary>
    public SftpTransportOptions Sftp { get; set; } = new();
}

public sealed class HttpsTransportOptions
{
    /// <summary>NLDR file upload endpoint URL.</summary>
    public string Endpoint { get; set; } = "https://nldr.epacs.gov.in/api/v1.0/files";

    /// <summary>Chunk size for multipart upload in bytes.</summary>
    public int ChunkSizeBytes { get; set; } = 1048576; // 1 MB

    /// <summary>Upload timeout per chunk in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class SftpTransportOptions
{
    /// <summary>SFTP server hostname.</summary>
    public string Host { get; set; } = "sftp.nldr.epacs.gov.in";

    /// <summary>SFTP port.</summary>
    public int Port { get; set; } = 22;

    /// <summary>SFTP username (typically pacs_id).</summary>
    public string Username { get; set; } = "";

    /// <summary>Path to SSH private key file.</summary>
    public string KeyPath { get; set; } = "${DataRoot}\\keys\\sftp_key";

    /// <summary>Remote base path on SFTP server.</summary>
    public string RemotePath { get; set; } = "/";
}

public sealed class FileSyncDeduplicationOptions
{
    /// <summary>Hash algorithm for content deduplication.</summary>
    public string Algorithm { get; set; } = "SHA-256";

    /// <summary>Skip upload if content hash is unchanged since last sync.</summary>
    public bool SkipIfHashUnchanged { get; set; } = true;
}

public sealed class FileSyncInboundOptions
{
    /// <summary>Whether inbound (NLDR → PACS) file sync is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Interval between inbound poll cycles in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 600;
}
