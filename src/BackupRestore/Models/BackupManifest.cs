namespace BackupRestore.Models;

/// <summary>
/// Manifest for a backup package. Signed and verified during restore.
/// Follows the BRD 13.2 backup manifest structure.
/// </summary>
public sealed record BackupManifest
{
    public required string BackupId { get; init; }
    public required string PacsId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string CreatedBy { get; init; }
    public required BackupType BackupType { get; init; }
    public required string StackVersion { get; init; }
    public required int SchemaVersion { get; init; }
    public required string Encryption { get; init; }
    public required string KeyProtection { get; init; }
    public string? CertificateThumbprint { get; init; }
    public required BackupIncludes Includes { get; init; }
    public required BackupValidation Validation { get; init; }
    public IReadOnlyList<BackupFileEntry> Files { get; init; } = [];
}

public enum BackupType
{
    PreUpgrade,
    DailyIncremental,
    WeeklyFull,
    Manual,
    PreRestore
}

public sealed record BackupIncludes
{
    public bool MySql { get; init; } = true;
    public bool Attachments { get; init; } = true;
    public bool Configuration { get; init; } = true;
    public bool Keys { get; init; } = true;
    public bool SyncState { get; init; } = true;
}

public sealed record BackupValidation
{
    public bool ChecksumVerified { get; init; }
    public bool DumpReadable { get; init; }
    public bool ManifestSigned { get; init; }
}

public sealed record BackupFileEntry
{
    public required string RelativePath { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required string Category { get; init; } // db, config, keys, attachments, sync, logs
}
