using System.Security.Cryptography;
using System.Text;
using BackupRestore.Crypto;
using FluentAssertions;

namespace Installer.UnitTests;

/// <summary>
/// 15.6 at the primitive level: round trips, and every way a package can be wrong is a refusal
/// that names the chunk, never garbage.
/// </summary>
public sealed class BackupCryptoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-crypto-tests", Guid.NewGuid().ToString("N"));

    public BackupCryptoTests() => Directory.CreateDirectory(_root);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(BackupCrypto.ChunkBytes - 1)]
    [InlineData(BackupCrypto.ChunkBytes)]
    [InlineData(BackupCrypto.ChunkBytes + 1)]
    [InlineData(2 * BackupCrypto.ChunkBytes + 12345)]
    public async Task A_file_of_any_size_round_trips_byte_for_byte(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var src = Write("plain.bin", plain);
        var key = BackupCrypto.NewKey();

        await BackupCrypto.EncryptFileAsync(src, src + ".enc", key);
        await BackupCrypto.DecryptFileAsync(src + ".enc", src + ".out", key);

        File.ReadAllBytes(src + ".out").Should().Equal(plain);
        new FileInfo(src + ".enc").Length.Should().BeGreaterThan(size, "magic, version, and a nonce+tag per chunk");
    }

    [Fact]
    public async Task Encrypting_the_same_file_twice_never_reuses_a_nonce_or_produces_the_same_bytes()
    {
        var src = Write("same.bin", Encoding.UTF8.GetBytes("the same plaintext, twice"));
        var key = BackupCrypto.NewKey();

        await BackupCrypto.EncryptFileAsync(src, src + ".1", key);
        await BackupCrypto.EncryptFileAsync(src, src + ".2", key);

        var a = File.ReadAllBytes(src + ".1");
        var b = File.ReadAllBytes(src + ".2");
        a.Should().NotEqual(b);
        a.AsSpan(5 + 4, BackupCrypto.NonceBytes).ToArray().Should().NotEqual(b.AsSpan(5 + 4, BackupCrypto.NonceBytes).ToArray(), "the nonce is fresh per chunk");
    }

    [Fact]
    public async Task A_wrong_key_is_refused_not_garbage()
    {
        var src = Write("k.bin", RandomNumberGenerator.GetBytes(1000));
        await BackupCrypto.EncryptFileAsync(src, src + ".enc", BackupCrypto.NewKey());

        var act = () => BackupCrypto.DecryptFileAsync(src + ".enc", src + ".out", BackupCrypto.NewKey());

        await act.Should().ThrowAsync<CryptographicException>().WithMessage("*chunk 0 failed authentication*");
    }

    [Fact]
    public async Task A_flipped_byte_is_refused_naming_the_chunk()
    {
        var src = Write("t.bin", RandomNumberGenerator.GetBytes(BackupCrypto.ChunkBytes + 100));
        var key = BackupCrypto.NewKey();
        await BackupCrypto.EncryptFileAsync(src, src + ".enc", key);
        var bytes = File.ReadAllBytes(src + ".enc");
        bytes[^5] ^= 0x01;                       // inside the second chunk's ciphertext/tag
        File.WriteAllBytes(src + ".enc", bytes);

        var act = () => BackupCrypto.DecryptFileAsync(src + ".enc", src + ".out", key);

        await act.Should().ThrowAsync<CryptographicException>().WithMessage("*chunk 1 failed authentication*");
    }

    [Fact]
    public async Task A_truncated_file_is_refused_as_truncated()
    {
        var src = Write("tr.bin", RandomNumberGenerator.GetBytes(500));
        var key = BackupCrypto.NewKey();
        await BackupCrypto.EncryptFileAsync(src, src + ".enc", key);
        var bytes = File.ReadAllBytes(src + ".enc");
        File.WriteAllBytes(src + ".enc", bytes[..(bytes.Length - 40)]);

        var act = () => BackupCrypto.DecryptFileAsync(src + ".enc", src + ".out", key);

        await act.Should().ThrowAsync<Exception>().Where(e => e is CryptographicException || e is EndOfStreamException);
    }

    [Fact]
    public async Task A_file_that_is_not_ours_is_refused_by_its_header()
    {
        var src = Write("notours.enc", Encoding.UTF8.GetBytes("this is a plain text file"));

        var act = () => BackupCrypto.DecryptFileAsync(src, src + ".out", BackupCrypto.NewKey());

        await act.Should().ThrowAsync<CryptographicException>().WithMessage("*not an ePACS encrypted backup file*");
    }

    [Fact]
    public void Key_wrap_round_trips_and_a_wrong_kek_is_refused()
    {
        var dek = BackupCrypto.NewKey();
        var kek = BackupCrypto.NewKey();

        var wrapped = BackupCrypto.WrapKey(dek, kek);
        BackupCrypto.UnwrapKey(wrapped, kek).Should().Equal(dek);

        var act = () => BackupCrypto.UnwrapKey(wrapped, BackupCrypto.NewKey());
        act.Should().Throw<CryptographicException>().WithMessage("*another node*");
    }

    [Fact]
    public void Recovery_wrap_round_trips_through_an_RSA_key_pair_and_names_the_key()
    {
        using var rsa = RSA.Create(2048);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();
        var dek = BackupCrypto.NewKey();

        var wrapped = BackupCrypto.WrapKeyToPublicKey(dek, publicPem);
        BackupCrypto.UnwrapKeyWithPrivateKey(wrapped, privatePem).Should().Equal(dek);
        BackupCrypto.PublicKeyId(publicPem).Should().HaveLength(16);

        using var other = RSA.Create(2048);
        var act = () => BackupCrypto.UnwrapKeyWithPrivateKey(wrapped, other.ExportPkcs8PrivateKeyPem());
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Manifest_mac_detects_a_single_changed_byte()
    {
        var kek = BackupCrypto.NewKey();
        var manifest = Encoding.UTF8.GetBytes("{\"backupId\":\"BAK-1\"}");
        var mac = BackupCrypto.ManifestMac(manifest, kek);

        BackupCrypto.ManifestMacValid(manifest, kek, mac).Should().BeTrue();
        BackupCrypto.ManifestMacValid(manifest, kek, mac + "\n").Should().BeTrue("a trailing newline in the .mac file is not a change");
        var altered = Encoding.UTF8.GetBytes("{\"backupId\":\"BAK-2\"}");
        BackupCrypto.ManifestMacValid(altered, kek, mac).Should().BeFalse();
        BackupCrypto.ManifestMacValid(manifest, BackupCrypto.NewKey(), mac).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
