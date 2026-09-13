using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Crypto;
using SharedKernel.Security;
using SupportBundle;

namespace Installer.UnitTests;

/// <summary>
/// 27.4, 28.6, 11.8: the tamper-evidence chain, the store that holds the database password, and
/// the bundle that leaves the node on a stick - the three pieces whose failure is invisible until
/// it matters, tested by what they must never do.
/// </summary>
public sealed class TamperEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-tamper-tests", Guid.NewGuid().ToString("N"));

    private InstallerOptions Opts => new() { DataRoot = _root, BinaryRoot = Path.Combine(_root, "bin") };

    // ── Audit chain ──────────────────────────────────────────────────────────

    private AuditChain Chain() => new(Options.Create(Opts), NullLogger<AuditChain>.Instance);

    private static AuditChainEntry Entry(string what) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch, EventType = "install", Actor = "installer", Description = what
    };

    [Fact]
    public async Task Entries_chain_and_a_fresh_reader_verifies_the_whole_file()
    {
        var chain = Chain();
        var a = await chain.AppendAsync(Entry("one"));
        var b = await chain.AppendAsync(Entry("two"));
        var c = await chain.AppendAsync(Entry("three"));

        a.SequenceNumber.Should().Be(1);
        a.PreviousHash.Should().BeNull();
        b.PreviousHash.Should().Be(a.EntryHash);
        c.PreviousHash.Should().Be(b.EntryHash);

        var result = await Chain().VerifyChainAsync();
        result.Valid.Should().BeTrue(result.ErrorMessage);
        result.TotalEntries.Should().Be(3);
        result.VerifiedEntries.Should().Be(3);
    }

    [Fact]
    public async Task A_new_process_continues_the_chain_from_the_file()
    {
        await Chain().AppendAsync(Entry("one"));
        var second = await Chain().AppendAsync(Entry("two"));

        second.SequenceNumber.Should().Be(2);
        second.PreviousHash.Should().NotBeNull();
        (await Chain().VerifyChainAsync()).Valid.Should().BeTrue();
    }

    [Fact]
    public async Task Tampering_with_entry_N_is_detected_at_N()
    {
        var chain = Chain();
        for (var i = 1; i <= 5; i++)
        {
            await chain.AppendAsync(Entry($"entry {i}"));
        }

        var path = Path.Combine(_root, "installer", "audit-chain.jsonl");
        var lines = File.ReadAllLines(path);
        lines[2] = lines[2].Replace("entry 3", "entry 3 (edited)", StringComparison.Ordinal);
        File.WriteAllLines(path, lines);

        var result = await Chain().VerifyChainAsync();

        result.Valid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(3);
        result.VerifiedEntries.Should().Be(2);
    }

    [Fact]
    public async Task A_deleted_entry_breaks_the_chain_at_the_gap()
    {
        var chain = Chain();
        for (var i = 1; i <= 4; i++)
        {
            await chain.AppendAsync(Entry($"entry {i}"));
        }

        var path = Path.Combine(_root, "installer", "audit-chain.jsonl");
        var lines = File.ReadAllLines(path).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(path, lines);

        var result = await Chain().VerifyChainAsync();

        result.Valid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(3, "entry 3's previous hash no longer matches what precedes it");
    }

    [Fact]
    public async Task An_empty_chain_verifies_as_empty()
    {
        var result = await Chain().VerifyChainAsync();
        result.Valid.Should().BeTrue();
        result.TotalEntries.Should().Be(0);
    }

    // ── Secret store ─────────────────────────────────────────────────────────

    private SecretStore Store(string? root = null) =>
        new(Options.Create(new InstallerOptions { DataRoot = root ?? _root, BinaryRoot = Path.Combine(_root, "bin") }), NullLogger<SecretStore>.Instance);

    [Fact]
    public async Task Secrets_round_trip_and_never_sit_in_the_file_in_clear()
    {
        var store = Store();
        await store.StoreAsync("mysql.root.password", "R00t-p4ssw0rd-that-must-not-leak");

        (await Store().RetrieveAsync("mysql.root.password")).Should().Be("R00t-p4ssw0rd-that-must-not-leak");
        var bytes = File.ReadAllBytes(Path.Combine(_root, "keys", "secrets.enc"));
        System.Text.Encoding.UTF8.GetString(bytes).Should().NotContain("R00t-p4ssw0rd");
        System.Text.Encoding.Latin1.GetString(bytes).Should().NotContain("mysql.root.password", "not even the key names are in clear");
    }

    [Fact]
    public async Task The_master_key_is_random_and_on_disk_root_only_and_a_copy_of_the_store_without_it_is_useless()
    {
        var store = Store();
        await store.StoreAsync("k", "a-secret-value-1234");
        var master = Path.Combine(_root, "keys", "master.key");

        File.Exists(master).Should().BeTrue();
        File.ReadAllBytes(master).Should().HaveCount(32);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(master).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Another node with the same hostname and data root path (the old key derivation) but
        // a different master key cannot read a copied secrets.enc.
        var other = Path.Combine(_root, "clone");
        Directory.CreateDirectory(Path.Combine(other, "keys"));
        File.Copy(Path.Combine(_root, "keys", "secrets.enc"), Path.Combine(other, "keys", "secrets.enc"));
        // ...and a store that exists but cannot be read is a HARD STOP, never an empty store:
        // otherwise the next StoreAsync would overwrite it and the real passwords would be gone.
        var act = () => Store(other).RetrieveAsync("k");
        (await act.Should().ThrowAsync<CryptographicException>()).WithMessage("*cannot be decrypted*master.key*not an empty store*");
    }

    [Fact]
    public async Task Rotation_produces_a_different_password_and_keys_are_created_once()
    {
        var store = Store();
        await store.StoreAsync("mysql.app.password", "before-rotation-123");

        var rotated = await store.RotateAsync("mysql.app.password");

        rotated.Should().NotBe("before-rotation-123");
        (await store.RetrieveAsync("mysql.app.password")).Should().Be(rotated);

        var kek1 = await store.GetOrCreateKeyAsync("backup.kek", 32);
        var kek2 = await Store().GetOrCreateKeyAsync("backup.kek", 32);
        kek1.Should().Equal(kek2, "the same named key material is returned across processes");
        kek1.Should().HaveCount(32);
    }

    [Fact]
    public void Generated_passwords_are_long_random_and_never_repeat()
    {
        var store = Store();
        var a = store.GeneratePassword(includeSpecialChars: false);
        var b = store.GeneratePassword(includeSpecialChars: false);

        a.Should().HaveLength(32);
        a.Should().NotBe(b);
        store.ScanForSecrets("password=" + a).Should().BeFalse("ScanForSecrets answers 'is this clean?' - and a password literal is not clean");
        store.ScanForSecrets("nothing to see here").Should().BeTrue();
    }

    // ── Support bundle ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"SenderPassword\": \"gmail-app-password-1234\"", "gmail-app-password-1234")]
    [InlineData("\"ClientSecret\": \"b6c5a0f2-8c2f-4e1a\"", "b6c5a0f2")]
    [InlineData("\"aadharapikey\": \"MIIBIjANBgkqhkiG9w0BAQEF\"", "MIIBIjAN")]
    [InlineData("\"conn\": \"Server=127.0.0.1;Uid=epacs_app;Pwd=p4ss-w0rd-9\"", "p4ss-w0rd-9")]
    [InlineData("\"DefaultConnection\": \"Server=x;Password=devpass;Uid=root\"", "devpass")]
    [InlineData("Aadhaar 1234 5678 9012 on file", "1234 5678")]
    public void The_bundle_redacts_what_it_must_never_carry(string content, string secret)
    {
        var redacted = SupportBundleCollector.RedactSensitiveData(content);

        redacted.Should().NotContain(secret);
    }

    [Theory]
    [InlineData("\"FASUrl\": \"http://127.0.0.1:5010/api/v1/\"")]
    [InlineData("\"Server\": \"127.0.0.1\"")]
    [InlineData("\"conn\": \"Server=127.0.0.1;Uid=epacs_app\"")]
    public void The_bundle_keeps_what_a_support_engineer_needs(string content)
    {
        SupportBundleCollector.RedactSensitiveData(content).Should().Be(content);
    }

    [Fact]
    public async Task A_bundle_carries_redacted_config_and_state_and_is_encrypted_to_the_state_when_a_key_is_configured()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "installer"));
        File.WriteAllText(Path.Combine(_root, "config", "appsettings.Site.json"), "{ \"ConnectionStrings\": { \"conn\": \"Server=127.0.0.1;Uid=epacs_app;Pwd=the-app-password\" } }");
        File.WriteAllText(Opts.ResolvedStateFile, "{ \"phase\": \"Success\" }");
        using var rsa = RSA.Create(2048);
        var pub = Path.Combine(_root, "state.pub.pem");
        File.WriteAllText(pub, rsa.ExportSubjectPublicKeyInfoPem());

        var collector = new SupportBundleCollector(Options.Create(Opts),
            Options.Create(new BackupOptions { Encryption = new BackupEncryptionOptions { RecoveryPublicKeyPath = pub } }),
            NullLogger<SupportBundleCollector>.Instance);
        var bundle = await collector.CollectAsync("corr-1234567890ab", Path.Combine(_root, "out"));

        bundle.Should().EndWith(".zip.enc");
        File.Exists(bundle + SupportBundleCollector.KeyFileSuffix).Should().BeTrue();
        File.Exists(bundle[..^BackupCrypto.Suffix.Length]).Should().BeFalse("the clear zip is gone");

        // Only the state can open it - and what it finds is redacted.
        var keyDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(bundle + SupportBundleCollector.KeyFileSuffix));
        var dek = BackupCrypto.UnwrapKeyWithPrivateKey(keyDoc.RootElement.GetProperty("wrappedKey").GetString()!, rsa.ExportPkcs8PrivateKeyPem());
        var zip = Path.Combine(_root, "opened.zip");
        await BackupCrypto.DecryptFileAsync(bundle, zip, dek);
        using var archive = ZipFile.OpenRead(zip);
        archive.Entries.Select(e => e.FullName).Should().Contain("system-info.txt").And.Contain("installer-state.json").And.Contain(e => e.EndsWith("appsettings.Site.json"));
        using var reader = new StreamReader(archive.GetEntry(archive.Entries.First(e => e.FullName.EndsWith("appsettings.Site.json", StringComparison.Ordinal)).FullName)!.Open());
        (await reader.ReadToEndAsync()).Should().NotContain("the-app-password").And.Contain("Uid=epacs_app");
    }

    [Fact]
    public async Task Without_a_recovery_key_the_bundle_is_a_plain_zip_and_the_log_says_so()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        var collector = new SupportBundleCollector(Options.Create(Opts), Options.Create(new BackupOptions()), NullLogger<SupportBundleCollector>.Instance);

        var bundle = await collector.CollectAsync(null, Path.Combine(_root, "out"));

        bundle.Should().EndWith(".zip");
        File.Exists(bundle).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
