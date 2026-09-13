using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Installer.Actions.Database;
using Installer.Core.Packs;
using Installer.Core.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Packs;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// ADR-0011 against a REAL MySQL, real mysqldump and real mysql: a society's rows are cut into a
/// signed ledger pack (the other society's never travel), deleted, and put back by applying the
/// same rows as a policy pack, counted. Needs <c>EPACS_TEST_MYSQL</c> (a root connection string)
/// and <c>EPACS_TEST_MYSQL_BIN</c> (a directory holding mysql and mysqldump); skips otherwise,
/// naming both.
/// </summary>
public sealed class PackLiveTests : IDisposable
{
    private static string? Conn => Environment.GetEnvironmentVariable("EPACS_TEST_MYSQL");
    private static string? Bin => Environment.GetEnvironmentVariable("EPACS_TEST_MYSQL_BIN");
    private static bool Available => !string.IsNullOrWhiteSpace(Conn) && !string.IsNullOrWhiteSpace(Bin)
                                     && File.Exists(Path.Combine(Bin!, "mysql")) && File.Exists(Path.Combine(Bin!, "mysqldump"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-pack-live", Guid.NewGuid().ToString("N"));
    private readonly string _table = "pk_live_" + Guid.NewGuid().ToString("N")[..8];
    private X509Certificate2? _cert;

    private InstallerOptions Installer(string thumbprint) => new()
    {
        DataRoot = Path.Combine(_root, "data"), BinaryRoot = Path.Combine(_root, "opt"), ExpectedSigningThumbprint = thumbprint
    };

    [SkippableFact]
    public async Task A_societys_rows_go_out_as_a_signed_pack_and_come_back_counted()
    {
        Skip.IfNot(Available, "EPACS_TEST_MYSQL and EPACS_TEST_MYSQL_BIN (a directory with mysql and mysqldump) are not both set");

        // ── Arrange: the node's shape ──────────────────────────────────────
        var b = new MySqlConnectionStringBuilder(Conn!);
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "installer"));
        var binDir = Path.Combine(_root, "opt", "current", "mysql", "bin");
        Directory.CreateDirectory(binDir);
        File.CreateSymbolicLink(Path.Combine(binDir, "mysql"), Path.Combine(Bin!, "mysql"));
        File.CreateSymbolicLink(Path.Combine(binDir, "mysqldump"), Path.Combine(Bin!, "mysqldump"));

        using var rsa = RSA.Create(2048);
        _cert = new CertificateRequest("CN=live pack test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = Path.Combine(_root, "site.pfx");
        File.WriteAllBytes(pfx, _cert.Export(X509ContentType.Pkcs12, "pw"));
        Environment.SetEnvironmentVariable("EPACS_LIVE_PFX", "pw");

        var installer = Options.Create(Installer(_cert.Thumbprint));
        var services = Options.Create(new ServicesOptions { MySql = new MySqlServiceOptions { Port = (int)b.Port, DatabaseName = b.Database! } });
        var secrets = new SecretStore(installer, NullLogger<SecretStore>.Instance);
        await secrets.StoreAsync("mysql.root.password", b.Password!);
        File.WriteAllText(Path.Combine(_root, "data", "config", "pacs-table-classification.json"),
            $$"""{ "society": { "{{_table}}": "pacsid" }, "master": [], "excluded": [] }""");

        await using (var conn = new MySqlConnection(Conn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE `{_table}` (id INT NOT NULL, pacsid VARCHAR(20) NOT NULL, amount DECIMAL(13,2) NOT NULL, note VARCHAR(50) NULL, PRIMARY KEY (id));
                INSERT INTO `{_table}` VALUES (1,'GJ-0001',100.50,'ours'),(2,'GJ-0001',200.00,'it''s ours'),(3,'GJ-0002',999.99,'theirs');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var packs = Options.Create(new PacksOptions { SigningPfxPath = pfx, SigningPfxPasswordEnv = "EPACS_LIVE_PFX", InboundSignerThumbprint = _cert.Thumbprint });
        var mysql = new MySqlAccess(installer, services, secrets, new ProcessRunner());
        var fingerprinter = new MySqlSchemaFingerprinter(NullLogger<MySqlSchemaFingerprinter>.Instance);
        var site = new SiteConfigPack { Signature = "s", PacsId = "GJ-0001", StateCode = "GJ", DataRoot = installer.Value.DataRoot };

        // ── Act 1: cut the ledger pack ─────────────────────────────────────
        var exporter = new LedgerPackExporter(mysql, fingerprinter, packs, installer, services, NullLogger<LedgerPackExporter>.Instance);
        var (dir, manifest) = await exporter.ExportAsync(site);

        manifest.Tables.Should().ContainSingle(t => t.Table == _table && t.Rows == 2, "the other society's row is not ours");
        manifest.WatermarkFrom.Should().NotBeNullOrEmpty();
        var sql = File.ReadAllText(Path.Combine(dir, "data", _table + ".sql"));
        sql.Should().Contain("REPLACE INTO").And.Contain("ours").And.NotContain("theirs");
        File.Exists(Path.Combine(dir, PackEnvelope.SignatureFile)).Should().BeTrue();

        // ── Act 2: lose the rows, then apply the pack's rows back as a policy pack ──
        await using (var conn = new MySqlConnection(Conn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM `{_table}` WHERE pacsid = 'GJ-0001'";
            (await cmd.ExecuteNonQueryAsync()).Should().Be(2);
        }

        var inbound = Path.Combine(_root, "data", "packs", "inbound", "GJ-0001-P-000001");
        Directory.CreateDirectory(Path.Combine(inbound, "data"));
        File.Copy(Path.Combine(dir, "data", _table + ".sql"), Path.Combine(inbound, "data", _table + ".sql"));
        var fp = await fingerprinter.CaptureAsync(await mysql.ConnectionStringAsync(CancellationToken.None), b.Database!);
        await PackEnvelope.WriteAsync(inbound, new PackManifest
        {
            PacsId = "GJ-0001", State = "GJ", PackType = PackType.Policy, PackSeq = 1, PrevPackHash = PackManifest.Genesis,
            SchemaFingerprint = fp.FingerprintHash, ProducedAt = DateTimeOffset.UtcNow, ProducerVersion = "live-test", Tables = []
        }, new Dictionary<string, long> { [_table] = 2 }, new CmsCodeSigner(() => _cert), null);

        var applier = new PolicyPackApplier(mysql, fingerprinter, new CmsCodeSigner(() => null), packs, installer, services, NullLogger<PolicyPackApplier>.Instance);
        var sweep = await applier.ApplyInboundAsync(site);

        sweep.Applied.Should().Equal("GJ-0001-P-000001");
        sweep.Rejected.Should().BeEmpty();
        (await mysql.CountAsync(_table, "pacsid", "GJ-0001", CancellationToken.None)).Should().Be(2, "the rows came back");
        (await mysql.CountAsync(_table, "pacsid", "GJ-0002", CancellationToken.None)).Should().Be(1, "the other society was never touched");

        // ── Act 3: a second export chains to the first ─────────────────────
        var (_, second) = await exporter.ExportAsync(site);
        second.PackSeq.Should().Be(2);
        second.PrevPackHash.Should().Be(await PackEnvelope.ManifestHashAsync(dir));
    }

    public void Dispose()
    {
        _cert?.Dispose();
        if (Available)
        {
            try
            {
                using var conn = new MySqlConnection(Conn);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP TABLE IF EXISTS `{_table}`";
                cmd.ExecuteNonQuery();
            }
            catch (MySqlException)
            {
                // best effort
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
