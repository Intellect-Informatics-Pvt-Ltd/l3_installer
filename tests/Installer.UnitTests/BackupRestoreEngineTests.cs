using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupRestore.Backup;
using BackupRestore.Crypto;
using BackupRestore.Models;
using BackupRestore.Restore;
using FluentAssertions;
using Installer.Actions.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// 15.6 / 15.7 / 15.9 / 15.3 / 16.8, end to end: a package is taken, encrypted, wrapped, MAC'd,
/// verified by reading it back, and restored - with a REAL secret store (real master key on
/// disk) and a fake mysqldump/mysql behind <see cref="IProcessRunner"/>. Every refusal a
/// package can earn is exercised: altered ciphertext, altered manifest, missing MAC, a key
/// from another node, a package with no recovery wrap on a node with no key.
/// </summary>
public sealed class BackupRestoreEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IProcessRunner> _runner = new();
    private readonly List<string> _restoredSql = [];
    private int _tableCount = 42;

    private const string Dump = "-- MySQL dump 10.13  Distrib 8.4.0\n--\nCREATE TABLE t (id INT);\nINSERT INTO t VALUES (1);\n-- Dump completed on 2026-09-13\n";

    public BackupRestoreEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "mysql", "data"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "attachments", "members"));
        Directory.CreateDirectory(Path.Combine(_root, "opt", "current", "mysql", "bin"));
        File.WriteAllText(Path.Combine(_root, "data", "mysql", "data", "ibdata1"), new string('x', 2048));
        File.WriteAllText(Path.Combine(_root, "data", "config", "appsettings.Site.json"), "{ \"Site\": { \"PacsId\": \"GJ-0001\" } }");
        File.WriteAllBytes(Path.Combine(_root, "data", "attachments", "members", "photo.jpg"), RandomNumberGenerator.GetBytes(5000));
        File.WriteAllText(Path.Combine(_root, "opt", "current", "mysql", "bin", "mysqldump"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(_root, "opt", "current", "mysql", "bin", "mysql"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(_root, "opt", "current", "mysql", "bin", "mysqldump.exe"), "");
        File.WriteAllText(Path.Combine(_root, "opt", "current", "mysql", "bin", "mysql.exe"), "");

        // A fake mysqldump that writes the dump to --result-file, and a fake mysql that records
        // what it was fed and answers the census.
        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string exe, string args, string? _, string? stdin, IReadOnlyCollection<string>? _, IReadOnlyDictionary<string, string>? _, CancellationToken _) =>
            {
                if (exe.EndsWith("mysqldump", StringComparison.Ordinal) || exe.EndsWith("mysqldump.exe", StringComparison.Ordinal))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(args, "--result-file=\"([^\"]+)\"");
                    File.WriteAllText(m.Groups[1].Value, Dump);
                    return Task.FromResult(new ProcessResult { ExitCode = 0, StandardOutput = "", StandardError = "" });
                }

                if (stdin is not null && stdin.Contains("information_schema", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessResult { ExitCode = 0, StandardOutput = _tableCount + "\n", StandardError = "" });
                }

                _restoredSql.Add(stdin ?? "");
                return Task.FromResult(new ProcessResult { ExitCode = 0, StandardOutput = "", StandardError = "" });
            });
    }

    private InstallerOptions Installer => new() { DataRoot = Path.Combine(_root, "data"), BinaryRoot = Path.Combine(_root, "opt") };

    private BackupOptions Backup(string? recoveryPublic = null, string? recoveryPrivate = null) => new()
    {
        Targets = [Path.Combine(_root, "backups")],
        Encryption = new BackupEncryptionOptions { RecoveryPublicKeyPath = recoveryPublic, RecoveryPrivateKeyPath = recoveryPrivate },
        TargetFreeSpaceMultiplier = 0
    };

    private SecretStore Secrets(string? dataRoot = null) =>
        new(Options.Create(new InstallerOptions { DataRoot = dataRoot ?? Installer.DataRoot, BinaryRoot = Installer.BinaryRoot }), NullLogger<SecretStore>.Instance);

    private BackupEngine Engine(ISecretStore? secrets = null, BackupOptions? options = null) =>
        new(Options.Create(options ?? Backup()), Options.Create(Installer), Options.Create(new ServicesOptions()),
            secrets ?? Secrets(), _runner.Object, NullLogger<BackupEngine>.Instance);

    private RestoreEngine Restore(IBackupEngine engine, ISecretStore? secrets = null) =>
        new(engine, Options.Create(Installer), Options.Create(new ServicesOptions()), secrets ?? Secrets(), _runner.Object, NullLogger<RestoreEngine>.Instance);

    // ── Taking a package ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_package_is_encrypted_wrapped_and_maced_and_carries_no_plaintext()
    {
        var manifest = await Engine().CreateBackupAsync(BackupType.Manual);

        var dir = manifest.PackagePath!;
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(f => f != BackupEngine.ManifestFileName && f != BackupEngine.ManifestMacFileName)
            .Should().OnlyContain(f => f!.EndsWith(BackupCrypto.Suffix), "every payload file is encrypted");
        var dumpEnc = Path.Combine(dir, "db", "mysql-dump.sql.enc");
        File.Exists(dumpEnc).Should().BeTrue();
        Encoding.UTF8.GetString(File.ReadAllBytes(dumpEnc)).Should().NotContain("CREATE TABLE", "the dump is not readable in the package");

        manifest.KeyWrap.Should().NotBeNull();
        manifest.KeyWrap!.Recovery.Should().BeNull("no recovery key was configured");
        manifest.KeyProtection.Should().Contain("THIS node only");
        manifest.Validation.ManifestSigned.Should().BeTrue();
        manifest.Validation.SignatureType.Should().Contain("HMAC").And.Contain("not a certificate signature");
        manifest.Files.Should().Contain(f => f.Category == "db" && f.EncryptedSha256 != null && f.Sha256 != f.EncryptedSha256);
        manifest.Files.Should().Contain(f => f.Category == "attachments" && f.RelativePath.EndsWith("attachments.zip.enc"));
        File.Exists(Path.Combine(dir, BackupEngine.ManifestMacFileName)).Should().BeTrue();
        File.Exists(Path.Combine(Installer.DataRoot, "keys", "master.key")).Should().BeTrue("the secret store created a real master key");
        Directory.GetFiles(Path.Combine(dir, "keys")).Should().NotContain(f => f.Contains("master.key"), "a backup that carries the key that decrypts it is not encrypted");
    }

    [Fact]
    public async Task Verify_reads_the_package_back_and_confirms_the_dump_is_complete()
    {
        var engine = Engine();
        var manifest = await engine.CreateBackupAsync(BackupType.Manual);

        var result = await engine.VerifyBackupAsync(manifest.PackagePath!);

        result.Valid.Should().BeTrue(string.Join("; ", result.Errors));
        result.ChecksumVerified.Should().BeTrue();
        result.ManifestSignatureValid.Should().BeTrue();
        result.DumpReadable.Should().BeTrue();
    }

    [Fact]
    public async Task With_a_recovery_key_the_data_key_is_also_wrapped_to_it()
    {
        using var rsa = RSA.Create(2048);
        var pub = Path.Combine(_root, "recovery.pub.pem");
        File.WriteAllText(pub, rsa.ExportSubjectPublicKeyInfoPem());

        var manifest = await Engine(options: Backup(recoveryPublic: pub)).CreateBackupAsync(BackupType.Manual);

        manifest.KeyWrap!.Recovery.Should().NotBeNull();
        manifest.KeyWrap.RecoveryKeyId.Should().Be(BackupCrypto.PublicKeyId(rsa.ExportSubjectPublicKeyInfoPem()));
        manifest.KeyProtection.Should().Contain("recovery-rsa-oaep");
        BackupCrypto.UnwrapKeyWithPrivateKey(manifest.KeyWrap.Recovery!, rsa.ExportPkcs8PrivateKeyPem()).Should().HaveCount(32);
    }

    [Fact]
    public async Task A_configured_recovery_key_that_is_missing_refuses_rather_than_silently_dropping_the_wrap()
    {
        var act = () => Engine(options: Backup(recoveryPublic: Path.Combine(_root, "nope.pem"))).CreateBackupAsync(BackupType.Manual);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RecoveryPublicKeyPath*does not exist*");
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_flipped_byte_in_the_dump_fails_verification_and_restore_changes_nothing()
    {
        var engine = Engine();
        var manifest = await engine.CreateBackupAsync(BackupType.Manual);
        var enc = Path.Combine(manifest.PackagePath!, "db", "mysql-dump.sql.enc");
        var bytes = File.ReadAllBytes(enc);
        bytes[^3] ^= 0x01;
        File.WriteAllBytes(enc, bytes);

        var verified = await engine.VerifyBackupAsync(manifest.PackagePath!);
        verified.Valid.Should().BeFalse();
        verified.Errors.Should().Contain(e => e.Contains("Checksum mismatch"));

        var act = () => Restore(engine).RestoreAsync(manifest.PackagePath!, createSafetyBackup: false);
        await act.Should().ThrowAsync<RestoreException>().WithMessage("*does not verify*Nothing has been changed*");
        _restoredSql.Should().BeEmpty();
    }

    [Fact]
    public async Task An_altered_manifest_fails_its_mac()
    {
        var engine = Engine();
        var manifest = await engine.CreateBackupAsync(BackupType.Manual);
        var path = Path.Combine(manifest.PackagePath!, BackupEngine.ManifestFileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Manual\"", "\"WeeklyFull\"", StringComparison.Ordinal).Replace("4", "5", StringComparison.Ordinal));

        var verified = await engine.VerifyBackupAsync(manifest.PackagePath!);

        verified.Valid.Should().BeFalse();
        verified.Errors.Should().Contain(e => e.Contains("MAC does not verify") || e.Contains("does not parse") || e.Contains("Checksum mismatch"));
    }

    [Fact]
    public async Task A_missing_mac_is_named()
    {
        var engine = Engine();
        var manifest = await engine.CreateBackupAsync(BackupType.Manual);
        File.Delete(Path.Combine(manifest.PackagePath!, BackupEngine.ManifestMacFileName));

        var verified = await engine.VerifyBackupAsync(manifest.PackagePath!);

        verified.ManifestSignatureValid.Should().BeFalse();
        verified.Errors.Should().ContainSingle(e => e.Contains("MAC is missing"));
    }

    [Fact]
    public async Task Another_node_cannot_open_a_package_wrapped_to_this_node_only()
    {
        var manifest = await Engine().CreateBackupAsync(BackupType.Manual);

        // A different data root = a different master key = a different node.
        var otherRoot = Path.Combine(_root, "other-node");
        Directory.CreateDirectory(Path.Combine(otherRoot, "keys"));
        var other = Secrets(otherRoot);
        await other.GetOrCreateKeyAsync(BackupEngine.BackupKekSecretName, 32);
        var otherEngine = new BackupEngine(Options.Create(Backup()), Options.Create(new InstallerOptions { DataRoot = otherRoot, BinaryRoot = Installer.BinaryRoot }),
            Options.Create(new ServicesOptions()), other, _runner.Object, NullLogger<BackupEngine>.Instance);

        var act = () => otherEngine.UnwrapAsync(manifest);

        (await act.Should().ThrowAsync<CryptographicException>()).WithMessage("*does not unwrap under this node's key*");
    }

    [Fact]
    public async Task A_replacement_node_opens_a_package_with_the_states_recovery_key()
    {
        using var rsa = RSA.Create(2048);
        var pub = Path.Combine(_root, "recovery.pub.pem");
        var priv = Path.Combine(_root, "recovery.key.pem");
        File.WriteAllText(pub, rsa.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(priv, rsa.ExportPkcs8PrivateKeyPem());
        var manifest = await Engine(options: Backup(recoveryPublic: pub)).CreateBackupAsync(BackupType.Manual);

        var replacementRoot = Path.Combine(_root, "replacement");
        Directory.CreateDirectory(replacementRoot);
        var replacement = new BackupEngine(Options.Create(Backup(recoveryPrivate: priv)),
            Options.Create(new InstallerOptions { DataRoot = replacementRoot, BinaryRoot = Installer.BinaryRoot }),
            Options.Create(new ServicesOptions()), Secrets(replacementRoot), _runner.Object, NullLogger<BackupEngine>.Instance);

        var dek = await replacement.UnwrapAsync(manifest);

        dek.Should().HaveCount(32);
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_decrypts_into_root_only_staging_feeds_the_dump_to_mysql_counts_and_cleans_up()
    {
        var engine = Engine();
        var manifest = await engine.CreateBackupAsync(BackupType.Manual);
        File.Delete(Path.Combine(Installer.DataRoot, "attachments", "members", "photo.jpg"));
        File.WriteAllText(Path.Combine(Installer.DataRoot, "config", "appsettings.Site.json"), "{ \"broken\": true }");

        var result = await Restore(engine).RestoreAsync(manifest.PackagePath!, createSafetyBackup: false);

        result.Success.Should().BeTrue();
        result.RequiresReconciliation.Should().BeTrue("always, after a restore");
        _restoredSql.Should().ContainSingle().Which.Should().Be(Dump);
        File.ReadAllText(Path.Combine(Installer.DataRoot, "config", "appsettings.Site.json")).Should().Contain("GJ-0001", "configuration came back");
        File.Exists(Path.Combine(Installer.DataRoot, "attachments", "members", "photo.jpg")).Should().BeTrue("attachments came back, hash-checked");
        var stagingRoot = Path.Combine(Installer.DataRoot, "temp", "restore");
        (Directory.Exists(stagingRoot) ? Directory.GetDirectories(stagingRoot) : []).Should().BeEmpty("the plaintext staging copy is gone");
    }

    [Fact]
    public async Task Restore_with_a_safety_backup_takes_one_first_and_names_it()
    {
        var engine = Engine();
        var manifest = await engine.CreateBackupAsync(BackupType.Manual);

        var result = await Restore(engine).RestoreAsync(manifest.PackagePath!, createSafetyBackup: true);

        result.SafetyBackupId.Should().NotBeNull().And.StartWith("BAK-");
        Directory.Exists(Path.Combine(_root, "backups", result.SafetyBackupId!)).Should().BeTrue();
    }

    [Fact]
    public async Task A_restore_that_leaves_the_database_empty_is_a_refusal_naming_the_safety_backup()
    {
        var engine = Engine();
        var manifest = await engine.CreateBackupAsync(BackupType.Manual);
        _tableCount = 0;

        var act = () => Restore(engine).RestoreAsync(manifest.PackagePath!, createSafetyBackup: true);

        var ex = await act.Should().ThrowAsync<RestoreException>();
        ex.Which.Message.Should().Contain("database is empty").And.Contain("safety backup BAK-");
    }

    [Fact]
    public void Manifest_json_round_trips_with_the_key_wrap_and_never_the_package_path()
    {
        var manifest = new BackupManifest
        {
            BackupId = "BAK-1", PacsId = "GJ", CreatedAtUtc = DateTimeOffset.UnixEpoch, CreatedBy = "t", BackupType = BackupType.Manual,
            StackVersion = "3.3.0", SchemaVersion = 25, Encryption = "AES-256-GCM", KeyProtection = "local",
            Includes = new BackupIncludes(), Validation = new BackupValidation { ManifestSigned = true, SignatureType = "HMAC" },
            KeyWrap = new BackupKeyWrap { Algorithm = "a", Local = "l", Recovery = "r", RecoveryKeyId = "id" },
            PackagePath = "/should/not/serialise"
        };

        var json = JsonSerializer.Serialize(manifest);
        json.Should().NotContain("should/not/serialise");
        var back = JsonSerializer.Deserialize<BackupManifest>(json)!;
        back.KeyWrap!.Recovery.Should().Be("r");
        back.Validation.SignatureType.Should().Be("HMAC");
        back.PackagePath.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
