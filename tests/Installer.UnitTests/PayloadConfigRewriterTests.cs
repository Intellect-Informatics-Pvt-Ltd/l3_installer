using System.Text.Json.Nodes;
using FluentAssertions;
using Installer.Actions.Install;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// The rewriter puts the node's facts into each deployed service's own appsettings.json - the
/// only file every code path in the estate reads (G26). These pin the three things it writes
/// (overlay, sibling URLs, credential), the three things it must never do (invent a key, touch an
/// external host, write a file that does not parse), and - through the workspace contract test -
/// that the generated sibling table actually clears every dev-server address from a real module.
/// </summary>
public sealed class PayloadConfigRewriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-rewriter-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ISecretStore> _secrets = new();

    private const string Password = "p4ss;with'special chars";

    private PayloadConfigRewriter Build() => new(_secrets.Object, NullLogger<PayloadConfigRewriter>.Instance);

    private string Services => Path.Combine(_root, "releases", "3.3.0", "services");

    private static ServiceMapEntry App(string name, int port) => new()
    {
        Name = name, DisplayName = name, Executable = "${BinaryRoot}/current/dotnet/dotnet",
        Arguments = $"${{BinaryRoot}}/current/services/{name}/{name[3..]}.dll", Account = "l2r2",
        StartOrder = 120, StopOrder = 80,
        HealthCheck = new ServiceHealthCheck { Type = "tcp", Host = "127.0.0.1", Port = port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        Recovery = Recovery()
    };

    private static ServiceMapEntry Infra(string name) => new()
    {
        Name = name, DisplayName = name, Executable = "${BinaryRoot}/current/mysql/bin/mysqld", Account = "epacs-db",
        StartOrder = 10, StopOrder = 90,
        HealthCheck = new ServiceHealthCheck { Type = "tcp", Host = "127.0.0.1", Port = "3306" },
        Recovery = Recovery()
    };

    private static ServiceRecovery Recovery() => new()
    {
        FirstFailure = new RecoveryAction { Action = "restart", DelaySeconds = 30 },
        SecondFailure = new RecoveryAction { Action = "restart", DelaySeconds = 60 },
        Subsequent = new RecoveryAction { Action = "restart", DelaySeconds = 120 }
    };

    private string WriteService(string name, string json)
    {
        var dir = Path.Combine(Services, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteOverlay(string json)
    {
        var path = Path.Combine(_root, "config", "appsettings.Site.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteSiblings(string json)
    {
        var path = Path.Combine(_root, "media", "config", "sibling-urls.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private const string FasLike = """
        {
          // a comment the estate's files sometimes carry
          "ConnectionStrings": { "conn": "Server=192.168.25.131;Database=dev;Uid=root;Pwd=devpass", "DefaultConnection": "Server=192.168.25.131;Database=dev;Uid=root;Password=devpass" },
          "APIKeys": {
            "PacsAPIUrl": "http://192.168.25.131:5001/api/v1/",
            "MembershipUrl": "http://192.168.25.131:5005/api/v1/",
            "EKYCAadharOtpapiUrl": "http://203.112.135.169:8079/EkycMhServices/ekyc_otp/",
            "XApiKey": "not-a-url"
          },
          "Serilog": { "WriteTo": [ { "Name": "Console" } ], "MinimumLevel": "Warning" },
          "Caching": { "Enabled": false, "Namespace": { "ModuleName": "fas" } },
        }
        """;

    private const string Overlay = """
        {
          "//": "GENERATED",
          "Site": { "PacsId": "GJ-0001", "StateCode": "GJ" },
          "ConnectionStrings": {
            "//": [ "commentary" ],
            "conn": "Server=127.0.0.1;Port=3306;Database=epacs;Uid=epacs_app;AllowUserVariables=true",
            "DefaultConnection": "Server=127.0.0.1;Port=3306;Database=epacs;Uid=epacs_app;AllowUserVariables=true"
          },
          "Caching": { "Enabled": true, "StateCode": "GJ", "Configuration": "127.0.0.1:6379" },
          "Serilog": { "WriteTo": [ { "Name": "File", "Args": { "path": "/data/epacs/logs/app/epacs-.log" } } ] }
        }
        """;

    private const string Siblings = """
        {
          "services": {
            "l3_FAS": { "APIKeys": { "PacsAPIUrl": "http://127.0.0.1:5001/api/v1/", "MembershipUrl": "http://127.0.0.1:5005/api/v1/", "NotInFile": "http://127.0.0.1:9999/" } },
            "l3_ERPClient": { "ERPKeys": { "FASUrl": "http://127.0.0.1:5010/api/v1/" } }
          }
        }
        """;

    [Fact]
    public async Task Writes_overlay_sibling_urls_and_credential_into_the_service_file()
    {
        _secrets.Setup(s => s.RetrieveAsync(PayloadConfigRewriter.AppPasswordSecretKey, It.IsAny<CancellationToken>())).ReturnsAsync(Password);
        var path = WriteService("l3_FAS", FasLike);

        var result = await Build().RewriteAsync(Services, WriteOverlay(Overlay), WriteSiblings(Siblings), [App("l3_FAS", 5010), Infra("ePACSMySQL")]);

        result.Rewritten.Should().ContainKey("l3_FAS");
        result.Skipped.Should().Equal("ePACSMySQL");
        result.PasswordWritten.Should().BeTrue();

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        // Sibling URLs: rewritten to localhost; the external eKYC host and the non-URL untouched.
        doc["APIKeys"]!["PacsAPIUrl"]!.GetValue<string>().Should().Be("http://127.0.0.1:5001/api/v1/");
        doc["APIKeys"]!["MembershipUrl"]!.GetValue<string>().Should().Be("http://127.0.0.1:5005/api/v1/");
        doc["APIKeys"]!["EKYCAadharOtpapiUrl"]!.GetValue<string>().Should().StartWith("http://203.112.135.169");
        doc["APIKeys"]!["XApiKey"]!.GetValue<string>().Should().Be("not-a-url");
        doc["APIKeys"]!.AsObject().ContainsKey("NotInFile").Should().BeFalse("a key the module does not read is never invented");

        // Credential: the dev password is gone, the node's is quoted because it carries ; and '.
        var conn = doc["ConnectionStrings"]!["conn"]!.GetValue<string>();
        conn.Should().StartWith("Server=127.0.0.1;Port=3306;Database=epacs;Uid=epacs_app;AllowUserVariables=true");
        conn.Should().NotContain("devpass").And.Contain("Pwd='p4ss;with''special chars'");
        doc["ConnectionStrings"]!["DefaultConnection"]!.GetValue<string>().Should().NotContain("Password=devpass");
        doc["ConnectionStrings"]!.AsObject().ContainsKey("//").Should().BeFalse("template commentary is not configuration");

        // Overlay: arrays replaced whole, objects merged, the module's own keys kept.
        doc["Serilog"]!["WriteTo"]!.AsArray().Should().HaveCount(1);
        doc["Serilog"]!["WriteTo"]![0]!["Name"]!.GetValue<string>().Should().Be("File");
        doc["Serilog"]!["MinimumLevel"]!.GetValue<string>().Should().Be("Warning", "a key the overlay does not set survives");
        doc["Caching"]!["Enabled"]!.GetValue<bool>().Should().BeTrue();
        doc["Caching"]!["Namespace"]!["ModuleName"]!.GetValue<string>().Should().Be("fas");
        doc["Site"]!["StateCode"]!.GetValue<string>().Should().Be("GJ");

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
    }

    [Fact]
    public async Task Refuses_a_service_that_runs_from_services_but_has_no_configuration()
    {
        // It would start under compiled-in defaults and might even pass a TCP check.
        Directory.CreateDirectory(Path.Combine(Services, "l3_Loans"));

        var act = () => Build().RewriteAsync(Services, null, null, [App("l3_Loans", 5012)]);

        await act.Should().ThrowAsync<ConfigGenerationException>().WithMessage("*l3_Loans*no appsettings.json*refused*");
    }

    [Fact]
    public async Task Refuses_a_file_that_does_not_parse_rather_than_rewriting_it()
    {
        WriteService("l3_FAS", "{ this is not json");

        var act = () => Build().RewriteAsync(Services, null, null, [App("l3_FAS", 5010)]);

        await act.Should().ThrowAsync<ConfigGenerationException>().WithMessage("*l3_FAS*does not parse*");
    }

    [Fact]
    public async Task Without_a_password_it_says_so_and_writes_none()
    {
        _secrets.Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var path = WriteService("l3_FAS", FasLike);

        var result = await Build().RewriteAsync(Services, WriteOverlay(Overlay), null, [App("l3_FAS", 5010)]);

        result.PasswordWritten.Should().BeFalse();
        File.ReadAllText(path).Should().NotContain("Pwd=").And.NotContain("devpass", "the overlay replaced the connection strings");
    }

    [Fact]
    public async Task Sibling_urls_use_the_section_the_module_actually_reads()
    {
        // ERPClient keeps its siblings under ERPKeys, not APIKeys. Rewriting APIKeys for it is
        // inert and leaves the web client - the one thing a browser drives - on the dev server.
        var path = WriteService("l3_ERPClient", """{ "ERPKeys": { "FASUrl": "http://192.168.25.131:5010/api/v1/" }, "APIKeys": { "FASUrl": "http://192.168.25.131:5010/" } }""");

        await Build().RewriteAsync(Services, null, WriteSiblings(Siblings), [App("l3_ERPClient", 5000)]);

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        doc["ERPKeys"]!["FASUrl"]!.GetValue<string>().Should().Be("http://127.0.0.1:5010/api/v1/");
        doc["APIKeys"]!["FASUrl"]!.GetValue<string>().Should().Be("http://192.168.25.131:5010/", "the table names ERPKeys for this service, not APIKeys");
    }

    [Fact]
    public async Task A_sibling_file_without_a_services_object_is_refused_not_ignored()
    {
        WriteService("l3_FAS", FasLike);

        var act = () => Build().RewriteAsync(Services, null, WriteSiblings("""{ "wrong": {} }"""), [App("l3_FAS", 5010)]);

        await act.Should().ThrowAsync<ConfigGenerationException>().WithMessage("*services*");
    }

    [Fact]
    public async Task Rewriting_twice_is_idempotent()
    {
        _secrets.Setup(s => s.RetrieveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("pw");
        var path = WriteService("l3_FAS", FasLike);
        var overlay = WriteOverlay(Overlay);
        var siblings = WriteSiblings(Siblings);

        await Build().RewriteAsync(Services, overlay, siblings, [App("l3_FAS", 5010)]);
        var first = File.ReadAllText(path);
        await Build().RewriteAsync(Services, overlay, siblings, [App("l3_FAS", 5010)]);

        File.ReadAllText(path).Should().Be(first, "repair re-runs this and must not stack Pwd= entries");
        first.Should().ContainAll("Pwd=pw").And.NotContain("Pwd=pw;Pwd=");
    }

    // ── Pure functions ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Server=a;Uid=u;Pwd=old", "Server=a;Uid=u;Pwd=new")]
    [InlineData("Server=a;Password=old;Uid=u", "Server=a;Uid=u;Pwd=new")]
    [InlineData("Server=a;Uid=u", "Server=a;Uid=u;Pwd=new")]
    public void WithPassword_replaces_any_existing_credential(string input, string expected)
    {
        PayloadConfigRewriter.WithPassword(input, "new").Should().Be(expected);
    }

    [Fact]
    public void WithPassword_quotes_a_value_the_connector_would_misparse()
    {
        PayloadConfigRewriter.WithPassword("Server=a", "a;b'c").Should().Be("Server=a;Pwd='a;b''c'");
    }

    [Fact]
    public void Merge_is_deep_for_objects_whole_for_arrays_and_ignores_commentary()
    {
        var target = JsonNode.Parse("""{ "A": { "x": 1, "y": 2 }, "B": [1, 2, 3], "C": "keep" }""")!.AsObject();
        var overlay = JsonNode.Parse("""{ "//": "note", "A": { "//": "note", "y": 20, "z": 30 }, "B": [9], "D": { "n": { "m": 1 } } }""")!.AsObject();

        var count = PayloadConfigRewriter.Merge(target, overlay);

        target["A"]!["x"]!.GetValue<int>().Should().Be(1);
        target["A"]!["y"]!.GetValue<int>().Should().Be(20);
        target["A"]!["z"]!.GetValue<int>().Should().Be(30);
        target["A"]!.AsObject().ContainsKey("//").Should().BeFalse();
        target["B"]!.AsArray().Should().HaveCount(1);
        target["C"]!.GetValue<string>().Should().Be("keep");
        target["D"]!["n"]!["m"]!.GetValue<int>().Should().Be(1);
        count.Should().Be(4, "y, z, B, D.n.m");
    }

    // ── The workspace contract: the generated table clears a REAL module's dev-server addresses ──

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

    [SkippableTheory]
    [InlineData("l3_FAS", "l3_FAS/FAS/appsettings.json", "APIKeys")]
    [InlineData("l3_ERPClient", "l3_ERPClient/ERPClient/appsettings.json", "ERPKeys")]
    [InlineData("l3_Loans", "l3_Loans/Loans/appsettings.json", "APIKeys")]
    public async Task The_generated_sibling_table_leaves_no_dev_server_address_in_a_real_module(string service, string relative, string section)
    {
        var source = WorkspaceFile(relative);
        Skip.If(source is null, $"{relative} is not checked out beside this repository");
        var siblings = WorkspaceFile("l3_installer/topology/sibling-urls.json");
        Skip.If(siblings is null, "topology/sibling-urls.json is not generated");

        var path = WriteService(service, File.ReadAllText(source!));
        await Build().RewriteAsync(Services, null, siblings, [App(service, 5000)]);

        var block = JsonNode.Parse(File.ReadAllText(path))!.AsObject()[section]!.AsObject();
        var stillOnDevServer = block
            .Where(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("192.168.25.131", StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        stillOnDevServer.Should().BeEmpty(
            "every sibling URL must be re-pointed; a survivor is a 75-second timeout per call on an offline node");
        block.Count(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
             .Should().BeGreaterThan(20, "the module addresses most of its 26 siblings");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
