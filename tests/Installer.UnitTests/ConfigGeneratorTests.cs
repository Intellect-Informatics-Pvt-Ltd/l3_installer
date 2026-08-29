using FluentAssertions;
using Installer.Actions.Install;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// Site configuration generation.
///
/// Two properties carry most of the weight here. The generator must be able to address N
/// application services — a payload of 26 was inexpressible while the token map was a fixed
/// dictionary of four infrastructure ports. And an unresolved token must be fatal, because the
/// alternative is a config file containing a literal <c>${...}</c> that a service either fails
/// on at startup or silently treats as a value.
/// </summary>
public sealed class ConfigGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-cfg-tests", Guid.NewGuid().ToString("N"));
    private string TemplateDir => Path.Combine(_root, "templates");
    private string OutputDir => Path.Combine(_root, "out");

    private ServicesOptions _services = new();

    public ConfigGeneratorTests() => Directory.CreateDirectory(TemplateDir);

    private ConfigGenerator Build() => new(
        Options.Create(new InstallerOptions { DataRoot = @"D:\ePACSData", BinaryRoot = @"C:\ePACS" }),
        Options.Create(_services),
        NullLogger<ConfigGenerator>.Instance);

    private static SiteConfigPack Site => new()
    {
        Signature = "sig", PacsId = "AP-XYZ-0001", StateCode = "AP",
        DistrictCode = "GUNTUR", DataRoot = @"D:\ePACSData"
    };

    private string WriteTemplate(string name, string content)
    {
        var path = Path.Combine(TemplateDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── N application services: the point of F4 ──────────────────────────────

    [Fact]
    public async Task Addresses_any_application_service_by_name()
    {
        // The regression this pins. Before 2026-08-29 the token map was a fixed dictionary of
        // four infrastructure ports, so a 26-service payload could not be described at all.
        _services.Applications["l3_FAS"] = new ApplicationServiceOptions { Port = 5010, StartOrder = 20 };
        _services.Applications["l3_Loans"] = new ApplicationServiceOptions { Port = 5012 };
        WriteTemplate("ports.template.json", """{ "fas": "${Service:l3_FAS:Port}", "loans": "${Service:l3_Loans:Port}" }""");

        await Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        var output = await File.ReadAllTextAsync(Path.Combine(OutputDir, "ports.json"));
        output.Should().Contain("\"fas\": \"5010\"").And.Contain("\"loans\": \"5012\"");
    }

    [Fact]
    public async Task Scales_to_the_full_estate_service_count()
    {
        // 26 services: 25 middleware plus the ERPClient UI, per ops/ansible/group_vars/all.yml.
        for (var i = 1; i <= 26; i++)
        {
            _services.Applications[$"l3_svc{i}"] = new ApplicationServiceOptions { Port = 5000 + i };
        }
        var body = string.Join(",\n", Enumerable.Range(1, 26).Select(i => $"  \"svc{i}\": \"${{Service:l3_svc{i}:Port}}\""));
        WriteTemplate("all.template.json", "{\n" + body + "\n}");

        var result = await Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        result.TokensResolved.Should().Be(26);
        (await File.ReadAllTextAsync(Path.Combine(OutputDir, "all.json"))).Should().Contain("5026").And.NotContain("${");
    }

    [Fact]
    public async Task Service_names_resolve_case_insensitively()
    {
        // Names arrive from JSON, environment variables and a service map. l3_FAS and l3_fas
        // naming the same service in two of them is a mistake nobody would find quickly.
        _services.Applications["l3_FAS"] = new ApplicationServiceOptions { Port = 5010 };
        WriteTemplate("x.template.json", """{ "p": "${service:l3_fas:port}" }""");

        await Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        (await File.ReadAllTextAsync(Path.Combine(OutputDir, "x.json"))).Should().Contain("5010");
    }

    [Fact]
    public async Task A_service_in_the_topology_but_not_in_options_is_still_addressable()
    {
        // The estate generates its service model from each module's own appsettings — the code
        // is authoritative — so the map can legitimately carry a service the installer's options
        // were never told about.
        WriteTemplate("t.template.json", """{ "acct": "${Service:l3_New:Account}" }""");
        var topology = new List<ServiceMapEntry>
        {
            new()
            {
                Name = "l3_New", DisplayName = "New", Executable = "x.exe", Account = "ePACSAppSvc",
                StartOrder = 30, StopOrder = 30,
                HealthCheck = new ServiceHealthCheck { Type = "tcp", Port = "1" },
                Recovery = new ServiceRecovery
                {
                    FirstFailure = new RecoveryAction { Action = "none", DelaySeconds = 0 },
                    SecondFailure = new RecoveryAction { Action = "none", DelaySeconds = 0 },
                    Subsequent = new RecoveryAction { Action = "none", DelaySeconds = 0 }
                }
            }
        };

        await Build().GenerateAllAsync(Site, TemplateDir, OutputDir, topology);

        (await File.ReadAllTextAsync(Path.Combine(OutputDir, "t.json"))).Should().Contain("ePACSAppSvc");
    }

    // ── Unresolved tokens are fatal ──────────────────────────────────────────

    [Fact]
    public async Task An_unresolved_token_aborts_generation()
    {
        WriteTemplate("bad.template.json", """{ "a": "${Service:l3_Missing:Port}" }""");

        var act = () => Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        await act.Should().ThrowAsync<ConfigGenerationException>().WithMessage("*l3_Missing*");
    }

    [Fact]
    public async Task Every_unresolved_token_is_listed_at_once()
    {
        // One failed build per missing token is one trip to a site with no internet, per token.
        WriteTemplate("bad.template.json", """{ "a": "${Nope:One}", "b": "${Nope:Two}", "c": "${Nope:Three}" }""");

        var act = () => Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        var ex = await act.Should().ThrowAsync<ConfigGenerationException>();
        ex.Which.Message.Should().Contain("Nope:One").And.Contain("Nope:Two").And.Contain("Nope:Three");
    }

    [Fact]
    public async Task No_output_file_is_left_behind_when_a_token_is_unresolved()
    {
        WriteTemplate("bad.template.json", """{ "a": "${Missing}" }""");

        try { await Build().GenerateAllAsync(Site, TemplateDir, OutputDir); } catch (ConfigGenerationException) { }

        File.Exists(Path.Combine(OutputDir, "bad.json")).Should().BeFalse(
            "a half-resolved config is worse than none: the service starts and uses a default where a value was meant");
    }

    [Fact]
    public async Task A_missing_template_directory_is_fatal_rather_than_skipped()
    {
        // Was a warning-and-return, which meant the node installed with default paths, ports and
        // identity and reported success.
        var act = () => Build().GenerateAllAsync(Site, Path.Combine(_root, "nope"), OutputDir);

        await act.Should().ThrowAsync<ConfigGenerationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task An_empty_template_directory_is_fatal()
    {
        var act = () => Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        await act.Should().ThrowAsync<ConfigGenerationException>().WithMessage("*No *template*");
    }

    // ── The template that ships ──────────────────────────────────────────────

    [Fact]
    public async Task The_shipped_site_template_resolves_completely()
    {
        // Contract test. This is the file an operator's node is configured from; every token in
        // it must be one the generator can supply, or the first real install fails.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;
        var shipped = Path.Combine(dir!.FullName, "packaging", "config-templates");

        var result = await Build().GenerateAllAsync(Site, shipped, OutputDir);

        result.GeneratedFiles.Should().ContainSingle(f => f.EndsWith("appsettings.Site.json", StringComparison.Ordinal));
        var output = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("appsettings.Site.json", StringComparison.Ordinal)));
        output.Should().NotContain("${");
    }

    [Fact]
    public async Task The_shipped_template_overrides_the_hardcoded_linux_log_path()
    {
        // ~15 L2-R2 source files hardcode /data/L3-logs/... as a FALLBACK, which is what makes
        // it dangerous: on Windows with no override, logging silently stops rather than failing.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;

        var result = await Build().GenerateAllAsync(Site, Path.Combine(dir!.FullName, "packaging", "config-templates"), OutputDir);
        var output = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("appsettings.Site.json", StringComparison.Ordinal)));

        // Assert on the EFFECTIVE value, not on the file text: the template's own explanatory
        // comment names /data/L3-logs, and a test that cannot tell a comment from a setting is
        // a test that will break the next time someone documents something.
        using var doc = System.Text.Json.JsonDocument.Parse(output,
            new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        var path = doc.RootElement
            .GetProperty("Serilog").GetProperty("WriteTo")[0]
            .GetProperty("Args").GetProperty("path").GetString();

        path.Should().StartWith(@"D:\ePACSData");
        path.Should().NotContain("/data/L3-logs");
    }

    [Fact]
    public async Task The_shipped_template_sets_both_connection_string_keys()
    {
        // The estate's services are not consistent about which they read; ops/compose sets both
        // on every service for exactly this reason. Setting one produces a service that starts
        // and then cannot reach the database.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;

        var result = await Build().GenerateAllAsync(Site, Path.Combine(dir!.FullName, "packaging", "config-templates"), OutputDir);
        var output = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("appsettings.Site.json", StringComparison.Ordinal)));

        output.Should().Contain("\"conn\"").And.Contain("\"DefaultConnection\"");
    }

    [Fact]
    public async Task The_shipped_template_carries_no_password()
    {
        // A connection string with a password in a file on disk is a credential in every support
        // bundle ever taken from that node.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;

        var result = await Build().GenerateAllAsync(Site, Path.Combine(dir!.FullName, "packaging", "config-templates"), OutputDir);
        var output = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("appsettings.Site.json", StringComparison.Ordinal)));

        output.Should().NotContain("Pwd=").And.NotContain("Password=");
    }

    [Fact]
    public async Task Site_identity_reaches_the_generated_file()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;

        var result = await Build().GenerateAllAsync(Site, Path.Combine(dir!.FullName, "packaging", "config-templates"), OutputDir);
        var output = await File.ReadAllTextAsync(result.GeneratedFiles.Single(f => f.EndsWith("appsettings.Site.json", StringComparison.Ordinal)));

        output.Should().Contain("AP-XYZ-0001").And.Contain("\"AP\"");
    }

    [Fact]
    public async Task Output_is_written_atomically_leaving_no_temp_file()
    {
        // Write-then-rename. A power cut mid-write must not leave a service with a truncated
        // config that parses as far as the cut and silently omits everything after it.
        WriteTemplate("a.template.json", """{ "root": "${DataRoot}" }""");

        await Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        Directory.GetFiles(OutputDir, "*.tmp").Should().BeEmpty();
    }


    // ── JSON escaping: found by running it, not by reading it ────────────────

    [Fact]
    public async Task Windows_paths_are_escaped_so_the_generated_json_parses()
    {
        // THE DEFECT THIS PINS. ${DataRoot} expands to D:\ePACSData. Dropped raw into a JSON
        // string that becomes "D:\ePACSData\\logs" — and \e is an invalid escape, so the whole
        // file fails to parse. Every path token on the target platform hits this. The first
        // thing that notices is a service refusing to start on a node in a village.
        WriteTemplate("p.template.json", """{ "path": "${DataRoot}\\logs\\app.log" }""");

        await Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        var output = await File.ReadAllTextAsync(Path.Combine(OutputDir, "p.json"));
        var act = () => System.Text.Json.JsonDocument.Parse(output);
        act.Should().NotThrow("a generated config that does not parse is found by the service, on the node, after the installer reported success");
    }

    [Fact]
    public async Task A_template_that_would_produce_invalid_json_is_not_written()
    {
        WriteTemplate("broken.template.json", """{ "a": "unclosed ${DataRoot} """);

        var act = () => Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        await act.Should().ThrowAsync<ConfigGenerationException>().WithMessage("*invalid JSON*");
        File.Exists(Path.Combine(OutputDir, "broken.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Non_json_templates_are_not_escaped()
    {
        // my.ini, kafka.properties and garnet.conf are not JSON; escaping their backslashes
        // would corrupt every path in them.
        WriteTemplate("app.template.ini", "datadir = ${DataRoot}\\mysql");

        await Build().GenerateAllAsync(Site, TemplateDir, OutputDir);

        var output = await File.ReadAllTextAsync(Path.Combine(OutputDir, "app.ini"));
        output.Should().Be(@"datadir = D:\ePACSData\mysql");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
