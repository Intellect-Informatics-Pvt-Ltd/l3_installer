using FluentAssertions;
using ManifestVerifier;

namespace Installer.UnitTests;

public sealed class ManifestParserTests
{
    private readonly ManifestParser _parser = new();

    private const string ValidManifestYaml = """
        manifest:
          manifest_id: "rel-2026-05-04-3.2.1"
          stack_version: "3.2.1"
          schema_version: 25
          sync_protocol_version: "1.0"
          min_os_build: 17763
          installer_tool_version: "4.0.0"
          signing_cert_thumbprint: "ABC123"
          created_at: "2026-05-04T00:00:00Z"
          created_by: "release-pipeline"
          hotfix_base_version: null
        payloads:
          - name: "mysql"
            file: "mysql-8.4.2-winx64.zip"
            sha256: "abc123def456"
            size_bytes: 450000000
            install_order: 1
            required: true
          - name: "garnet"
            file: "garnet-1.0.0-win-x64.zip"
            sha256: "def456abc123"
            size_bytes: 25000000
            install_order: 2
            required: true
        compatibility:
          min_upgrade_from: "3.1.0"
          max_upgrade_from: "3.2.0"
          requires_side_by_side: false
          breaking_schema_change: false
        """;

    [Fact]
    public void Parse_ValidYaml_ReturnsManifestWithCorrectMetadata()
    {
        var result = _parser.Parse(ValidManifestYaml);

        result.Manifest.ManifestId.Should().Be("rel-2026-05-04-3.2.1");
        result.Manifest.StackVersion.Should().Be("3.2.1");
        result.Manifest.SchemaVersion.Should().Be(25);
        result.Manifest.MinOsBuild.Should().Be(17763);
        result.Manifest.InstallerToolVersion.Should().Be("4.0.0");
        result.Manifest.SigningCertThumbprint.Should().Be("ABC123");
        result.Manifest.HotfixBaseVersion.Should().BeNull();
    }

    [Fact]
    public void Parse_ValidYaml_ReturnsCorrectPayloadCount()
    {
        var result = _parser.Parse(ValidManifestYaml);

        result.Payloads.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ValidYaml_ReturnsPayloadsWithCorrectProperties()
    {
        var result = _parser.Parse(ValidManifestYaml);

        var mysql = result.Payloads.First(p => p.Name == "mysql");
        mysql.File.Should().Be("mysql-8.4.2-winx64.zip");
        mysql.Sha256.Should().Be("abc123def456");
        mysql.SizeBytes.Should().Be(450000000);
        mysql.InstallOrder.Should().Be(1);
        mysql.Required.Should().BeTrue();
    }

    [Fact]
    public void Parse_ValidYaml_ReturnsCorrectCompatibility()
    {
        var result = _parser.Parse(ValidManifestYaml);

        result.Compatibility.MinUpgradeFrom.Should().Be("3.1.0");
        result.Compatibility.MaxUpgradeFrom.Should().Be("3.2.0");
        result.Compatibility.RequiresSideBySide.Should().BeFalse();
        result.Compatibility.BreakingSchemaChange.Should().BeFalse();
    }

    [Fact]
    public void Parse_EmptyString_ThrowsArgumentException()
    {
        var act = () => _parser.Parse("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_MissingManifestSection_ThrowsInvalidOperationException()
    {
        var yaml = """
            payloads:
              - name: "test"
                file: "test.zip"
                sha256: "abc"
                size_bytes: 100
                install_order: 1
                required: true
            """;

        var act = () => _parser.Parse(yaml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing the 'manifest' root element*");
    }

    [Fact]
    public void Parse_NoPayloads_ThrowsInvalidOperationException()
    {
        var yaml = """
            manifest:
              manifest_id: "test"
              stack_version: "1.0.0"
              schema_version: 1
              min_os_build: 17763
              installer_tool_version: "1.0"
              signing_cert_thumbprint: "ABC"
              created_at: "2026-01-01T00:00:00Z"
              created_by: "test"
            """;

        var act = () => _parser.Parse(yaml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no payloads*");
    }

    [Fact]
    public void ParseFile_NonExistentFile_ThrowsFileNotFoundException()
    {
        var act = () => _parser.ParseFile("/nonexistent/path/manifest.yaml");

        act.Should().Throw<FileNotFoundException>();
    }
}
