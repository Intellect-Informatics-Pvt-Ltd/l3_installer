using System.Text.Json.Serialization;

namespace SharedKernel.Packs;

/// <summary>What a pack is (ADR-0011).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackType>))]
public enum PackType
{
    /// <summary>Society → state: the society's rows in its PacsId scope.</summary>
    Ledger,

    /// <summary>State → society: masters, product definitions, decisions on deferred requests, configuration.</summary>
    Policy,

    /// <summary>The zeroth policy pack: the society's whole scope at birth (the carve-out, <c>.epdata</c>).</summary>
    SiteData
}

/// <summary>
/// The envelope's <c>manifest.json</c>, exactly as ADR-0011 fixes it. Everything a reader checks
/// is here; nothing under <c>data/</c> is opened until <c>manifest.sig</c> verifies over these bytes.
/// </summary>
public sealed record PackManifest
{
    public int FormatVersion { get; init; } = 1;
    public required string PacsId { get; init; }
    public required string State { get; init; }
    public required PackType PackType { get; init; }

    /// <summary>Monotone per (pacs_id, pack_type). The genesis pack is 1.</summary>
    public required long PackSeq { get; init; }

    /// <summary>SHA-256 of the previous pack's manifest.json bytes; <see cref="Genesis"/> for seq 1.</summary>
    public required string PrevPackHash { get; init; }

    /// <summary>For a ledger pack: the source's GTID set (or binlog position) at snapshot time. For a policy pack: the source snapshot identifier.</summary>
    public string? WatermarkFrom { get; init; }
    public string? WatermarkTo { get; init; }

    /// <summary>The schema the pack was cut against (MySqlSchemaFingerprinter). A pack is never applied across a different one.</summary>
    public required string SchemaFingerprint { get; init; }

    public required DateTimeOffset ProducedAt { get; init; }
    public required string ProducerVersion { get; init; }

    /// <summary>What v0 packs are: whole snapshots of the scope, idempotent to apply. Stated so a reader knows what a count means.</summary>
    public string Mode { get; init; } = "snapshot";

    /// <summary>"none", or the wrap that protects the data files - see <see cref="PackEncryption"/>.</summary>
    public PackEncryption? Encryption { get; init; }

    /// <summary>One entry per data file: table, file, row count, plaintext hash, and the hash of the file as it sits in the pack.</summary>
    public required IReadOnlyList<PackTable> Tables { get; init; }

    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
}

public sealed record PackTable
{
    public required string Table { get; init; }
    public required string File { get; init; }
    public required long Rows { get; init; }
    public required string Sha256 { get; init; }
    public string? EncryptedSha256 { get; init; }
    public required long SizeBytes { get; init; }
}

/// <summary>The data files are AES-256-GCM under a per-pack key, wrapped RSA-OAEP to the receiver's public key.</summary>
public sealed record PackEncryption
{
    public required string Algorithm { get; init; }
    public required string RecipientKeyId { get; init; }
    public required string WrappedKey { get; init; }
}
