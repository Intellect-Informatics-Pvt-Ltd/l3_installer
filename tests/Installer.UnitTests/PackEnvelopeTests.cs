using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SharedKernel.Crypto;
using SharedKernel.Packs;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// ADR-0011's envelope, refusal by refusal, with a real CMS signer: signature before content,
/// scope, sequence, chain, replay, schema, hash, encryption. Each refusal is a named kind, so
/// the applier can tell a replay (acknowledge) from a gap (wait) from tampering (reject).
/// </summary>
public sealed class PackEnvelopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-pack-tests", Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _siteCert;
    private readonly X509Certificate2 _otherCert;

    public PackEnvelopeTests()
    {
        Directory.CreateDirectory(_root);
        _siteCert = SelfSigned("CN=ePACS Site GJ-0001");
        _otherCert = SelfSigned("CN=Somebody Else");
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private ICodeSigner SiteSigner => new CmsCodeSigner(() => _siteCert);
    private ICodeSigner OtherSigner => new CmsCodeSigner(() => _otherCert);
    private static ICodeSigner Verifier => new CmsCodeSigner(() => null);

    private static PackManifest Manifest(long seq = 1, string prev = PackManifest.Genesis, string pacs = "GJ-0001", string schema = "schema-A", PackType type = PackType.Ledger) => new()
    {
        PacsId = pacs, State = "GJ", PackType = type, PackSeq = seq, PrevPackHash = prev, SchemaFingerprint = schema,
        ProducedAt = DateTimeOffset.UnixEpoch, ProducerVersion = "test", Tables = []
    };

    private async Task<string> WritePackAsync(string name, PackManifest manifest, ICodeSigner? signer, string? recipientPem = null, params (string table, string sql, long rows)[] tables)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, PackEnvelope.DataDirectory));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (table, sql, rows) in tables)
        {
            File.WriteAllText(Path.Combine(dir, PackEnvelope.DataDirectory, table + ".sql"), sql);
            counts[table] = rows;
        }

        await PackEnvelope.WriteAsync(dir, manifest, counts, signer, recipientPem);
        return dir;
    }

    // The state PINS the site's signer, as the node pins the release key for a medium: a
    // self-signed site certificate has no chain to trust, and does not need one.
    private Task<PackManifest> ReadAsync(string dir, long expectedSeq = 1, string expectedPrev = PackManifest.Genesis, string pacs = "GJ-0001", string? schema = "schema-A", string? thumbprint = null, bool requireSignature = true) =>
        PackEnvelope.ReadAndVerifyAsync(dir, Verifier, thumbprint ?? _siteCert.Thumbprint, requireSignature, pacs, expectedSeq, expectedPrev, schema);

    [Fact]
    public async Task A_signed_pack_round_trips_with_measured_hashes_and_counts()
    {
        var dir = await WritePackAsync("p1", Manifest(), SiteSigner, null, ("fa_vouchermain", "REPLACE INTO fa_vouchermain VALUES (1);\n", 1), ("mem_member", "REPLACE INTO mem_member VALUES (1),(2);\n", 2));

        var manifest = await ReadAsync(dir, thumbprint: _siteCert.Thumbprint);

        manifest.Tables.Should().HaveCount(2);
        manifest.Tables.Select(t => t.Table).Should().Equal("fa_vouchermain", "mem_member");
        manifest.Tables.Single(t => t.Table == "mem_member").Rows.Should().Be(2);
        manifest.Tables.Should().OnlyContain(t => t.Sha256.Length == 64 && t.EncryptedSha256 == null);
        manifest.Encryption.Should().BeNull();
        File.Exists(Path.Combine(dir, PackEnvelope.SignatureFile)).Should().BeTrue();
    }

    [Fact]
    public async Task An_unsigned_pack_is_refused_where_signatures_are_required_and_accepted_on_a_bench()
    {
        var dir = await WritePackAsync("p2", Manifest(), signer: null, null, ("t", "x", 1));

        var act = () => ReadAsync(dir);
        (await act.Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.NotSigned);

        (await ReadAsync(dir, requireSignature: false)).PackSeq.Should().Be(1);
    }

    [Fact]
    public async Task The_wrong_signer_is_refused_before_a_byte_of_content_is_read()
    {
        var dir = await WritePackAsync("p3", Manifest(), OtherSigner, null, ("t", "x", 1));

        var act = () => ReadAsync(dir, thumbprint: _siteCert.Thumbprint);

        (await act.Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Signature);
    }

    [Fact]
    public async Task A_manifest_altered_after_signing_is_refused()
    {
        var dir = await WritePackAsync("p4", Manifest(), SiteSigner, null, ("t", "x", 1));
        var path = Path.Combine(dir, PackEnvelope.ManifestFile);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"PackSeq\": 1", "\"PackSeq\": 2", StringComparison.Ordinal));

        var act = () => ReadAsync(dir, thumbprint: _siteCert.Thumbprint);

        (await act.Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Signature);
    }

    [Fact]
    public async Task Scope_sequence_chain_replay_and_schema_each_refuse_by_name()
    {
        var dir = await WritePackAsync("p5", Manifest(seq: 3, prev: "abc"), SiteSigner, null, ("t", "x", 1));

        (await ((Func<Task>)(() => ReadAsync(dir, 3, "abc", pacs: "GJ-0002"))).Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Scope);
        (await ((Func<Task>)(() => ReadAsync(dir, 2, "abc"))).Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Sequence, "3 arrived, 2 has not");
        (await ((Func<Task>)(() => ReadAsync(dir, 4, "abc"))).Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Replay, "3 was already applied");
        (await ((Func<Task>)(() => ReadAsync(dir, 3, "not-abc"))).Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Chain);
        (await ((Func<Task>)(() => ReadAsync(dir, 3, "abc", schema: "schema-B"))).Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Schema);
        (await ReadAsync(dir, 3, "abc")).PackSeq.Should().Be(3);
    }

    [Fact]
    public async Task A_data_file_altered_after_signing_is_refused_by_hash()
    {
        var dir = await WritePackAsync("p6", Manifest(), SiteSigner, null, ("t", "REPLACE INTO t VALUES (1);\n", 1));
        File.WriteAllText(Path.Combine(dir, PackEnvelope.DataDirectory, "t.sql"), "REPLACE INTO t VALUES (2);\n");

        var act = () => ReadAsync(dir);

        (await act.Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Hash);
    }

    [Fact]
    public async Task A_missing_data_file_is_refused_by_hash_too()
    {
        var dir = await WritePackAsync("p7", Manifest(), SiteSigner, null, ("t", "x", 1));
        File.Delete(Path.Combine(dir, PackEnvelope.DataDirectory, "t.sql"));

        var act = () => ReadAsync(dir);

        (await act.Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Hash);
    }

    [Fact]
    public async Task An_encrypted_pack_carries_no_plaintext_and_opens_only_with_the_recipients_key()
    {
        using var recipient = RSA.Create(2048);
        var dir = await WritePackAsync("p8", Manifest(), SiteSigner, recipient.ExportSubjectPublicKeyInfoPem(), ("t", "REPLACE INTO t VALUES ('secret-row');\n", 1));

        Directory.GetFiles(Path.Combine(dir, PackEnvelope.DataDirectory)).Should().ContainSingle().Which.Should().EndWith("t.sql" + BackupCrypto.Suffix);
        var manifest = await ReadAsync(dir);
        manifest.Encryption.Should().NotBeNull();
        manifest.Tables[0].EncryptedSha256.Should().NotBeNull();

        var noKey = () => PackEnvelope.OpenDataAsync(dir, manifest, Path.Combine(_root, "stage1"), null);
        (await noKey.Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Encryption);

        using var wrong = RSA.Create(2048);
        var wrongKey = () => PackEnvelope.OpenDataAsync(dir, manifest, Path.Combine(_root, "stage2"), wrong.ExportPkcs8PrivateKeyPem());
        (await wrongKey.Should().ThrowAsync<PackException>()).Which.Refusal.Should().Be(PackRefusal.Encryption);

        var staging = await PackEnvelope.OpenDataAsync(dir, manifest, Path.Combine(_root, "stage3"), recipient.ExportPkcs8PrivateKeyPem());
        File.ReadAllText(Path.Combine(staging, "t.sql")).Should().Contain("secret-row");
    }

    [Fact]
    public async Task The_next_pack_chains_to_the_hash_of_the_previous_manifest()
    {
        var first = await WritePackAsync("c1", Manifest(1), SiteSigner, null, ("t", "x", 1));
        var hash = await PackEnvelope.ManifestHashAsync(first);
        var second = await WritePackAsync("c2", Manifest(2, prev: hash), SiteSigner, null, ("t", "y", 1));

        (await ReadAsync(second, 2, hash)).PrevPackHash.Should().Be(hash);
        hash.Should().Be(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(first, PackEnvelope.ManifestFile)))).ToLowerInvariant());
    }

    [Fact]
    public async Task The_ledger_persists_cursors_atomically_and_starts_at_genesis()
    {
        var ledger = new PackLedger(Path.Combine(_root, "sync", "pack-ledger.json"));
        var state = await ledger.LoadAsync();
        PackLedger.NextProduced(state, PackType.Ledger).Should().Match<PackLedger.Cursor>(c => c.LastSeq == 0 && c.LastHash == PackManifest.Genesis);

        PackLedger.RecordProduced(state, PackType.Ledger, 1, "h1", "GJ-0001-L-000001");
        PackLedger.RecordApplied(state, PackType.Policy, 7, "h7", "GJ-0001-P-000007");
        await ledger.SaveAsync(state);

        var back = await ledger.LoadAsync();
        PackLedger.NextProduced(back, PackType.Ledger).LastSeq.Should().Be(1);
        PackLedger.NextApplied(back, PackType.Policy).Should().Match<PackLedger.Cursor>(c => c.LastSeq == 7 && c.LastHash == "h7");
        PackLedger.NextApplied(back, PackType.SiteData).LastSeq.Should().Be(0, "types are independent");
        File.Exists(ledger.Path + ".tmp").Should().BeFalse("write-then-rename leaves no temp file");
    }

    public void Dispose()
    {
        _siteCert.Dispose();
        _otherCert.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
