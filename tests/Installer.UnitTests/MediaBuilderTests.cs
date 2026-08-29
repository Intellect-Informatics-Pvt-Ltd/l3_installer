using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Installer.MediaBuilder;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// The media pipeline — W6.
///
/// These tests build real media with a real self-signed certificate and then attack them, because
/// the only interesting property of this tool is what it refuses. A builder that produces a
/// plausible-looking medium from wrong inputs is worse than no builder: the medium is carried to
/// a village before anybody finds out.
/// </summary>
public sealed class MediaBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-media-tests", Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _cert;

    public MediaBuilderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "app"));
        File.WriteAllText(Path.Combine(_root, "src", "app", "service.dll"), new string('a', 4096));
        File.WriteAllText(Path.Combine(_root, "src", "app", "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "src", "baseline.sql"), "CREATE TABLE t (id INT);");
        File.WriteAllText(Path.Combine(_root, "src", "kafka.tgz"), new string('k', 2048));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ePACS Media Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private CmsCodeSigner Signer => new(() => _cert);

    private string WriteSpec(string extra = "") =>
        WriteSpecFile($"""
            release:
              stack_version: "3.3.0"
              schema_version: 25
              created_by: "tests"
            payloads:
              - name: "epacs-services"
                source: "src/app"
                install_order: 10
                group: "core"
              - name: "baseline-schema"
                source: "src/baseline.sql"
                install_order: 20
                group: "core"
              - name: "kafka"
                source: "src/kafka.tgz"
                install_order: 30
                group: "eventing"
            {extra}
            """);

    private string WriteSpecFile(string yaml)
    {
        var path = Path.Combine(_root, "media-spec.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private Task<MediaBuildResult> BuildAsync(string[]? groups = null, ICodeSigner? signer = null, string? specPath = null) =>
        new MediaAssembler(TextWriter.Null).BuildAsync(
            MediaAssembler.LoadSpec(specPath ?? WriteSpec()),
            Path.Combine(_root, "medium"),
            groups ?? ["core", "cache"],
            signer ?? Signer);

    // ── The happy path, and what it proves ───────────────────────────────────

    [Fact]
    public async Task A_built_medium_verifies_with_the_installers_own_verifier()
    {
        // The whole point of the tool. "The build passed" and "the installer will accept this"
        // must be the same statement, so the last step runs ManifestVerificationService — the
        // class the node runs — rather than a reimplementation.
        var result = await BuildAsync();

        result.IsSigned.Should().BeTrue();
        result.Payloads.Should().HaveCount(2);
        File.Exists(result.ManifestPath).Should().BeTrue();
        File.Exists(result.SignaturePath!).Should().BeTrue();
    }

    [Fact]
    public async Task Hashes_are_measured_not_declared()
    {
        // samples/release-manifest.yaml carried hand-typed SHA-256 values and round-number sizes
        // for payloads no build produced. Every value here comes from the file beside it.
        var result = await BuildAsync();

        foreach (var p in result.Payloads)
        {
            var path = Path.Combine(result.OutputDirectory, p.File);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(path))).ToLowerInvariant();

            p.Sha256.Should().Be(actual);
            p.SizeBytes.Should().Be(new FileInfo(path).Length);
        }
    }

    [Fact]
    public async Task The_build_is_byte_reproducible()
    {
        // Two runs of the same release must produce the same manifest, or "did this medium
        // change?" cannot be answered by comparing hashes — and the manifest is signed, so a
        // reordered or re-timestamped one is also a different signature.
        var spec = WriteSpec();
        var first = await new MediaAssembler(TextWriter.Null).BuildAsync(
            MediaAssembler.LoadSpec(spec), Path.Combine(_root, "m1"), ["core"], Signer);
        var second = await new MediaAssembler(TextWriter.Null).BuildAsync(
            MediaAssembler.LoadSpec(spec), Path.Combine(_root, "m2"), ["core"], Signer);

        (await File.ReadAllTextAsync(first.ManifestPath))
            .Should().Be(await File.ReadAllTextAsync(second.ManifestPath));
    }

    // ── Component groups ─────────────────────────────────────────────────────

    [Fact]
    public async Task Payloads_outside_the_requested_groups_are_left_off_the_medium()
    {
        // ~290 MB of Kafka and JRE not carried to a site that will not run it (ADR-0003).
        var result = await BuildAsync(groups: ["core"]);

        result.Payloads.Select(p => p.Name).Should().NotContain("kafka");
        File.Exists(Path.Combine(result.OutputDirectory, "kafka.tgz")).Should().BeFalse();
    }

    [Fact]
    public async Task Enabling_the_group_puts_the_payload_back()
    {
        var result = await BuildAsync(groups: ["core", "eventing"]);

        result.Payloads.Select(p => p.Name).Should().Contain("kafka");
    }

    [Fact]
    public async Task A_medium_with_no_payloads_is_refused()
    {
        // It would verify successfully and install nothing.
        var act = () => BuildAsync(groups: ["nosuchgroup"]);

        await act.Should().ThrowAsync<MediaBuildException>().WithMessage("*no payloads*");
    }

    // ── Tamper detection: the reason any of this exists ──────────────────────

    [Fact]
    public async Task A_tampered_payload_is_caught()
    {
        var result = await BuildAsync();
        await File.AppendAllTextAsync(Path.Combine(result.OutputDirectory, "epacs-services.zip"), "x");

        var check = await MediaAssembler.VerifyAsync(result.OutputDirectory, expectedThumbprint: _cert.Thumbprint);

        check.Valid.Should().BeFalse();
        check.PayloadResults.Single(p => p.PayloadName == "epacs-services").Valid.Should().BeFalse();
    }

    [Fact]
    public async Task A_tampered_manifest_is_caught_by_the_signature()
    {
        // The payload hashes would still match — the attacker changed the manifest, not the
        // payloads. Only the signature catches this.
        var result = await BuildAsync();
        var text = await File.ReadAllTextAsync(result.ManifestPath);
        await File.WriteAllTextAsync(result.ManifestPath, text.Replace("3.3.0", "9.9.9", StringComparison.Ordinal));

        var check = await MediaAssembler.VerifyAsync(result.OutputDirectory, expectedThumbprint: _cert.Thumbprint);

        check.Valid.Should().BeFalse();
        check.Errors.Should().Contain(e => e.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_medium_signed_by_the_wrong_key_is_refused()
    {
        // The case pinning exists for: a perfectly valid signature from a signer we did not
        // authorise. Chain validation alone would accept this if the other key were also trusted.
        var result = await BuildAsync();

        var check = await MediaAssembler.VerifyAsync(
            result.OutputDirectory, expectedThumbprint: new string('A', 40));

        check.Valid.Should().BeFalse();
        check.Errors.Should().Contain(e => e.Contains("pinned to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_payload_file_is_caught()
    {
        var result = await BuildAsync();

        // Delete whatever the manifest actually names, not a filename guessed from the payload's
        // name: a file payload keeps its own file name, a directory payload becomes <name>.zip.
        var target = result.Payloads.Single(p => p.Name == "baseline-schema").File;
        File.Delete(Path.Combine(result.OutputDirectory, target));

        var check = await MediaAssembler.VerifyAsync(result.OutputDirectory, expectedThumbprint: _cert.Thumbprint);

        check.Valid.Should().BeFalse();
    }

    // ── Build-time refusals ──────────────────────────────────────────────────

    [Fact]
    public async Task A_payload_whose_source_does_not_exist_stops_the_build()
    {
        var spec = WriteSpecFile("""
            release:
              stack_version: "3.3.0"
            payloads:
              - name: "ghost"
                source: "src/not-published-yet"
                install_order: 10
                group: "core"
            """);

        var act = () => BuildAsync(groups: ["core"], specPath: spec);

        await act.Should().ThrowAsync<MediaBuildException>().WithMessage("*does not exist*");
    }

    [Fact]
    public async Task An_unsigned_medium_is_built_but_reported_as_unsigned()
    {
        // Buildable for development; the CLI turns this into exit 2 so a pipeline cannot mistake
        // it for a releasable artefact.
        var result = await new MediaAssembler(TextWriter.Null).BuildAsync(
            MediaAssembler.LoadSpec(WriteSpec()), Path.Combine(_root, "unsigned"), ["core"], signer: null);

        result.IsSigned.Should().BeFalse();
        result.SignaturePath.Should().BeNull();
    }

    [Fact]
    public async Task The_output_directory_is_cleaned_so_stale_payloads_cannot_survive()
    {
        // A medium assembled over a previous build can carry a payload the manifest no longer
        // lists. That verifies fine — verification checks that LISTED payloads are present and
        // correct, not that PRESENT payloads are listed — and installs something nobody intended.
        var output = Path.Combine(_root, "medium");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "leftover-from-last-release.zip"), "stale");

        await BuildAsync();

        File.Exists(Path.Combine(output, "leftover-from-last-release.zip")).Should().BeFalse();
    }

    // ── Signing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signing_without_a_certificate_refuses_rather_than_producing_an_unsigned_manifest()
    {
        var signer = new CmsCodeSigner(() => null);
        var path = Path.Combine(_root, "x.txt");
        await File.WriteAllTextAsync(path, "content");

        var act = () => signer.SignFileAsync(path, path + ".sig");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tamper-evidence*");
    }

    [Fact]
    public async Task The_signature_covers_the_manifest_and_therefore_every_payload_hash()
    {
        // One signature over ~1.8 GB of media, because the manifest names every payload and its
        // hash. Changing any payload breaks its hash; changing any hash breaks the signature.
        var result = await BuildAsync();
        var verified = await Signer.VerifyFileAsync(result.ManifestPath, result.SignaturePath!, _cert.Thumbprint);

        verified.Valid.Should().BeTrue();
        verified.SignerThumbprint.Should().Be(_cert.Thumbprint);
    }


    // ── The spec that ships ──────────────────────────────────────────────────

    [Fact]
    public async Task The_shipped_sample_spec_builds_and_verifies()
    {
        // Contract test, same shape as the service-map one: samples/media-spec.yaml is what a
        // release engineer copies to make a real spec, so a payload path that has gone stale
        // must fail here rather than on the first release attempt. CI builds from this file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;
        var spec = MediaAssembler.LoadSpec(Path.Combine(dir!.FullName, "samples", "media-spec.yaml"));

        var result = await new MediaAssembler(TextWriter.Null)
            .BuildAsync(spec, Path.Combine(_root, "sample-medium"), ["core"], Signer);

        result.Payloads.Should().NotBeEmpty();

        var check = await MediaAssembler.VerifyAsync(result.OutputDirectory, _cert.Thumbprint);
        check.Valid.Should().BeTrue();
    }

    [Fact]
    public void The_shipped_sample_spec_does_not_reference_the_stale_schema_dump()
    {
        // docs/AP_DDL.sql is a mysqldump of MHCluster3 and is not the estate's schema authority;
        // db/stable_baseline_ddl.sql in the L2-R2 workspace is. A sample that pointed at the
        // dump would teach the wrong thing to whoever copies it first.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "samples", "media-spec.yaml"));

        text.Split('\n')
            .Where(l => l.TrimStart().StartsWith("source:", StringComparison.Ordinal))
            .Should().NotContain(l => l.Contains("AP_DDL", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _cert.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
