using System.Security.Cryptography;
using System.Text.Json;
using SharedKernel.Crypto;
using SharedKernel.Security;

namespace SharedKernel.Packs;

/// <summary>Why a pack was refused. Every refusal names one of these, so an operator and a test can tell them apart.</summary>
public enum PackRefusal
{
    Missing,
    NotSigned,
    Signature,
    Hash,
    Sequence,
    Chain,
    Replay,
    Scope,
    Schema,
    Encryption
}

public sealed class PackException : Exception
{
    public PackRefusal Refusal { get; }

    public PackException(PackRefusal refusal, string message) : base(message) => Refusal = refusal;

    public PackException(PackRefusal refusal, string message, Exception inner) : base(message, inner) => Refusal = refusal;
}

/// <summary>
/// The envelope on disk (ADR-0011): <c>manifest.json</c>, <c>manifest.sig</c> (detached CMS over
/// the manifest bytes), <c>data/&lt;table&gt;.sql</c> (or <c>.sql.enc</c>). The reader checks in
/// this order and stops at the first failure: signature, then sequence and chain against the
/// ledger, then scope and schema, then the hash of every data file. Nothing under <c>data/</c>
/// is opened before the signature verifies.
/// </summary>
public static class PackEnvelope
{
    public const string ManifestFile = "manifest.json";
    public const string SignatureFile = "manifest.sig";
    public const string DataDirectory = "data";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Writes the envelope. <paramref name="dataFiles"/> maps table → plaintext SQL file already
    /// in <paramref name="directory"/>/data; the manifest's per-table hashes are measured here.
    /// With <paramref name="recipientPublicKeyPem"/> the data files are encrypted to that key
    /// and the plaintext deleted; without it they stay in clear and the manifest says
    /// <c>encryption: none</c>.
    /// </summary>
    public static async Task<PackManifest> WriteAsync(
        string directory,
        PackManifest manifest,
        IReadOnlyDictionary<string, long> rowCounts,
        ICodeSigner? signer,
        string? recipientPublicKeyPem,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(rowCounts);
        var dataDir = Path.Combine(directory, DataDirectory);
        Directory.CreateDirectory(dataDir);

        byte[]? dek = null;
        PackEncryption? encryption = null;
        if (recipientPublicKeyPem is not null)
        {
            dek = BackupCrypto.NewKey();
            encryption = new PackEncryption
            {
                Algorithm = "AES-256-GCM(data) / RSA-OAEP-SHA256(key)",
                RecipientKeyId = BackupCrypto.PublicKeyId(recipientPublicKeyPem),
                WrappedKey = BackupCrypto.WrapKeyToPublicKey(dek, recipientPublicKeyPem)
            };
        }

        var tables = new List<PackTable>();
        foreach (var (table, rows) in rowCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var plain = Path.Combine(dataDir, table + ".sql");
            if (!File.Exists(plain))
            {
                throw new InvalidOperationException($"No data file for table {table} at {plain}; the exporter counted it but did not write it.");
            }

            var plainHash = await HashAsync(plain, ct);
            var size = new FileInfo(plain).Length;
            var file = table + ".sql";
            string? encHash = null;
            if (dek is not null)
            {
                var enc = plain + BackupCrypto.Suffix;
                await BackupCrypto.EncryptFileAsync(plain, enc, dek, ct);
                File.Delete(plain);
                file += BackupCrypto.Suffix;
                encHash = await HashAsync(enc, ct);
            }

            tables.Add(new PackTable { Table = table, File = file, Rows = rows, Sha256 = plainHash, EncryptedSha256 = encHash, SizeBytes = size });
        }

        if (dek is not null)
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        var final = manifest with { Tables = tables, Encryption = encryption };
        var manifestPath = Path.Combine(directory, ManifestFile);
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(final, Json), ct);

        if (signer is not null)
        {
            await signer.SignFileAsync(manifestPath, Path.Combine(directory, SignatureFile), ct);
        }

        return final;
    }

    /// <summary>SHA-256 of the manifest bytes as written — what the NEXT pack carries as <c>PrevPackHash</c>.</summary>
    public static async Task<string> ManifestHashAsync(string directory, CancellationToken ct = default) =>
        await HashAsync(Path.Combine(directory, ManifestFile), ct);

    /// <summary>
    /// Reads and checks, in ADR-0011's order. <paramref name="expectedPrevHash"/> and
    /// <paramref name="expectedSeq"/> come from the receiver's ledger; a replay (seq already
    /// applied) is reported as <see cref="PackRefusal.Replay"/> so the caller can acknowledge it
    /// without re-applying.
    /// </summary>
    public static async Task<PackManifest> ReadAndVerifyAsync(
        string directory,
        ICodeSigner verifier,
        string? expectedSignerThumbprint,
        bool requireSignature,
        string expectedPacsId,
        long expectedSeq,
        string expectedPrevHash,
        string? expectedSchemaFingerprint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var manifestPath = Path.Combine(directory, ManifestFile);
        var sigPath = Path.Combine(directory, SignatureFile);
        if (!File.Exists(manifestPath))
        {
            throw new PackException(PackRefusal.Missing, $"{directory} carries no {ManifestFile}; it is not a pack.");
        }

        // 1. Signature, before a byte of content.
        if (File.Exists(sigPath))
        {
            var sig = await verifier.VerifyFileAsync(manifestPath, sigPath, expectedSignerThumbprint, ct);
            if (!sig.Valid)
            {
                throw new PackException(PackRefusal.Signature, $"{directory}: the manifest signature does not verify ({sig.ErrorMessage}). Nothing was read.");
            }
        }
        else if (requireSignature)
        {
            throw new PackException(PackRefusal.NotSigned, $"{directory}: the pack is unsigned and unsigned packs are refused here. Nothing was read.");
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
        PackManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PackManifest>(bytes) ?? throw new PackException(PackRefusal.Missing, "empty manifest");
        }
        catch (JsonException ex)
        {
            throw new PackException(PackRefusal.Missing, $"{directory}: the manifest does not parse: {ex.Message}", ex);
        }

        // 2. Scope.
        if (!string.Equals(manifest.PacsId, expectedPacsId, StringComparison.Ordinal))
        {
            throw new PackException(PackRefusal.Scope, $"{directory}: the pack is for {manifest.PacsId}; this node is {expectedPacsId}. Refused.");
        }

        // 3. Sequence and chain.
        if (manifest.PackSeq < expectedSeq)
        {
            throw new PackException(PackRefusal.Replay, $"{directory}: pack {manifest.PackSeq} was already applied (next expected is {expectedSeq}). Acknowledged, not re-applied.");
        }

        if (manifest.PackSeq > expectedSeq)
        {
            throw new PackException(PackRefusal.Sequence, $"{directory}: pack {manifest.PackSeq} arrived but {expectedSeq} has not; the gap is refused, not skipped.");
        }

        if (!string.Equals(manifest.PrevPackHash, expectedPrevHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackException(PackRefusal.Chain, $"{directory}: pack {manifest.PackSeq} does not chain to the last applied pack (prev hash differs). Refused.");
        }

        // 4. Schema.
        if (expectedSchemaFingerprint is not null && !string.Equals(manifest.SchemaFingerprint, expectedSchemaFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackException(PackRefusal.Schema, $"{directory}: the pack was cut against schema {manifest.SchemaFingerprint[..Math.Min(12, manifest.SchemaFingerprint.Length)]}…, this node runs {expectedSchemaFingerprint[..Math.Min(12, expectedSchemaFingerprint.Length)]}…. Upgrade first (ADR-0013).");
        }

        // 5. Every data file, by the hash that can be checked without a key.
        foreach (var table in manifest.Tables)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, DataDirectory, table.File);
            if (!File.Exists(path))
            {
                throw new PackException(PackRefusal.Hash, $"{directory}: {table.File} is missing.");
            }

            var actual = await HashAsync(path, ct);
            var expected = table.EncryptedSha256 ?? table.Sha256;
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackException(PackRefusal.Hash, $"{directory}: {table.File} does not hash to what the manifest recorded.");
            }
        }

        return manifest;
    }

    /// <summary>
    /// Opens the data files for reading: decrypts to <paramref name="staging"/> when the pack is
    /// encrypted (needs the recipient's private key), else returns the pack's own data directory.
    /// Every plaintext is hashed against the manifest.
    /// </summary>
    public static async Task<string> OpenDataAsync(string directory, PackManifest manifest, string staging, string? recipientPrivateKeyPem, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var dataDir = Path.Combine(directory, DataDirectory);
        if (manifest.Encryption is null)
        {
            return dataDir;
        }

        if (recipientPrivateKeyPem is null)
        {
            throw new PackException(PackRefusal.Encryption, $"{directory}: the pack is encrypted to key {manifest.Encryption.RecipientKeyId} and no private key was supplied.");
        }

        byte[] dek;
        try
        {
            dek = BackupCrypto.UnwrapKeyWithPrivateKey(manifest.Encryption.WrappedKey, recipientPrivateKeyPem);
        }
        catch (CryptographicException ex)
        {
            throw new PackException(PackRefusal.Encryption, $"{directory}: the pack's key does not unwrap with the supplied private key (recipient {manifest.Encryption.RecipientKeyId}).", ex);
        }

        try
        {
            Directory.CreateDirectory(staging);
            foreach (var table in manifest.Tables)
            {
                ct.ThrowIfCancellationRequested();
                var plain = Path.Combine(staging, table.Table + ".sql");
                await BackupCrypto.DecryptFileAsync(Path.Combine(dataDir, table.File), plain, dek, ct);
                var hash = await HashAsync(plain, ct);
                if (!string.Equals(hash, table.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PackException(PackRefusal.Hash, $"{directory}: {table.Table} decrypted but does not hash to what the manifest recorded.");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        return staging;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}
