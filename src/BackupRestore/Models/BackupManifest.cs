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

    /// <summary>How the data key is protected. Null on a package written before encryption existed (2026-09-13).</summary>
    public BackupKeyWrap? KeyWrap { get; init; }

    /// <summary>Where the engine wrote this package. Set by the engine for the caller; never serialised into the package.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PackagePath { get; init; }
}

/// <summary>
/// The per-backup data key, wrapped. <c>Local</c> is under the node's own key-encryption key
/// (same-node restore); <c>Recovery</c> is under the state's recovery public key (RSA-OAEP), so a
/// replacement node with no secret store can be restored by whoever holds that private key.
/// </summary>
public sealed record BackupKeyWrap
{
    public required string Algorithm { get; init; }
    public required string Local { get; init; }
    public string? Recovery { get; init; }
    public string? RecoveryKeyId { get; init; }
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

    /// <summary>What "signed" means here: "HMAC-SHA256(local KEK)" on a node with no signing certificate.</summary>
    public string? SignatureType { get; init; }
}

public sealed record BackupFileEntry
{
    /// <summary>Path inside the package. Ends in .enc on an encrypted package.</summary>
    public required string RelativePath { get; init; }

    /// <summary>SHA-256 of the PLAINTEXT - what the file must hash to after decryption.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Plaintext length.</summary>
    public required long SizeBytes { get; init; }

    public required string Category { get; init; } // db, config, keys, attachments, sync, logs

    /// <summary>SHA-256 of the file as it sits in the package - verifiable without the key.</summary>
    public string? EncryptedSha256 { get; init; }

    public long? EncryptedSizeBytes { get; init; }
}
