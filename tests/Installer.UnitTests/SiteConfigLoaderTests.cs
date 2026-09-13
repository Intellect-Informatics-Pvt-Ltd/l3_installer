using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Security;
using FluentAssertions;
using Installer.Core.SiteConfig;
using Microsoft.Extensions.Logging.Abstractions;

namespace Installer.UnitTests;

/// <summary>
/// The .epcfg is how a node learns which PACS it is. Before this loader existed the CLI parsed
/// <c>/config:</c>, printed the path and never opened the file — so every site-specific value
/// the installer claimed to honour actually came from a default.
/// </summary>
public sealed class SiteConfigLoaderTests : IDisposable
{
    private readonly List<string> _temp = [];
    private readonly X509Certificate2 _releaseKey = SelfSigned("CN=ePACS release key (test)");
    private readonly X509Certificate2 _impostor = SelfSigned("CN=impostor");

    private SiteConfigLoader NewLoader(string? pinned = null) =>
        new(new CmsCodeSigner(() => null), Options.Create(new InstallerOptions { DataRoot = "/d", BinaryRoot = "/b", ExpectedSigningThumbprint = pinned ?? _releaseKey.Thumbprint }), NullLogger<SiteConfigLoader>.Instance);

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        return new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>Signs a pack the way the state does: a detached CMS sidecar over the exact bytes.</summary>
    private async Task<string> SignAsync(string path, X509Certificate2? with = null)
    {
        await new CmsCodeSigner(() => with ?? _releaseKey).SignFileAsync(path, path + SiteConfigLoader.SignatureSuffix);
        _temp.Add(path + SiteConfigLoader.SignatureSuffix);
        return path;
    }

    private string WritePack(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"epacs-{Guid.NewGuid():N}.epcfg");
        File.WriteAllText(path, json);
        _temp.Add(path);
        return path;
    }

    private const string Valid = """
        {
          "signature": "MIIBogYJKoZIhvcNAQcCoIIBkzCCAY8CAQE=",
          "schema_version": 1,
          "pacs_id": "AP-XYZ-0001",
          "state_code": "AP",
          "district_code": "GUNTUR",
          "data_root": "D:\\ePACSData"
        }
        """;

    [Fact]
    public async Task Loads_a_pack_whose_detached_signature_verifies_against_the_pinned_release_key()
    {
        var path = await SignAsync(WritePack(Valid));

        var pack = await NewLoader().LoadAsync(path);

        pack.PacsId.Should().Be("AP-XYZ-0001");
        pack.StateCode.Should().Be("AP");
        pack.DataRoot.Should().Be(@"D:\ePACSData");
    }

    [Fact]
    public async Task An_embedded_signature_field_alone_is_refused_because_no_build_ever_verified_one()
    {
        // 7.9: until 2026-09-13 a non-placeholder 'signature' field was "presence only" - it
        // made an unsigned pack look signed. Now it is refused, naming the sidecar to produce.
        var act = () => NewLoader().LoadAsync(WritePack(Valid));

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*embedded*never verified*openssl cms*");
    }

    [Fact]
    public async Task A_pack_altered_after_signing_is_refused()
    {
        var path = await SignAsync(WritePack(Valid));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"AP\"", "\"KA\"", StringComparison.Ordinal));

        var act = () => NewLoader().LoadAsync(path);

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*does not verify*nothing was read*");
    }

    [Fact]
    public async Task A_pack_signed_by_an_impostor_is_refused()
    {
        var path = await SignAsync(WritePack(Valid), with: _impostor);

        var act = () => NewLoader().LoadAsync(path);

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*does not verify*");
    }

    [Fact]
    public async Task Reads_the_sample_pack_that_ships_in_this_repository()
    {
        // Contract test: the sample is what an operator copies to make a real pack, so it must
        // stay loadable. It is unsigned, hence allowUnsigned.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "samples", "site-config-pack.epcfg");

        var pack = await NewLoader().LoadAsync(path, allowUnsigned: true);

        pack.PacsId.Should().NotBeNullOrWhiteSpace();
        pack.StateCode.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Refuses_an_unsigned_pack_by_default()
    {
        var path = WritePack(Valid.Replace("MIIBogYJKoZIhvcNAQcCoIIBkzCCAY8CAQE=", "", StringComparison.Ordinal));

        var act = () => NewLoader().LoadAsync(path);

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*carries no signature*");
    }

    [Fact]
    public async Task Refuses_the_sample_signature_placeholder()
    {
        // The sample ships "BASE64_ENCODED_SIGNATURE_PLACEHOLDER". A placeholder that parses is
        // more dangerous than one that does not: it makes an unsigned pack look signed.
        var path = WritePack(Valid.Replace("MIIBogYJKoZIhvcNAQcCoIIBkzCCAY8CAQE=", "BASE64_ENCODED_SIGNATURE_PLACEHOLDER", StringComparison.Ordinal));

        var act = () => NewLoader().LoadAsync(path);

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*carries no signature*");
    }

    [Fact]
    public async Task Accepts_an_unsigned_pack_only_when_explicitly_allowed()
    {
        var path = WritePack(Valid.Replace("MIIBogYJKoZIhvcNAQcCoIIBkzCCAY8CAQE=", "", StringComparison.Ordinal));

        var pack = await NewLoader().LoadAsync(path, allowUnsigned: true);

        pack.PacsId.Should().Be("AP-XYZ-0001");
    }

    [Theory]
    [InlineData("pacs_id")]
    [InlineData("state_code")]
    [InlineData("data_root")]
    public async Task Refuses_a_pack_missing_a_required_field(string field)
    {
        var json = System.Text.RegularExpressions.Regex.Replace(Valid, $"\\s*\"{field}\":[^,\n]*,?", "");

        var act = () => NewLoader().LoadAsync(WritePack(json));

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage($"*{field}*");
    }

    [Theory]
    [InlineData("a")]        // too short
    [InlineData("ap")]       // lowercase — would select the wrong appsettings file
    [InlineData("ANDHRA")]   // too long
    [InlineData("A1")]       // not letters
    public async Task Refuses_a_malformed_state_code(string stateCode)
    {
        // This value becomes ASPNETCORE_ENVIRONMENT in every service. A wrong one does not
        // crash — it runs another state's configuration, silently.
        var path = WritePack(Valid.Replace("\"state_code\": \"AP\"", $"\"state_code\": \"{stateCode}\"", StringComparison.Ordinal));

        var act = () => NewLoader().LoadAsync(path);

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*state_code*");
    }

    [Fact]
    public async Task Refuses_malformed_json_rather_than_defaulting()
    {
        var act = () => NewLoader().LoadAsync(WritePack("{ not json"));

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public async Task Refuses_a_missing_file()
    {
        var act = () => NewLoader().LoadAsync(Path.Combine(Path.GetTempPath(), "no-such.epcfg"));

        await act.Should().ThrowAsync<SiteConfigException>().WithMessage("*not found*");
    }

    public void Dispose()
    {
        _releaseKey.Dispose();
        _impostor.Dispose();
        foreach (var p in _temp.Where(File.Exists)) File.Delete(p);
    }
}
