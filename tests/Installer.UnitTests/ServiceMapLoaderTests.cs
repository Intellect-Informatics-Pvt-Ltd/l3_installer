using FluentAssertions;
using Installer.Actions.Topology;
using Microsoft.Extensions.Logging.Abstractions;

namespace Installer.UnitTests;

/// <summary>
/// The loader is the framework's only topology input, so these tests do two jobs: they cover the
/// parser, and they act as a contract test on the two service maps that actually ship in this
/// repository. If someone edits a map into a shape the framework cannot register, the failure
/// belongs here — not on a PACS node.
/// </summary>
public sealed class ServiceMapLoaderTests
{
    private static ServiceMapLoader NewLoader() => new(NullLogger<ServiceMapLoader>.Instance);

    /// <summary>
    /// Walks up from the test binary to the repository root. The maps under test are repository
    /// artefacts, not test fixtures — copying them here would let the copies drift from the
    /// files the installer actually reads, which is the whole failure this test guards against.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests must be able to locate the repository root");
        return dir!.FullName;
    }

    // ── Contract tests against the maps that ship ────────────────────────────

    [Fact]
    public async Task Loads_the_canonical_infrastructure_service_map()
    {
        var path = Path.Combine(RepoRoot(), "samples", "service-map.yaml");

        var services = await NewLoader().LoadAsync(path);

        services.Should().HaveCount(6);
        services.Select(s => s.Name).Should().ContainInOrder(
            "ePACSMySQL", "ePACSCache", "ePACSEventing", "ePACSWeb", "ePACSSync", "ePACSInstallerAgent");
    }

    [Fact]
    public async Task Preserves_every_health_check_type_in_the_canonical_map()
    {
        // The regression this pins: HarnessServiceMapLoader understands `http` only, so it
        // silently dropped the MySQL `command` check and the tcp checks for cache and eventing.
        // A dropped check does not fail — it passes vacuously, and the tier gate behind it
        // opens against a service that never came up.
        var path = Path.Combine(RepoRoot(), "samples", "service-map.yaml");

        var services = await NewLoader().LoadAsync(path);

        var mysql = services.Single(s => s.Name == "ePACSMySQL");
        mysql.HealthCheck.Type.Should().Be("command");
        mysql.HealthCheck.Command.Should().Contain("mysqladmin");
        mysql.HealthCheck.Arguments.Should().Contain("ping");
        mysql.DataDirectories.Should().HaveCount(2);

        var cache = services.Single(s => s.Name == "ePACSCache");
        cache.HealthCheck.Type.Should().Be("tcp");
        cache.HealthCheck.Port.Should().Be("${Services:Cache:Port}", "tokens are resolved at install time, not at parse time");

        var web = services.Single(s => s.Name == "ePACSWeb");
        web.HealthCheck.Type.Should().Be("http");
        web.HealthCheck.ExpectedStatus.Should().Be(200);
    }

    [Fact]
    public async Task Loads_the_harness_service_map_including_unmodelled_keys()
    {
        // The harness map carries `dependencies`, which this version does not model. Unknown
        // keys must be tolerated, or every map format addition becomes a breaking change.
        var path = Path.Combine(RepoRoot(), "harness", "packaging", "service-map.yaml");

        var services = await NewLoader().LoadAsync(path);

        services.Should().NotBeEmpty();
        services.Should().OnlyContain(s => s.HealthCheck.Type == "http");
    }

    [Fact]
    public async Task Filters_the_harness_map_by_group()
    {
        var path = Path.Combine(RepoRoot(), "harness", "packaging", "service-map.yaml");
        var loader = NewLoader();

        var all = await loader.LoadAsync(path);
        var pacsOnly = await loader.LoadAsync(path, ["pacs"]);

        pacsOnly.Should().NotBeEmpty();
        pacsOnly.Count.Should().BeLessThan(all.Count, "the harness map declares an nldr group too");
    }

    // ── Parser behaviour ─────────────────────────────────────────────────────

    private const string MinimalMap = """
        services:
          - name: "svcA"
            executable: "C:\\a.exe"
            start_order: 20
            stop_order: 10
            health_check:
              type: "tcp"
              port: "1234"
          - name: "svcB"
            executable: "C:\\b.exe"
            start_order: 10
            stop_order: 20
            health_check:
              type: "tcp"
              port: "5678"
        """;

    [Fact]
    public void Returns_services_in_start_order()
    {
        var services = NewLoader().Parse(MinimalMap);

        services.Select(s => s.Name).Should().ContainInOrder("svcB", "svcA");
    }

    [Fact]
    public void Applies_defaults_for_omitted_optional_fields()
    {
        var services = NewLoader().Parse(MinimalMap);
        var a = services.Single(s => s.Name == "svcA");

        a.DisplayName.Should().Be("svcA", "an omitted display name falls back to the service name");
        a.Account.Should().Be("LocalSystem");
        a.StartupType.Should().Be("Automatic");
        a.DataDirectories.Should().BeEmpty();
        a.Recovery.FirstFailure.Action.Should().Be("none", "an omitted recovery block means sc.exe's own default: take no action");
        a.Recovery.ResetAfterSeconds.Should().Be(86400);
    }

    [Fact]
    public void Ungrouped_services_survive_group_filtering()
    {
        // A map with no groups is a single-group map. Filtering it to nothing would mean an
        // install that registers zero services and reports success.
        var services = NewLoader().Parse(MinimalMap, ["pacs"]);

        services.Should().HaveCount(2);
    }

    [Fact]
    public void Rejects_a_duplicate_service_name()
    {
        const string yaml = """
            services:
              - name: "dupe"
                executable: "C:\\a.exe"
                health_check: { type: "tcp", port: "1" }
              - name: "dupe"
                executable: "C:\\b.exe"
                health_check: { type: "tcp", port: "2" }
            """;

        var act = () => NewLoader().Parse(yaml);

        act.Should().Throw<ServiceMapException>().WithMessage("*Duplicate service name*dupe*");
    }

    [Fact]
    public void Rejects_a_service_with_no_health_check()
    {
        const string yaml = """
            services:
              - name: "svcA"
                executable: "C:\\a.exe"
            """;

        var act = () => NewLoader().Parse(yaml);

        act.Should().Throw<ServiceMapException>().WithMessage("*no 'health_check'*");
    }

    [Theory]
    [InlineData("type: \"command\"", "requires 'command'")]
    [InlineData("type: \"tcp\"", "requires 'port'")]
    [InlineData("type: \"http\"", "requires 'url'")]
    [InlineData("type: \"smoke-signal\"", "unknown health_check type")]
    public void Rejects_a_half_specified_health_check(string healthCheckType, string expected)
    {
        var yaml = $"""
            services:
              - name: "svcA"
                executable: "C:\\a.exe"
                health_check:
                  {healthCheckType}
            """;

        var act = () => NewLoader().Parse(yaml);

        act.Should().Throw<ServiceMapException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Rejects_a_service_with_no_executable()
    {
        const string yaml = """
            services:
              - name: "svcA"
                health_check: { type: "tcp", port: "1" }
            """;

        var act = () => NewLoader().Parse(yaml);

        act.Should().Throw<ServiceMapException>().WithMessage("*no 'executable'*");
    }

    [Fact]
    public void Rejects_an_empty_map()
    {
        var act = () => NewLoader().Parse("services: []");

        act.Should().Throw<ServiceMapException>().WithMessage("*no 'services' entries*");
    }

    [Fact]
    public void Rejects_malformed_yaml_with_a_line_number()
    {
        var act = () => NewLoader().Parse("services:\n  - name: \"a\"\n   bad-indent: x\n");

        act.Should().Throw<ServiceMapException>().WithMessage("*not valid YAML at line*");
    }

    [Fact]
    public async Task Missing_file_is_fatal_rather_than_an_empty_topology()
    {
        var act = () => NewLoader().LoadAsync(Path.Combine(Path.GetTempPath(), "no-such-service-map.yaml"));

        await act.Should().ThrowAsync<ServiceMapException>().WithMessage("*not found*");
    }
}
