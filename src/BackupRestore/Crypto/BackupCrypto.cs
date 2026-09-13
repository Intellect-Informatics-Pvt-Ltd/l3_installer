using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BackupRestore.Crypto;

/// <summary>
/// The backup package's cryptography, in one place so it can be read in one sitting.
///
/// THE MODEL. Every backup gets its own random 256-bit data key (DEK). Each file in the package
/// is encrypted with AES-256-GCM under that DEK, in chunks, each chunk with its own nonce and
/// tag, so a corrupted byte fails one chunk's authentication and the failure names the file
/// rather than producing garbage. The DEK is then WRAPPED — encrypted — and only the wrapped
/// forms go into the manifest:
///
///   * under the node's own key-encryption key (KEK), which lives in the secret store, so the
///     same node restores its own backups without ceremony;
///   * under the state's RECOVERY public key (RSA-OAEP), when one is configured, so a
///     re-imaged or replacement node — which has no secret store — can be restored by whoever
///     holds the state's private key. A backup that can only be read by the machine that wrote
///     it is not a backup of that machine.
///
/// The manifest itself carries an HMAC-SHA256 under the KEK. That is a MAC, not a signature: it
/// proves the manifest was written by a holder of the node's key and not altered since, which
/// is what "manifest signed" means on a node that holds no signing certificate. It is named as
/// a MAC in the manifest so nobody reads more into it than that.
///
/// File format (per encrypted file): magic "EPBK", version 1, then chunks of
/// [u32 plaintext length][12-byte nonce][ciphertext][16-byte tag]. Nonces are 96-bit random per
/// chunk; with a fresh DEK per backup the birthday bound is not approached.
/// </summary>
public static class BackupCrypto
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int ChunkBytes = 4 * 1024 * 1024;
    public const string Suffix = ".enc";
    private static readonly byte[] Magic = "EPBK"u8.ToArray();
    private const byte Version = 1;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    // ── Files ────────────────────────────────────────────────────────────────

    public static async Task EncryptFileAsync(string plaintextPath, string encryptedPath, byte[] dek, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dek);
        await using var input = File.OpenRead(plaintextPath);
        await using var output = File.Create(encryptedPath);
        await output.WriteAsync(Magic, ct);
        output.WriteByte(Version);

        using var aes = new AesGcm(dek, TagBytes);
        var buffer = new byte[ChunkBytes];
        var header = new byte[4 + NonceBytes];
        var tag = new byte[TagBytes];
        int read;
        while ((read = await input.ReadAtLeastAsync(buffer, ChunkBytes, throwOnEndOfStream: false, ct)) > 0)
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var cipher = new byte[read];
            aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher, tag);
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)read);
            nonce.CopyTo(header, 4);
            await output.WriteAsync(header, ct);
            await output.WriteAsync(cipher, ct);
            await output.WriteAsync(tag, ct);
        }

        await output.FlushAsync(ct);
    }

    /// <summary>Decrypts, or throws <see cref="CryptographicException"/> naming the chunk that failed authentication.</summary>
    public static async Task DecryptFileAsync(string encryptedPath, string plaintextPath, byte[] dek, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dek);
        await using var input = File.OpenRead(encryptedPath);
        var magic = new byte[Magic.Length + 1];
        await input.ReadExactlyAsync(magic, ct);
        if (!magic.AsSpan(0, Magic.Length).SequenceEqual(Magic) || magic[^1] != Version)
        {
            throw new CryptographicException($"{Path.GetFileName(encryptedPath)} is not an ePACS encrypted backup file (bad header).");
        }

        await using var output = File.Create(plaintextPath);
        using var aes = new AesGcm(dek, TagBytes);
        var header = new byte[4 + NonceBytes];
        var tag = new byte[TagBytes];
        var chunk = 0;
        while (true)
        {
            var got = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (got == 0)
            {
                break;
            }

            if (got < header.Length)
            {
                throw new CryptographicException($"{Path.GetFileName(encryptedPath)} is truncated at chunk {chunk}.");
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length is < 0 or > ChunkBytes)
            {
                throw new CryptographicException($"{Path.GetFileName(encryptedPath)} chunk {chunk} declares an impossible length.");
            }

            var nonce = header.AsSpan(4, NonceBytes).ToArray();
            var cipher = new byte[length];
            await input.ReadExactlyAsync(cipher, ct);
            await input.ReadExactlyAsync(tag, ct);
            var plain = new byte[length];
            try
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new CryptographicException($"{Path.GetFileName(encryptedPath)} chunk {chunk} failed authentication: the file was altered or the key is wrong.", ex);
            }

            await output.WriteAsync(plain, ct);
            chunk++;
        }

        await output.FlushAsync(ct);
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    /// <summary>DEK under KEK, AES-256-GCM: nonce ‖ ciphertext ‖ tag, base64.</summary>
    public static string WrapKey(byte[] dek, byte[] kek)
    {
        ArgumentNullException.ThrowIfNull(dek);
        ArgumentNullException.ThrowIfNull(kek);
        using var aes = new AesGcm(kek, TagBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[dek.Length];
        var tag = new byte[TagBytes];
        aes.Encrypt(nonce, dek, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public static byte[] UnwrapKey(string wrapped, byte[] kek)
    {
        ArgumentNullException.ThrowIfNull(kek);
        var bytes = Convert.FromBase64String(wrapped);
        if (bytes.Length != NonceBytes + KeyBytes + TagBytes)
        {
            throw new CryptographicException("The wrapped data key has the wrong length.");
        }

        using var aes = new AesGcm(kek, TagBytes);
        var dek = new byte[KeyBytes];
        try
        {
            aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes, KeyBytes), bytes.AsSpan(NonceBytes + KeyBytes, TagBytes), dek);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CryptographicException("The data key does not unwrap under this node's key: the backup was taken by another node, or the key store has changed.", ex);
        }

        return dek;
    }

    /// <summary>DEK under the state's recovery public key, RSA-OAEP-SHA256, base64.</summary>
    public static string WrapKeyToPublicKey(byte[] dek, string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToBase64String(rsa.Encrypt(dek, RSAEncryptionPadding.OaepSHA256));
    }

    public static byte[] UnwrapKeyWithPrivateKey(string wrapped, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return rsa.Decrypt(Convert.FromBase64String(wrapped), RSAEncryptionPadding.OaepSHA256);
    }

    // ── Manifest MAC ─────────────────────────────────────────────────────────

    public static string ManifestMac(byte[] manifestBytes, byte[] kek) =>
        Convert.ToHexString(HMACSHA256.HashData(kek, manifestBytes)).ToLowerInvariant();

    public static bool ManifestMacValid(byte[] manifestBytes, byte[] kek, string mac) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(ManifestMac(manifestBytes, kek)),
            Convert.FromHexString(mac.Trim()));

    /// <summary>The recovery key's identity: SHA-256 of its SubjectPublicKeyInfo, so a manifest can say which key it was wrapped to.</summary>
    public static string PublicKeyId(string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()[..16];
    }
}
