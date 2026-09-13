using System.IO.Compression;
using System.Text.RegularExpressions;
using FluentAssertions;
using Installer.MediaBuilder;

namespace Installer.UnitTests;

/// <summary>
/// The secret gate refuses a medium whose payload carries a credential (G27, 28.5). Its rules are
/// the estate's own — <c>build/config-hygiene.py</c> — and the last test here reads that file and
/// asserts the patterns are byte-equal, so the two guards cannot drift apart: whatever the
/// estate's CI would flag, this gate refuses, and nothing more.
/// </summary>
public sealed class SecretGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-secret-gate-tests", Guid.NewGuid().ToString("N"));

    // ── The decision, per leaf (the estate's Rules.is_secret) ────────────────

    [Theory]
    [InlineData("SenderPassword", "gmail-app-password-1234", true)]
    [InlineData("ClientSecret", "b6c5a0f2-8c2f-4e1a-9b7e-0d1c2e3f4a5b", true)]
    [InlineData("AdminPassword", "Sup3rSecret!", true)]
    [InlineData("aadharapikey", "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA7", true)]
    [InlineData("aadharkey", "shortish-but-real", true)]
    [InlineData("XApiKey", "ac7d6e3b7f1a4c2e9d8b", true)]
    [InlineData("conn", "Server=1.2.3.4;Uid=root;Pwd=devpass", true)]
    [InlineData("DefaultConnection", "Server=1.2.3.4;Uid=root;Password=devpass", true)]
    public void Flags_what_the_estate_flags(string key, string value, bool expected)
    {
        SecretGate.IsSecret(key, value, "/" + key).Should().Be(expected);
    }

    [Theory]
    [InlineData("verifyaadharotp", "https://apicooperation.ap.gov.in/APCOBAPI/ThirdParty/UIDVerifyOTP")]   // an endpoint is not a credential
    [InlineData("TokenUrl", "api/Token")]                                                                 // a key naming a location is not a credential
    [InlineData("ClientSecret", "REPLACE_VIA_SECRETS")]                                                   // a placeholder is what a shipped file holds
    [InlineData("ClientSecret", "${epcfg:client_secret}")]                                                // a token, resolved on the node
    [InlineData("Password", "short")]                                                                     // below the estate's minimum length
    [InlineData("Password", "xxxxxxxxxx")]                                                                // a run of x is a placeholder
    [InlineData("FASUrl", "http://192.168.25.131:5010/api/v1/")]                                          // topology
    [InlineData("Description", "a perfectly ordinary sentence that is long")]                            // key does not match
    public void Does_not_flag_what_the_estate_does_not_flag(string key, string value)
    {
        SecretGate.IsSecret(key, value, "/" + key).Should().BeFalse();
    }

    [Fact]
    public void A_url_that_smuggles_a_credential_in_its_query_string_is_flagged()
    {
        // The estate's rule: an embedded query-string credential removes the endpoint exemption,
        // and the KEY must still match the secret pattern - so `SmsApiKey` is flagged while a
        // location key (`SmsUrl`) is cleared by LOCATION_KEY. That second half is a gap in
        // config-hygiene.py, mirrored here on purpose: equality with the estate's guard first.
        SecretGate.IsSecret("SmsApiKey", "http://gw.example/send?apikey=abcdef123456", "/SmsApiKey").Should().BeTrue();
        SecretGate.IsSecret("SmsApiKey", "http://gw.example/send", "/SmsApiKey").Should().BeFalse("an endpoint without a credential is topology");
        SecretGate.IsSecret("SmsUrl", "http://gw.example/send?apikey=abcdef123456", "/SmsUrl").Should().BeFalse();
    }

    [Fact]
    public void An_allow_pattern_over_the_path_clears_a_fixture_value()
    {
        var allow = new List<Regex> { new("^/Fixtures/", RegexOptions.IgnoreCase) };
        SecretGate.IsSecret("Password", "fixture-password-1", "/Fixtures/Password", allow).Should().BeFalse();
        SecretGate.IsSecret("Password", "fixture-password-1", "/Real/Password", allow).Should().BeTrue();
    }

    [Fact]
    public void Mask_never_reveals_more_than_the_ends()
    {
        SecretGate.Mask("gmail-app-password-1234").Should().Be("gm************34");
        SecretGate.Mask("abcd").Should().Be("****");
    }

    // ── Scanning a payload ───────────────────────────────────────────────────

    private string Payload(string name, params (string file, string json)[] files)
    {
        var dir = Path.Combine(_root, name);
        foreach (var (file, json) in files)
        {
            var path = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        return dir;
    }

    [Fact]
    public void Scans_every_appsettings_file_in_a_directory_and_names_file_and_key()
    {
        var dir = Payload("services",
            ("l3_ERPClient/appsettings.json", """{ "Email": { "SmtpHost": "smtp.gmail.com", "SenderPassword": "gmail-app-password-1234" }, "ERPKeys": { "FASUrl": "http://127.0.0.1:5010/" } }"""),
            ("l3_FAS/appsettings.json", """{ "Iam": { "Keycloak": { "ClientSecret": "REPLACE_VIA_SECRETS" } } }"""),
            ("l3_FAS/appsettings.KA.json", """{ "Messaging": { "Sms": { "ApiKey": "ka-sms-api-key-value" } } }"""),
            ("l3_FAS/notes.json", """{ "Password": "not scanned: not an appsettings file" }"""));

        var findings = SecretGate.Scan(dir);

        findings.Should().HaveCount(2);
        findings.Should().ContainSingle(f => f.File.Replace('\\', '/') == "l3_ERPClient/appsettings.json" && f.KeyPath == "/Email/SenderPassword");
        findings.Should().ContainSingle(f => f.File.Replace('\\', '/') == "l3_FAS/appsettings.KA.json" && f.KeyPath == "/Messaging/Sms/ApiKey");
        findings.Should().OnlyContain(f => !f.MaskedValue.Contains("gmail-app-password-1234") && !f.MaskedValue.Contains("ka-sms-api-key-value"),
            "the value is masked in every message, including the refusal");
    }

    [Fact]
    public void Scans_inside_a_zip_payload_too()
    {
        var dir = Payload("zipped", ("svc/appsettings.json", """{ "Db": { "Password": "zipped-secret-value" } }"""));
        var zip = Path.Combine(_root, "zipped.zip");
        ZipFile.CreateFromDirectory(dir, zip);

        var findings = SecretGate.Scan(zip);

        findings.Should().ContainSingle().Which.KeyPath.Should().Be("/Db/Password");
    }

    [Fact]
    public void A_file_that_does_not_parse_cannot_be_cleared()
    {
        var dir = Payload("broken", ("appsettings.json", "{ not json"));

        SecretGate.Scan(dir).Should().ContainSingle().Which.KeyPath.Should().Be("(unparseable)");
    }

    [Fact]
    public void A_clean_payload_has_no_findings()
    {
        var dir = Payload("clean",
            ("appsettings.json", """{ "ConnectionStrings": { "conn": "Server=127.0.0.1;Uid=epacs_app" }, "Iam": { "Authority": "https://idp/realms/x", "ClientSecret": "REPLACE_VIA_SECRETS" } }"""));

        SecretGate.Scan(dir).Should().BeEmpty();
    }

    // ── Through the builder ──────────────────────────────────────────────────

    [Fact]
    public async Task The_builder_refuses_a_payload_with_a_credential_before_staging_a_byte()
    {
        var dir = Payload("app", ("appsettings.json", """{ "Email": { "SenderPassword": "gmail-app-password-1234" } }"""));
        var spec = Path.Combine(_root, "media-spec.yaml");
        File.WriteAllText(spec, $"""
            release:
              stack_version: "3.3.0"
              created_by: "tests"
            payloads:
              - name: "epacs-services"
                source: "app"
                install_order: 10
                group: "core"
            """);
        var output = Path.Combine(_root, "medium");

        var act = () => new MediaAssembler(TextWriter.Null).BuildAsync(MediaAssembler.LoadSpec(spec), output, ["core"], signer: null);

        var ex = await act.Should().ThrowAsync<SecretGateException>();
        ex.Which.Message.Should().Contain("epacs-services").And.Contain("/Email/SenderPassword").And.Contain("gm************34")
            .And.NotContain("gmail-app-password-1234");
        Directory.GetFiles(output).Should().BeEmpty("nothing is staged after a refusal");
    }

    [Fact]
    public async Task The_builder_honours_an_allow_pattern()
    {
        var dir = Payload("app2", ("appsettings.json", """{ "Fixtures": { "Password": "fixture-password-1" } }"""));
        var spec = Path.Combine(_root, "media-spec2.yaml");
        File.WriteAllText(spec, """
            release:
              stack_version: "3.3.0"
              created_by: "tests"
            payloads:
              - name: "fixtures"
                source: "app2"
                install_order: 10
                group: "core"
            """);

        var result = await new MediaAssembler(TextWriter.Null, ["^/Fixtures/"]).BuildAsync(
            MediaAssembler.LoadSpec(spec), Path.Combine(_root, "medium2"), ["core"], signer: null);

        result.Payloads.Should().ContainSingle();
    }

    // ── The contract with the estate's own guard ─────────────────────────────

    private static string? WorkspaceFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln")))
        {
            dir = dir.Parent;
        }

        var candidate = dir?.Parent is null ? null : Path.Combine(dir.Parent.FullName, relative);
        return candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    [SkippableFact]
    public void The_rules_are_byte_equal_to_config_hygiene_py()
    {
        var path = WorkspaceFile("build/config-hygiene.py");
        Skip.If(path is null, "build/config-hygiene.py is not checked out beside this repository");
        var py = File.ReadAllText(path!);

        static string PyRegex(string source, string name)
        {
            var m = Regex.Match(source, name + @"\s*=\s*re\.compile\(r'((?:[^'\\]|\\.)*)'");
            m.Success.Should().BeTrue($"{name} must still be a raw-string regex in config-hygiene.py");
            return m.Groups[1].Value;
        }

        static string PyRaw(string source, string name)
        {
            var m = Regex.Match(source, name + @"\s*=\s*r'((?:[^'\\]|\\.)*)'");
            m.Success.Should().BeTrue($"{name} must still be a raw string in config-hygiene.py");
            return m.Groups[1].Value;
        }

        PyRegex(py, "SECRET_VALUE").Should().Be(SecretGate.SecretValuePattern);
        PyRaw(py, "DEFAULT_SECRET_KEY").Should().Be(SecretGate.DefaultSecretKeyPattern);
        PyRegex(py, "PLACEHOLDER").Should().Be(SecretGate.PlaceholderPattern);
        PyRegex(py, "ENDPOINT_URL").Should().Be(SecretGate.EndpointUrlPattern);
        PyRegex(py, "LOCATION_KEY").Should().Be(SecretGate.LocationKeyPattern);
        PyRegex(py, "URL_EMBEDDED_SECRET").Should().Be(SecretGate.UrlEmbeddedSecretPattern);
        Regex.Match(py, @"MIN_SECRET_LEN\s*=\s*(\d+)").Groups[1].Value.Should().Be(SecretGate.MinSecretLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public void A_real_module_on_r2_dev_stable_still_carries_TD_123_and_the_gate_says_so()
    {
        // This test documents the state of the estate, not a wish: while TD-123 is unrotated,
        // a medium built from a plain publish of ERPClient MUST be refused. When the owner
        // rotates and redacts, this test flips - and that flip is the evidence.
        var path = WorkspaceFile("l3_ERPClient/ERPClient/appsettings.json");
        Skip.If(path is null, "l3_ERPClient is not checked out beside this repository");

        var dir = Path.Combine(_root, "erpclient");
        Directory.CreateDirectory(dir);
        File.Copy(path!, Path.Combine(dir, "appsettings.json"));

        var findings = SecretGate.Scan(dir);

        findings.Should().NotBeEmpty("r2-dev-stable's ERPClient appsettings.json carried TD-123 credentials on 2026-09-13");
        findings.Should().OnlyContain(f => !f.MaskedValue.Contains("smtp.gmail.com"), "hosts are not secrets");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
