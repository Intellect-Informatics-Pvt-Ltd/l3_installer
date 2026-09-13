using System.Globalization;
using FluentAssertions;
using Installer.Actions.Install;
using Installer.Actions.Topology;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// Contract tests over the GENERATED L2-R2 topology (`topology/`), which
/// `build/generate-topology.py` in the L2-R2 workspace emits from the modules' own appsettings
/// and `ops/ansible/group_vars/all.yml` (decision D1; gap G25). If the generator's output and
/// this framework ever disagree about shape, the failure belongs here — not on a node.
///
/// These read the checked-in files, not copies: a copy would drift from what the medium carries.
/// </summary>
public sealed class TopologyContractTests
{
    private static readonly string[] Infrastructure = ["ePACSMySQL", "ePACSCache", "ePACSEventing"];

    private static ServiceMapLoader NewLoader() => new(NullLogger<ServiceMapLoader>.Instance);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();
        return dir!.FullName;
    }

    private static string Topology(string file) => Path.Combine(RepoRoot(), "topology", file);

    private static SiteConfigPack Site => new()
    {
        Signature = "sig", PacsId = "GJ-0001", StateCode = "GJ", DataRoot = "/data/epacs"
    };

    // ── The map itself ───────────────────────────────────────────────────────

    [Fact]
    public async Task Linux_map_carries_the_infrastructure_and_all_27_application_services()
    {
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));

        var apps = services.Where(s => s.Name.StartsWith("l3_", StringComparison.Ordinal)).ToList();
        apps.Should().HaveCount(27, "the estate is 25 middleware + the UI + l3_lob");
        apps.Select(a => a.Name).Should().Contain(["l3_FAS", "l3_Loans", "l3_ERPClient", "l3_lob", "l3_membership"]);

        services.Select(s => s.Name).Should().Contain(["ePACSMySQL", "ePACSCache", "ePACSEventing", "ePACSSync", "ePACSInstallerAgent"]);
        services.Should().HaveCount(32);
    }

    [Fact]
    public async Task Windows_map_loads_with_the_same_service_set()
    {
        var linux = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));
        var windows = await NewLoader().LoadAsync(Topology("service-map.l2r2.windows.yaml"));

        windows.Select(s => s.Name).Should().BeEquivalentTo(linux.Select(s => s.Name));
        windows.Single(s => s.Name == "l3_FAS").Executable.Should().EndWith("dotnet.exe");
    }

    [Fact]
    public async Task Application_services_start_after_the_infrastructure_and_before_the_agents_in_the_estates_tiers()
    {
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));
        var by = services.ToDictionary(s => s.Name, StringComparer.Ordinal);

        // Masters (10) → membership/FAS (20) → modules (30) → UI (40), offset by 100.
        by["l3_PACDetailsAPI"].StartOrder.Should().Be(110);
        by["l3_FAS"].StartOrder.Should().Be(120);
        by["l3_Loans"].StartOrder.Should().Be(130);
        by["l3_ERPClient"].StartOrder.Should().Be(140);

        var infraMax = Infrastructure.Max(n => by[n].StartOrder);
        var apps = services.Where(s => s.Name.StartsWith("l3_", StringComparison.Ordinal)).ToList();
        apps.Min(a => a.StartOrder).Should().BeGreaterThan(infraMax, "a module must never start before its database");
        apps.Max(a => a.StartOrder).Should().BeLessThan(by["ePACSSync"].StartOrder, "the pack exporter starts once the ledger is up");

        // Shutdown walks backwards: the UI goes down first, MySQL last.
        by["l3_ERPClient"].StopOrder.Should().BeLessThan(by["l3_FAS"].StopOrder);
        by["ePACSMySQL"].StopOrder.Should().Be(services.Max(s => s.StopOrder));
    }

    [Fact]
    public async Task Every_application_service_selects_its_state_from_the_site_pack()
    {
        // This is the whole state-selection mechanism. A service without it runs the compiled-in
        // default, which does not fail — it serves the wrong state silently.
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));

        foreach (var app in services.Where(s => s.Name.StartsWith("l3_", StringComparison.Ordinal)))
        {
            app.Environment.Should().ContainKey("ASPNETCORE_ENVIRONMENT", app.Name);
            app.Environment["ASPNETCORE_ENVIRONMENT"].Should().Be("${epcfg:state_code}", app.Name);
            app.Executable.Should().Be("${BinaryRoot}/current/dotnet/dotnet", "framework-dependent, one bundled runtime (ADR-0009)");
            app.Arguments.Should().MatchRegex(@"^\$\{BinaryRoot\}/current/services/l3_\w+/[\w.]+\.dll$", app.Name);
            app.Account.Should().Be("l2r2", "the estate's own run user");
        }
    }

    [Fact]
    public async Task Health_checks_are_recorded_never_invented()
    {
        // 0 of 27 map /health/live or /health/ready (G31). Where a module maps a route it is
        // used; where it maps none the check is TCP, and the aggregator says "listening, not
        // known healthy" rather than assuming.
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));
        var apps = services.Where(s => s.Name.StartsWith("l3_", StringComparison.Ordinal)).ToList();

        apps.Should().OnlyContain(a => a.HealthCheck.Type == "http" || a.HealthCheck.Type == "tcp");
        apps.Single(a => a.Name == "l3_PACDetailsAPI").HealthCheck.Url.Should().Be("http://127.0.0.1:5001/health");
        apps.Single(a => a.Name == "l3_FAS").HealthCheck.Type.Should().Be("tcp");
        apps.Where(a => a.HealthCheck.Url is not null)
            .Should().OnlyContain(a => !a.HealthCheck.Url!.EndsWith("/health/live") && !a.HealthCheck.Url.EndsWith("/health/ready"),
                "if a module now serves the installer's contract, the assessment and this test move together");
    }

    [Fact]
    public async Task Ports_are_unique_across_the_whole_node()
    {
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));

        // Infrastructure ports are tokens (${Services:MySql:Port}); application ports are literal.
        var ports = services
            .Where(s => s.Name.StartsWith("l3_", StringComparison.Ordinal))
            .Select(s => s.HealthCheck.Port ?? new Uri(s.HealthCheck.Url!).Port.ToString(CultureInfo.InvariantCulture))
            .ToList();

        ports.Should().OnlyHaveUniqueItems();
        ports.Should().HaveCount(27, "every application service exposes exactly one port");
    }

    // ── Applications configuration ───────────────────────────────────────────

    [Fact]
    public void Applications_json_binds_into_ServicesOptions_with_27_entries()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Topology("appsettings.Applications.json"))
            .Build();
        var options = new ServicesOptions();
        config.GetSection(ServicesOptions.SectionName).Bind(options);

        options.Applications.Should().HaveCount(27);
        options.Applications["l3_FAS"].Port.Should().Be(5010);
        options.Applications["l3_FAS"].StartOrder.Should().Be(20);
        options.Applications["l3_FAS"].HealthPath.Should().BeNull();
        options.Applications["l3_PACDetailsAPI"].HealthPath.Should().Be("/health");
        options.Applications.Values.Select(a => a.Port).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Applications_json_and_the_map_agree_on_every_port()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Topology("appsettings.Applications.json")).Build();
        var options = new ServicesOptions();
        config.GetSection(ServicesOptions.SectionName).Bind(options);
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));

        foreach (var app in services.Where(s => s.Name.StartsWith("l3_", StringComparison.Ordinal)))
        {
            var port = app.HealthCheck.Port ?? new Uri(app.HealthCheck.Url!).Port.ToString(CultureInfo.InvariantCulture);
            options.Applications[app.Name].Port.ToString(CultureInfo.InvariantCulture).Should().Be(port, app.Name);
        }
    }

    // ── The token path, end to end ───────────────────────────────────────────

    [Fact]
    public async Task A_generated_entry_renders_to_a_unit_with_the_site_state_once_the_site_is_bound()
    {
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));
        var fas = services.Single(s => s.Name == "l3_FAS");
        var installer = new InstallerOptions { DataRoot = "/data/epacs", BinaryRoot = "/opt/epacs" };
        var tokens = InstallerTokenMap.Merge(
            InstallerTokenMap.BuildInfrastructure(installer, new ServicesOptions()),
            InstallerTokenMap.BuildSite(Site));

        var unit = SystemdUnitWriter.Render(fas, "l2r2", s => InstallerTokenMap.Resolve(s, tokens, "test"));

        unit.Should().Contain("ExecStart=/opt/epacs/current/dotnet/dotnet /opt/epacs/current/services/l3_FAS/FAS.dll");
        unit.Should().Contain("Environment=ASPNETCORE_ENVIRONMENT=GJ");
        unit.Should().Contain("Environment=DOTNET_ROLL_FORWARD=LatestPatch");
        unit.Should().Contain("User=l2r2");
    }

    [Fact]
    public async Task A_generated_entry_cannot_be_resolved_without_a_site_and_the_refusal_names_the_token()
    {
        // Unbound is not defaulted. A defaulted state runs the wrong configuration without failing.
        var services = await NewLoader().LoadAsync(Topology("service-map.l2r2.linux.yaml"));
        var fas = services.Single(s => s.Name == "l3_FAS");
        var source = new SiteTokenSource();
        var tokens = InstallerTokenMap.Merge(
            InstallerTokenMap.BuildInfrastructure(new InstallerOptions { DataRoot = "/d", BinaryRoot = "/b" }, new ServicesOptions()),
            source.Tokens);

        var act = () => InstallerTokenMap.Resolve(fas.Environment["ASPNETCORE_ENVIRONMENT"], tokens, "l3_FAS");

        act.Should().Throw<InvalidOperationException>().WithMessage("*${epcfg:state_code}*");
    }

    [Fact]
    public void SiteTokenSource_is_empty_until_bound_and_carries_the_site_after()
    {
        var source = new SiteTokenSource();
        source.Tokens.Should().BeEmpty();
        source.Site.Should().BeNull();

        var site = Site;
        source.Bind(site);

        source.Tokens["epcfg:state_code"].Should().Be("GJ");
        source.Tokens["epcfg:pacs_id"].Should().Be("GJ-0001");
        source.Site.Should().BeSameAs(site);
    }
}
