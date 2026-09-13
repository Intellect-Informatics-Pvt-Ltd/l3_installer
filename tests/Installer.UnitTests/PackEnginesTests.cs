using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Installer.Core.Packs;
using Installer.Core.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Packs;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// The exporter and the applier (ADR-0011) driven end to end over an in-memory "database":
/// a society's rows are cut into a ledger pack, the ledger advances, a policy pack comes back
/// and is applied and COUNTED; replays are acknowledged, gaps wait, tampering is rejected with
/// the reason written beside the pack. The classification file is the estate's shape.
/// </summary>
public sealed class PackEnginesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-pack-engine-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDatabase _db = new();
    private readonly Mock<ISchemaFingerprinter> _fingerprinter = new();
    private readonly X509Certificate2 _siteCert;
    private readonly X509Certificate2 _stateCert;
    private readonly string _schema = "schema-A";
    private static readonly string[] SchemaTables = ["fa_vouchermain", "mem_member", "ln_loanaccount", "cm_state", "trm_productdefinition"];

    public PackEnginesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "keys"));
        _siteCert = SelfSigned("CN=Site GJ-0001");
        _stateCert = SelfSigned("CN=State GJ release key");
        File.WriteAllBytes(Path.Combine(_root, "site.pfx"), _siteCert.Export(X509ContentType.Pkcs12, "pw"));
        Environment.SetEnvironmentVariable("EPACS_TEST_SITE_PFX", "pw");

        File.WriteAllText(Path.Combine(_root, "data", "config", "pacs-table-classification.json"), """
            { "generated_by": "test", "baseline_table_count": 5,
              "society": { "fa_vouchermain": "PacsId", "mem_member": "PacsID", "ln_loanaccount": "PacsId" },
              "master": [ "cm_state", "trm_productdefinition" ],
              "excluded": [ "OutboxMessages" ] }
            """);

        _fingerprinter.Setup(f => f.CaptureAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SchemaFingerprint
            {
                DatabaseName = "epacs", StackVersion = "3.3.0", CapturedAt = DateTimeOffset.UnixEpoch,
                Tables = SchemaTables
                    .ToDictionary(t => t, t => new TableFingerprint
                    {
                        Name = t, Engine = "InnoDB", Collation = "utf8mb4_general_ci",
                        Columns = new Dictionary<string, ColumnFingerprint>(),
                        Indexes = new Dictionary<string, IReadOnlyList<string>>(),
                        PrimaryKey = [], ForeignKeys = []
                    }),
                Views = [],
                FingerprintHash = _schema
            });

        // The society's rows, and another society's beside them.
        _db.Rows["fa_vouchermain"] = [("GJ-0001", "v1"), ("GJ-0001", "v2"), ("GJ-0002", "other")];
        _db.Rows["mem_member"] = [("GJ-0001", "m1")];
        _db.Rows["ln_loanaccount"] = [("GJ-0002", "not-ours")];
        _db.Rows["cm_state"] = [("", "GJ")];
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        return new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static SiteConfigPack Site => new() { Signature = "s", PacsId = "GJ-0001", StateCode = "GJ", DataRoot = "/ignored" };

    private InstallerOptions Installer => new()
    {
        DataRoot = Path.Combine(_root, "data"), BinaryRoot = Path.Combine(_root, "opt"),
        ExpectedSigningThumbprint = _stateCert.Thumbprint
    };

    private PacksOptions Packs(bool signed = true) => new()
    {
        SigningPfxPath = signed ? Path.Combine(_root, "site.pfx") : null,
        SigningPfxPasswordEnv = "EPACS_TEST_SITE_PFX"
    };

    private LedgerPackExporter Exporter(PacksOptions? packs = null) =>
        new(_db, _fingerprinter.Object, Options.Create(packs ?? Packs()), Options.Create(Installer), Options.Create(new ServicesOptions()), NullLogger<LedgerPackExporter>.Instance);

    private PolicyPackApplier Applier(PacksOptions? packs = null) =>
        new(_db, _fingerprinter.Object, new CmsCodeSigner(() => null), Options.Create(packs ?? Packs()), Options.Create(Installer), Options.Create(new ServicesOptions()), NullLogger<PolicyPackApplier>.Instance);

    // ── Export ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_ledger_pack_carries_only_this_societys_rows_from_society_tables_counted_and_signed()
    {
        var (dir, manifest) = await Exporter().ExportAsync(Site);

        manifest.PackType.Should().Be(PackType.Ledger);
        manifest.PackSeq.Should().Be(1);
        manifest.PrevPackHash.Should().Be(PackManifest.Genesis);
        manifest.SchemaFingerprint.Should().Be("schema-A");
        manifest.WatermarkFrom.Should().StartWith("gtid:");
        manifest.Tables.Select(t => t.Table).Should().Equal("fa_vouchermain", "mem_member");   // ln_loanaccount: 0 rows, skipped; cm_state: a master, never up
        manifest.Tables.Single(t => t.Table == "fa_vouchermain").Rows.Should().Be(2, "the other society's row is not ours");
        File.ReadAllText(Path.Combine(dir, "data", "fa_vouchermain.sql")).Should().Contain("v1").And.Contain("v2").And.NotContain("other");
        File.Exists(Path.Combine(dir, PackEnvelope.SignatureFile)).Should().BeTrue();
        Path.GetFileName(dir).Should().Be("GJ-0001-L-000001");

        var ledger = await new PackLedger(Path.Combine(_root, "data", "sync", "pack-ledger.json")).LoadAsync();
        PackLedger.NextProduced(ledger, PackType.Ledger).Should().Match<PackLedger.Cursor>(c => c.LastSeq == 1 && c.LastPackId == "GJ-0001-L-000001");
    }

    [Fact]
    public async Task Consecutive_packs_chain_and_a_lost_pack_is_re_cut_identically_in_content()
    {
        var (first, m1) = await Exporter().ExportAsync(Site);
        var h1 = await PackEnvelope.ManifestHashAsync(first);
        var (_, m2) = await Exporter().ExportAsync(Site);

        m2.PackSeq.Should().Be(2);
        m2.PrevPackHash.Should().Be(h1);
        m2.Tables.Select(t => (t.Table, t.Rows, t.Sha256)).Should().Equal(m1.Tables.Select(t => (t.Table, t.Rows, t.Sha256)), "a snapshot of unchanged data is byte-identical in content");
    }

    [Fact]
    public async Task Without_a_site_key_the_pack_is_written_unsigned_and_says_so()
    {
        var (dir, _) = await Exporter(Packs(signed: false)).ExportAsync(Site);

        File.Exists(Path.Combine(dir, PackEnvelope.SignatureFile)).Should().BeFalse();
    }

    [Fact]
    public async Task A_configured_site_key_that_is_missing_refuses_rather_than_going_out_unsigned()
    {
        var act = () => Exporter(new PacksOptions { SigningPfxPath = Path.Combine(_root, "nope.pfx") }).ExportAsync(Site);

        await act.Should().ThrowAsync<FileNotFoundException>().WithMessage("*silently*unsigned*");
    }

    [Fact]
    public async Task With_the_states_public_key_the_pack_is_encrypted_to_it()
    {
        using var rsa = RSA.Create(2048);
        var pub = Path.Combine(_root, "state.pub.pem");
        File.WriteAllText(pub, rsa.ExportSubjectPublicKeyInfoPem());

        var (dir, manifest) = await Exporter(new PacksOptions { SigningPfxPath = Path.Combine(_root, "site.pfx"), SigningPfxPasswordEnv = "EPACS_TEST_SITE_PFX", RecipientPublicKeyPath = pub }).ExportAsync(Site);

        manifest.Encryption.Should().NotBeNull();
        Directory.GetFiles(Path.Combine(dir, "data")).Should().OnlyContain(f => f.EndsWith(".enc"));
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    private async Task<string> InboundPolicyPackAsync(long seq, string prev, string name, X509Certificate2? signer = null, string schema = "schema-A", params (string table, string sql, long rows)[] tables)
    {
        var dir = Path.Combine(_root, "data", "packs", "inbound", name);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (table, sql, rows) in tables)
        {
            File.WriteAllText(Path.Combine(dir, "data", table + ".sql"), sql);
            counts[table] = rows;
        }

        var manifest = new PackManifest
        {
            PacsId = "GJ-0001", State = "GJ", PackType = PackType.Policy, PackSeq = seq, PrevPackHash = prev,
            SchemaFingerprint = schema, ProducedAt = DateTimeOffset.UnixEpoch, ProducerVersion = "state", Tables = []
        };
        await PackEnvelope.WriteAsync(dir, manifest, counts, new CmsCodeSigner(() => signer ?? _stateCert), null);
        return dir;
    }

    [Fact]
    public async Task A_policy_pack_is_applied_counted_and_recorded()
    {
        await InboundPolicyPackAsync(1, PackManifest.Genesis, "GJ-0001-P-000001", null, "schema-A",
            ("trm_productdefinition", "REPLACE INTO trm_productdefinition VALUES ('','FD-12'),('','FD-24');\n", 2));

        var sweep = await Applier().ApplyInboundAsync(Site);

        sweep.Applied.Should().Equal("GJ-0001-P-000001");
        sweep.Rejected.Should().BeEmpty();
        _db.Applied.Should().ContainSingle().Which.Should().Contain("FD-24");
        _db.Rows["trm_productdefinition"].Should().HaveCount(2, "the fake database applied the REPLACE");
        Directory.Exists(Path.Combine(_root, "data", "packs", "applied", "GJ-0001-P-000001")).Should().BeTrue();
        var ledger = await new PackLedger(Path.Combine(_root, "data", "sync", "pack-ledger.json")).LoadAsync();
        PackLedger.NextApplied(ledger, PackType.Policy).LastSeq.Should().Be(1);
    }

    [Fact]
    public async Task Replays_are_acknowledged_gaps_wait_and_tampering_is_rejected_with_the_reason_beside_the_pack()
    {
        var first = await InboundPolicyPackAsync(1, PackManifest.Genesis, "p1", null, "schema-A", ("cm_state", "REPLACE INTO cm_state VALUES ('','GJ');\n", 1));
        await Applier().ApplyInboundAsync(Site);
        var h1 = await PackEnvelope.ManifestHashAsync(Path.Combine(_root, "data", "packs", "applied", "p1"));

        // A replay of 1, a gap (3 without 2), and a tampered 2.
        await InboundPolicyPackAsync(1, PackManifest.Genesis, "p1-again", null, "schema-A", ("cm_state", "REPLACE INTO cm_state VALUES ('','GJ');\n", 1));
        await InboundPolicyPackAsync(3, "whatever", "p3", null, "schema-A", ("cm_state", "x", 1));
        var p2 = await InboundPolicyPackAsync(2, h1, "p2", null, "schema-A", ("cm_state", "REPLACE INTO cm_state VALUES ('','GJ');\n", 1));
        File.WriteAllText(Path.Combine(p2, "data", "cm_state.sql"), "DROP TABLE cm_state;\n");
        _db.Applied.Clear();

        var sweep = await Applier().ApplyInboundAsync(Site);

        sweep.Acknowledged.Should().Equal("p1-again");
        sweep.Rejected.Should().ContainSingle(r => r.Pack == "p2" && r.Refusal == PackRefusal.Hash);
        sweep.Waiting.Should().Equal(["p3"], "2 was rejected, so 3 still waits");
        sweep.Applied.Should().BeEmpty();
        _db.Applied.Should().BeEmpty("nothing reached the database");
        File.ReadAllText(Path.Combine(_root, "data", "packs", "rejected", "p2", "REJECTED.txt")).Should().StartWith("Hash:");
        Directory.Exists(Path.Combine(_root, "data", "packs", "inbound", "p3")).Should().BeTrue("a waiting pack stays where it is");
    }

    [Fact]
    public async Task A_pack_signed_by_someone_other_than_the_pinned_state_key_is_rejected()
    {
        using var impostor = SelfSigned("CN=Impostor");
        await InboundPolicyPackAsync(1, PackManifest.Genesis, "bad", impostor, "schema-A", ("cm_state", "x", 1));

        var sweep = await Applier().ApplyInboundAsync(Site);

        sweep.Rejected.Should().ContainSingle(r => r.Refusal == PackRefusal.Signature);
        _db.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pack_cut_against_another_schema_is_rejected_naming_upgrade()
    {
        await InboundPolicyPackAsync(1, PackManifest.Genesis, "old-schema", null, "schema-Z", ("cm_state", "x", 1));

        var sweep = await Applier().ApplyInboundAsync(Site);

        sweep.Rejected.Should().ContainSingle(r => r.Refusal == PackRefusal.Schema && r.Reason.Contains("ADR-0013"));
    }

    [Fact]
    public async Task A_count_that_falls_short_after_apply_is_not_recorded_as_applied()
    {
        // The pack claims 5 rows; the fake database will hold 1 after the REPLACE.
        await InboundPolicyPackAsync(1, PackManifest.Genesis, "short", null, "schema-A", ("cm_state", "REPLACE INTO cm_state VALUES ('','GJ');\n", 5));

        var sweep = await Applier().ApplyInboundAsync(Site);

        sweep.Rejected.Should().ContainSingle(r => r.Reason.Contains("holds 1 row(s)") && r.Reason.Contains("carried 5"));
        var ledger = await new PackLedger(Path.Combine(_root, "data", "sync", "pack-ledger.json")).LoadAsync();
        PackLedger.NextApplied(ledger, PackType.Policy).LastSeq.Should().Be(0, "the ledger did not advance");
    }

    [Fact]
    public async Task Site_data_is_the_zeroth_policy_pack_and_lands_a_society_from_nothing()
    {
        _db.Rows.Clear();
        var dir = Path.Combine(_root, "epdata");
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "data", "mem_member.sql"), "REPLACE INTO mem_member VALUES ('GJ-0001','m1'),('GJ-0001','m2');\n");
        File.WriteAllText(Path.Combine(dir, "data", "cm_state.sql"), "REPLACE INTO cm_state VALUES ('','GJ');\n");
        await PackEnvelope.WriteAsync(dir, new PackManifest
        {
            PacsId = "GJ-0001", State = "GJ", PackType = PackType.SiteData, PackSeq = 1, PrevPackHash = PackManifest.Genesis,
            SchemaFingerprint = "schema-A", ProducedAt = DateTimeOffset.UnixEpoch, ProducerVersion = "carve-out", Tables = []
        }, new Dictionary<string, long> { ["mem_member"] = 2, ["cm_state"] = 1 }, new CmsCodeSigner(() => _stateCert), null);

        var result = await Applier().ApplyAsync(dir, PackType.SiteData, Site);

        result.CountsAfter["mem_member"].Should().Be(2);
        result.CountsAfter["cm_state"].Should().Be(1);
        _db.Rows["mem_member"].Should().HaveCount(2);
    }

    public void Dispose()
    {
        _siteCert.Dispose();
        _stateCert.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Just enough of MySQL: rows are (pacs, payload); a dump writes one REPLACE per row in
    /// scope; apply parses REPLACE INTO t VALUES ('pacs','payload'),... into rows.
    /// </summary>
    private sealed class FakeDatabase : IMySqlAccess
    {
        public Dictionary<string, List<(string Pacs, string Payload)>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Applied { get; } = [];

        public string DatabaseName => "epacs";
        public Task<string> ConnectionStringAsync(CancellationToken ct) => Task.FromResult("Server=fake");
        public Task<string> WatermarkAsync(CancellationToken ct) => Task.FromResult("gtid:fake:1-10");

        public Task<long> CountAsync(string table, string? column, string? value, CancellationToken ct)
        {
            var rows = Rows.GetValueOrDefault(table) ?? [];
            return Task.FromResult<long>(column is null ? rows.Count : rows.Count(r => r.Pacs == value));
        }

        public Task DumpRowsAsync(string table, string? column, string? value, string outputFile, CancellationToken ct)
        {
            var rows = (Rows.GetValueOrDefault(table) ?? []).Where(r => column is null || r.Pacs == value);
            var sql = string.Join("", rows.Select(r => $"REPLACE INTO {table} VALUES ('{r.Pacs}','{r.Payload}');\n"));
            File.WriteAllText(outputFile, sql);
            return Task.CompletedTask;
        }

        public Task ApplySqlAsync(string sqlFile, CancellationToken ct)
        {
            var sql = File.ReadAllText(sqlFile);
            Applied.Add(sql);
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(sql, @"REPLACE INTO (\w+) VALUES (.*?);").Cast<System.Text.RegularExpressions.Match>())
            {
                var table = m.Groups[1].Value;
                var list = Rows.TryGetValue(table, out var existing) ? existing : Rows[table] = [];
                foreach (var tuple in System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, @"\('([^']*)','([^']*)'\)").Cast<System.Text.RegularExpressions.Match>())
                {
                    var row = (tuple.Groups[1].Value, tuple.Groups[2].Value);
                    if (!list.Contains(row))
                    {
                        list.Add(row);
                    }
                }
            }

            return Task.CompletedTask;
        }
    }
}
