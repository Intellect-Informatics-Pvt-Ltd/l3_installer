using FluentAssertions;
using Installer.Actions.Install;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// The Debian/systemd unit renderer — ADR-0010.
///
/// These tests do two jobs. They cover the renderer, and they act as a **consistency contract
/// with the estate's own Ansible template**
/// (`ops/ansible/roles/deployapp/templates/l2r2-service.service.j2`). Two different unit shapes
/// for the same 26 services — one from Ansible online, one from the installer offline — is how
/// an offline node comes to behave differently from the estate under load, in a way nobody sees
/// until it does. ADR-0010 records that as the largest risk of having two authorities.
/// </summary>
public sealed class SystemdUnitWriterTests
{
    // The real vocabulary, not a hand-rolled subset: a test helper that resolves more (or less)
    // than production does is a test that proves nothing about production.
    private static string Resolve(string s) => InstallerTokenMap
        .Resolve(s,
            InstallerTokenMap.BuildInfrastructure(
                new SharedKernel.Configuration.InstallerOptions { DataRoot = "/data/epacs", BinaryRoot = "/opt/epacs" },
                new SharedKernel.Configuration.ServicesOptions()),
            "test")
        .Replace('\\', '/');

    private static ServiceMapEntry Entry(
        string name = "l3_FAS",
        string account = "epacs",
        IReadOnlyDictionary<string, string>? env = null,
        string[]? dataDirs = null,
        string firstAction = "restart",
        int firstDelay = 10) => new()
    {
        Name = name,
        DisplayName = $"ePACS {name}",
        Description = "A service",
        Executable = "${BinaryRoot}\\current\\services\\FAS",
        Arguments = "--urls http://127.0.0.1:5010",
        Account = account,
        StartOrder = 20,
        StopOrder = 20,
        StartupType = "Automatic",
        HealthCheck = new ServiceHealthCheck { Type = "tcp", Port = "5010" },
        Recovery = new ServiceRecovery
        {
            FirstFailure = new RecoveryAction { Action = firstAction, DelaySeconds = firstDelay },
            SecondFailure = new RecoveryAction { Action = "restart", DelaySeconds = 30 },
            Subsequent = new RecoveryAction { Action = "restart_and_bundle", DelaySeconds = 60 },
            ResetAfterSeconds = 300
        },
        DataDirectories = dataDirs ?? [],
        Environment = env ?? new Dictionary<string, string>(StringComparer.Ordinal)
    };

    private static string Render(ServiceMapEntry e, string runUser = "epacs") =>
        SystemdUnitWriter.Render(e, runUser, Resolve);

    // ── Naming ───────────────────────────────────────────────────────────────

    [Fact]
    public void Units_are_prefixed_so_they_can_never_collide_with_the_estate_or_the_distro()
    {
        // `systemctl status 'epacs-*'` must show ours and only ours. The estate's own Ansible
        // uses `l2r2-<name>.service`; a node could plausibly have both.
        SystemdUnitWriter.UnitName(Entry()).Should().Be("epacs-l3_FAS");
        SystemdUnitWriter.UnitFileName(Entry()).Should().Be("epacs-l3_FAS.service");
    }

    // ── Consistency with ops/ansible/roles/deployapp ─────────────────────────

    [Fact]
    public void Carries_the_same_load_bearing_properties_as_the_estate_template()
    {
        var unit = Render(Entry());

        unit.Should().Contain("Type=simple");
        unit.Should().Contain("After=network-online.target");
        unit.Should().Contain("Wants=network-online.target");
        unit.Should().Contain("Restart=on-failure");
        unit.Should().Contain("StartLimitBurst=5");
        unit.Should().Contain("StandardOutput=journal");
        unit.Should().Contain("StandardError=journal");
        unit.Should().Contain("WantedBy=multi-user.target");
    }

    [Fact]
    public void Sets_the_file_descriptor_limit_the_connection_pools_require()
    {
        // 26 services × Max Pool Size=2000 exhausts the default 1024 long before the pool limit,
        // and the symptom is "Too many open files", which reads as a code leak.
        Render(Entry()).Should().Contain("LimitNOFILE=65535");
    }

    [Fact]
    public void Hardening_is_modest_on_purpose()
    {
        // ProtectSystem=full, not strict: these services legitimately write report files, and
        // strict breaks them in ways that look like application bugs.
        var unit = Render(Entry());

        unit.Should().Contain("NoNewPrivileges=true");
        unit.Should().Contain("PrivateTmp=true");
        unit.Should().Contain("ProtectSystem=full");
        // On the directive lines only — the unit's own comment explains why strict is wrong,
        // and a test that cannot tell a comment from a setting breaks the next time someone
        // documents something.
        unit.Split('\n').Where(l => !l.TrimStart().StartsWith('#'))
            .Should().NotContain(l => l.Trim() == "ProtectSystem=strict");
        unit.Should().Contain("ProtectHome=true");
    }

    [Fact]
    public void Data_directories_become_ReadWritePaths()
    {
        var unit = Render(Entry(dataDirs: ["${DataRoot}\\logs", "${DataRoot}\\files"]));

        unit.Should().Contain("ReadWritePaths=/data/epacs/logs /data/epacs/files");
    }

    [Fact]
    public void No_ReadWritePaths_line_when_the_service_declares_no_directories()
    {
        Render(Entry()).Should().NotContain("ReadWritePaths=");
    }

    // ── The state-selection mechanism ────────────────────────────────────────

    [Fact]
    public void Environment_variables_reach_the_unit()
    {
        // On Windows this needs a REG_MULTI_SZ write because sc.exe has no verb for it. Here it
        // is three lines in [Service] — and it is the entire mechanism by which one codebase
        // serves every state.
        var unit = Render(Entry(env: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "AP",
            ["DOTNET_ENVIRONMENT"] = "AP"
        }));

        unit.Should().Contain("Environment=ASPNETCORE_ENVIRONMENT=AP");
        unit.Should().Contain("Environment=DOTNET_ENVIRONMENT=AP");
    }

    [Fact]
    public void Environment_lines_are_ordered_so_the_unit_is_reproducible()
    {
        // Byte-identical output for identical input: otherwise the Installer Agent's config
        // drift detector reports a change every time a unit is regenerated.
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["Z"] = "1", ["A"] = "2", ["M"] = "3" };

        var unit = Render(Entry(env: env));

        unit.IndexOf("Environment=A=", StringComparison.Ordinal)
            .Should().BeLessThan(unit.IndexOf("Environment=M=", StringComparison.Ordinal));
        unit.IndexOf("Environment=M=", StringComparison.Ordinal)
            .Should().BeLessThan(unit.IndexOf("Environment=Z=", StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_values_are_token_resolved()
    {
        var unit = Render(Entry(env: new Dictionary<string, string>(StringComparer.Ordinal) { ["ROOT"] = "${DataRoot}\\x" }));

        unit.Should().Contain("Environment=ROOT=/data/epacs/x");
    }

    [Fact]
    public void No_environment_block_when_the_service_declares_none()
    {
        Render(Entry()).Should().NotContain("Environment=");
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    [Fact]
    public void Windows_separators_in_the_service_map_are_translated()
    {
        // The map is authored with backslashes because Windows was the first target. A unit with
        // backslashes in ExecStart fails at start with a path nobody can read.
        var unit = Render(Entry());

        unit.Should().Contain("ExecStart=/opt/epacs/current/services/FAS --urls http://127.0.0.1:5010");
        unit.Should().NotContain("\\");
    }

    // ── Accounts ─────────────────────────────────────────────────────────────

    [Fact]
    public void Runs_as_the_declared_unprivileged_account()
    {
        var unit = Render(Entry(account: "epacs"), runUser: "epacs");

        unit.Should().Contain("User=epacs").And.Contain("Group=epacs");
    }

    // ── Recovery mapping ─────────────────────────────────────────────────────

    [Fact]
    public void A_service_that_must_not_restart_maps_to_Restart_no()
    {
        Render(Entry(firstAction: "none")).Should().Contain("Restart=no");
    }

    [Fact]
    public void restart_and_bundle_still_maps_to_on_failure()
    {
        // systemd has no equivalent; collecting a support bundle after repeated failure is the
        // Installer Agent's job, so the policy stays on-failure and the bundling stays put.
        Render(Entry(firstAction: "restart_and_bundle")).Should().Contain("Restart=on-failure");
    }

    [Fact]
    public void RestartSec_is_never_zero()
    {
        // A zero backoff turns a database outage into a CPU incident and an unreadable journal.
        Render(Entry(firstDelay: 0)).Should().Contain("RestartSec=1");
    }

    [Fact]
    public void Reset_window_comes_from_the_service_map()
    {
        Render(Entry()).Should().Contain("StartLimitIntervalSec=300");
    }

    // ── The file itself ──────────────────────────────────────────────────────

    [Fact]
    public void Warns_against_editing_it_on_the_host()
    {
        Render(Entry()).Should().Contain("DO NOT EDIT ON THE HOST");
    }

    [Fact]
    public async Task Renders_every_service_in_the_shipped_map_without_throwing()
    {
        // Contract test against what actually ships: a map entry the renderer cannot handle
        // should fail here, not on a node.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln"))) dir = dir.Parent;
        var loader = new Installer.Actions.Topology.ServiceMapLoader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Installer.Actions.Topology.ServiceMapLoader>.Instance);

        var services = await loader.LoadAsync(Path.Combine(dir!.FullName, "samples", "service-map.yaml"));

        foreach (var s in services)
        {
            var unit = SystemdUnitWriter.Render(s, "epacs", Resolve);
            unit.Should().Contain("[Unit]").And.Contain("[Service]").And.Contain("[Install]");
            unit.Should().NotContain("${",
                $"{s.Name} left an unresolved token in its unit — this is what caught the orchestrator " +
                "resolving only BinaryRoot and DataRoot while the map also uses ${Services:...}");
        }
    }

    // ── The defect this file caught ──────────────────────────────────────────

    [Fact]
    public void Service_ports_from_the_infrastructure_vocabulary_are_resolved()
    {
        // THE REGRESSION. samples/service-map.yaml puts ${Services:Web:HttpsPort} in ePACSWeb's
        // arguments and ${Services:MySql:Port} in MySQL's health check, and the Windows
        // orchestrator substituted only ${BinaryRoot} and ${DataRoot} — so the web service would
        // have been registered with
        //     --urls https://0.0.0.0:${Services:Web:HttpsPort}
        // which Kestrel cannot parse. Neither orchestrator had ever run, so it had never failed.
        var e = Entry() with { Arguments = "--urls https://0.0.0.0:${Services:Web:HttpsPort}" };

        var unit = Render(e);

        unit.Should().Contain("--urls https://0.0.0.0:443");
        unit.Should().NotContain("${");
    }

    [Fact]
    public void An_unknown_token_aborts_rather_than_registering_a_literal()
    {
        var e = Entry() with { Arguments = "--flag ${Services:NoSuch:Port}" };

        var act = () => Render(e);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Services:NoSuch:Port*");
    }
}
