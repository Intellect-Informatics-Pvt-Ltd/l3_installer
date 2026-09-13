using FluentAssertions;
using Installer.Actions.Database;
using Installer.Actions.Install;
using Installer.Actions.Platform;
using Installer.Actions.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// The Debian least-privilege engines (tasks.md 25, 26, X1; repair 18.4). The whole point of a
/// least-privilege engine is that what it runs is exactly what it says, so these are contract
/// tests over the commands and the ruleset: every process call is recorded and compared.
/// </summary>
public sealed class LinuxPlatformEnginesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-platform-tests", Guid.NewGuid().ToString("N"));
    private readonly List<(string Exe, string Args)> _calls = [];
    private readonly Mock<IProcessRunner> _runner = new();
    private readonly Dictionary<string, ProcessResult> _answers = new(StringComparer.Ordinal);

    public LinuxPlatformEnginesTests()
    {
        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string exe, string args, string? _, string? _, IReadOnlyCollection<string>? _, IReadOnlyDictionary<string, string>? _, CancellationToken _) =>
            {
                _calls.Add((exe, args));
                return Task.FromResult(_answers.GetValueOrDefault(exe + " " + args, Ok("")));
            });
    }

    private static ProcessResult Ok(string stdout) => new() { ExitCode = 0, StandardOutput = stdout, StandardError = "" };
    private static ProcessResult Fail(string stderr, int code = 1) => new() { ExitCode = code, StandardOutput = "", StandardError = stderr };

    private InstallerOptions Opts => new() { DataRoot = Path.Combine(_root, "data"), BinaryRoot = Path.Combine(_root, "opt") };

    private LinuxAclEngine Acl(ServicesOptions? services = null) =>
        new(_runner.Object, Options.Create(Opts), Options.Create(services ?? new ServicesOptions()), new SiteTokenSource(), NullLogger<LinuxAclEngine>.Instance);

    private NftablesFirewallManager Firewall(ServicesOptions services) =>
        new(_runner.Object, Options.Create(services), NullLogger<NftablesFirewallManager>.Instance);

    private SystemdServiceAccountProvisioner Accounts() =>
        new(_runner.Object, NullLogger<SystemdServiceAccountProvisioner>.Instance);

    private static ServiceMapEntry Entry(string name, string account, params string[] dataDirs) => new()
    {
        Name = name, DisplayName = name, Executable = "/x", Account = account, StartOrder = 10, StopOrder = 10,
        HealthCheck = new ServiceHealthCheck { Type = "tcp", Host = "127.0.0.1", Port = "1" },
        Recovery = new ServiceRecovery
        {
            FirstFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
            SecondFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
            Subsequent = new RecoveryAction { Action = "restart", DelaySeconds = 1 }
        },
        DataDirectories = dataDirs
    };

    // ── ACL: the rules ───────────────────────────────────────────────────────

    [Fact]
    public void Rules_give_each_writer_its_own_directories_and_the_services_read_only_binaries_and_config()
    {
        var rules = Acl().GenerateRules([
            Entry("ePACSMySQL", "epacs-db", "${DataRoot}/mysql/data", "${DataRoot}/mysql/logs"),
            Entry("ePACSInstallerAgent", "LocalSystem", "${DataRoot}/agent")
        ]);

        var by = rules.ToDictionary(r => r.Path.Replace('\\', '/'), StringComparer.Ordinal);
        var data = Opts.DataRoot.Replace('\\', '/');
        var bin = Opts.BinaryRoot.Replace('\\', '/');

        by[$"{data}/mysql/data"].Should().Match<AclRule>(r => r.Account == "epacs-db" && r.Permission == AclAccessLevel.ReadWrite);
        by[$"{data}/mysql/logs"].Account.Should().Be("epacs-db");
        by[$"{data}/agent"].Should().Match<AclRule>(r => r.Account == "root" && r.Permission == AclAccessLevel.None, "LocalSystem means root here");
        by[$"{data}/attachments"].Should().Match<AclRule>(r => r.Account == "l2r2" && r.Permission == AclAccessLevel.ReadWrite);
        by[$"{data}/logs/app"].Account.Should().Be("l2r2", "the Serilog file sink for every application service");
        by[$"{data}/packs"].Account.Should().Be("l2r2", "ledger packs out, policy packs in");
        by[$"{data}/config"].Should().Match<AclRule>(r => r.Account == "l2r2" && r.Permission == AclAccessLevel.ReadOnly, "services read it, never write it");
        by[$"{bin}/releases"].Should().Match<AclRule>(r => r.Account == "l2r2" && r.Permission == AclAccessLevel.ReadOnly, "a service cannot modify its own binaries");
        by[$"{data}/keys"].Should().Match<AclRule>(r => r.Account == "root" && r.Permission == AclAccessLevel.None && r.Recursive, "the secret store is root-only, 0700");
        by[data].Should().Match<AclRule>(r => r.Account == "root" && !r.Recursive, "the root itself stays traversable (0755) so accounts reach their subdirectories");
    }

    [Fact]
    public async Task Rules_for_the_generated_topology_cover_every_declared_data_directory()
    {
        var loader = new Installer.Actions.Topology.ServiceMapLoader(NullLogger<Installer.Actions.Topology.ServiceMapLoader>.Instance);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ePACS.Installer.sln")))
        {
            dir = dir.Parent;
        }

        var services = await loader.LoadAsync(Path.Combine(dir!.FullName, "topology", "service-map.l2r2.linux.yaml"));

        var rules = Acl().GenerateRules(services);

        var declared = services.SelectMany(s => s.DataDirectories).Count();
        rules.Should().HaveCountGreaterThanOrEqualTo(16 + declared);
        rules.Should().Contain(r => r.Path.EndsWith("/mysql/data") && r.Account == "epacs-db");
        rules.Should().Contain(r => r.Path.EndsWith("/packs/outbound") && r.Account == "l2r2");
        rules.Select(r => r.Path).Should().OnlyContain(p => !p.Contains("${"), "every token resolved");
    }

    // ── ACL: the commands ────────────────────────────────────────────────────

    [Fact]
    public void ReadWrite_is_chown_to_the_account_and_0750()
    {
        var cmds = LinuxAclEngine.CommandsFor(new AclRule { Path = "/data/x", Account = "l2r2", Permission = AclAccessLevel.ReadWrite });
        cmds.Should().Equal(("chown", "-R l2r2:l2r2 \"/data/x\""), ("chmod", "-R u=rwX,g=rX,o= \"/data/x\""));
    }

    [Fact]
    public void ReadOnly_is_root_owned_with_the_reader_as_group()
    {
        var cmds = LinuxAclEngine.CommandsFor(new AclRule { Path = "/opt/releases", Account = "l2r2", Permission = AclAccessLevel.ReadOnly });
        cmds.Should().Equal(("chown", "-R root:l2r2 \"/opt/releases\""), ("chmod", "-R u=rwX,g=rX,o= \"/opt/releases\""));
    }

    [Fact]
    public void None_is_root_only_0700_for_a_leaf_and_0755_for_a_traversable_root()
    {
        LinuxAclEngine.CommandsFor(new AclRule { Path = "/data/keys", Account = "root", Permission = AclAccessLevel.None, Recursive = true })
            .Should().Equal(("chown", "-R root:root \"/data/keys\""), ("chmod", "-R u=rwX,g=,o= \"/data/keys\""));
        LinuxAclEngine.CommandsFor(new AclRule { Path = "/data", Account = "root", Permission = AclAccessLevel.None, Recursive = false })
            .Should().Equal(("chown", "root:root \"/data\""), ("chmod", "u=rwx,g=rx,o=rx \"/data\""));
    }

    [Fact]
    public async Task Apply_runs_exactly_the_stated_commands_and_creates_a_missing_directory()
    {
        var path = Path.Combine(_root, "data", "attachments");
        var rules = new[] { new AclRule { Path = path, Account = "l2r2", Permission = AclAccessLevel.ReadWrite } };

        await Acl().ApplyRulesAsync(rules);

        Directory.Exists(path).Should().BeTrue();
        _calls.Should().Equal(("chown", $"-R l2r2:l2r2 \"{path}\""), ("chmod", $"-R u=rwX,g=rX,o= \"{path}\""));
    }

    [Fact]
    public async Task Apply_stops_on_the_first_failed_command_and_names_it()
    {
        var path = Path.Combine(_root, "data", "attachments");
        _answers[$"chown -R l2r2:l2r2 \"{path}\""] = Fail("chown: invalid user: 'l2r2:l2r2'");

        var act = () => Acl().ApplyRulesAsync([new AclRule { Path = path, Account = "l2r2", Permission = AclAccessLevel.ReadWrite }]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*chown*invalid user*fails later*");
        _calls.Should().HaveCount(1, "chmod after a failed chown would set the wrong owner's mode");
    }

    [Fact]
    public async Task Verify_reads_owner_and_mode_and_names_every_mismatch()
    {
        var rw = Path.Combine(_root, "data", "attachments");
        var ro = Path.Combine(_root, "data", "config");
        Directory.CreateDirectory(rw);
        Directory.CreateDirectory(ro);
        _answers[$"stat -c \"%U %a\" \"{rw}\""] = Ok("root 755\n");            // wrong owner and mode
        _answers[$"stat -c \"%U %a\" \"{ro}\""] = Ok("root 750\n");            // right
        _answers[$"stat -c \"%G\" \"{ro}\""] = Ok("root\n");                    // wrong group for a read-only grant

        var result = await Acl().VerifyAsync([
            new AclRule { Path = rw, Account = "l2r2", Permission = AclAccessLevel.ReadWrite },
            new AclRule { Path = ro, Account = "l2r2", Permission = AclAccessLevel.ReadOnly },
            new AclRule { Path = Path.Combine(_root, "missing"), Account = "l2r2", Permission = AclAccessLevel.ReadWrite }
        ]);

        result.Valid.Should().BeFalse();
        result.Mismatches.Should().HaveCount(4);
        result.Mismatches.Should().Contain(m => m.Contains("owner is root, expected l2r2"));
        result.Mismatches.Should().Contain(m => m.Contains("mode is 755, expected 750"));
        result.Mismatches.Should().Contain(m => m.Contains("group is root, expected l2r2"));
        result.Mismatches.Should().Contain(m => m.EndsWith(": missing"));
    }

    // ── Firewall ─────────────────────────────────────────────────────────────

    private static ServicesOptions WithApps() => new()
    {
        Applications =
        {
            ["l3_FAS"] = new ApplicationServiceOptions { Port = 5010 },
            ["l3_ERPClient"] = new ApplicationServiceOptions { Port = 5000 }
        }
    };

    [Fact]
    public void Firewall_rules_keep_the_database_off_the_LAN_and_open_the_application_ports()
    {
        var rules = Firewall(WithApps()).GenerateRules();

        rules.Single(r => r.Name == "MySQL").Should().Match<FirewallRule>(r => r.Port == 3306 && r.LocalAddress == "127.0.0.1");
        rules.Single(r => r.Name == "Cache").LocalAddress.Should().Be("127.0.0.1");
        rules.Single(r => r.Name == "SSH").Port.Should().Be(22);
        rules.Single(r => r.Name == "l3_FAS").Should().Match<FirewallRule>(r => r.Port == 5010 && r.LocalAddress == null && r.Action == FirewallAction.Allow);
        rules.Should().OnlyContain(r => r.Direction == FirewallDirection.Inbound, "outbound is not restricted on a node whose pack path needs whatever link exists");
    }

    [Fact]
    public void Ruleset_is_one_owned_table_with_drop_by_default_loopback_open_and_the_stated_ports()
    {
        var text = NftablesFirewallManager.Render(Firewall(WithApps()).GenerateRules());

        text.Should().Contain("table inet epacs {");
        text.Should().Contain("policy drop;");
        text.Should().Contain("iif lo accept");
        text.Should().Contain("ct state established,related accept");
        text.Should().Contain("tcp dport 3306 iif != lo drop comment \"ePACS - MySQL (localhost only)\"");
        text.Should().Contain("tcp dport 5010 accept comment \"ePACS - l3_FAS\"");
        text.Should().Contain("tcp dport 22 accept comment \"ePACS - SSH\"");
        text.Should().Contain("chain output {").And.Contain("policy accept;");
        text.Split('\n').Count(l => l.Contains("dport")).Should().Be(8, "5 localhost-only, SSH, 2 apps");
    }

    [Fact]
    public async Task Apply_checks_the_ruleset_before_loading_it_and_refuses_on_a_check_failure()
    {
        _answers[$"nft -c -f {NftablesFirewallManager.RulesetPath}"] = Fail("epacs.nft:7:5-9: Error: syntax error");

        var act = () => Firewall(WithApps()).ApplyRulesAsync(Firewall(WithApps()).GenerateRules());

        // The file write targets /etc/nftables.d, which this test cannot create; either the
        // refusal (on a box where it can) or the IO error (where it cannot) is a stop before load.
        await act.Should().ThrowAsync<Exception>();
        _calls.Should().NotContain(c => c.Args == $"-f {NftablesFirewallManager.RulesetPath}", "nothing is loaded after a failed check");
    }

    [Fact]
    public async Task Verify_reports_ports_absent_from_the_live_table()
    {
        _answers["nft list table inet epacs"] = Ok("table inet epacs {\n chain input {\n tcp dport 3306 iif != lo drop\n tcp dport 5010 accept\n }\n}\n");

        var result = await Firewall(WithApps()).VerifyAsync(Firewall(WithApps()).GenerateRules());

        result.Valid.Should().BeFalse();
        result.MissingRules.Should().Contain(m => m.StartsWith("l3_ERPClient")).And.Contain(m => m.StartsWith("SSH"));
        result.MissingRules.Should().NotContain(m => m.StartsWith("MySQL") || m.StartsWith("l3_FAS"));
    }

    [Fact]
    public async Task Remove_deletes_only_our_table()
    {
        await Firewall(WithApps()).RemoveAllRulesAsync();

        _calls.Should().Equal(("nft", "delete table inet epacs"));
    }

    // ── Accounts ─────────────────────────────────────────────────────────────

    [Fact]
    public void Accounts_are_distinct_in_map_order_without_root_or_LocalSystem()
    {
        var accounts = SystemdServiceAccountProvisioner.Accounts([
            Entry("db", "epacs-db"), Entry("cache", "epacs-cache"), Entry("fas", "l2r2"), Entry("loans", "l2r2"),
            Entry("agent", "LocalSystem"), Entry("root-thing", "root"), Entry("blank", "")
        ]);

        accounts.Should().Equal("epacs-db", "epacs-cache", "l2r2");
    }

    [Fact]
    public async Task Ensure_creates_only_the_missing_accounts_as_system_accounts()
    {
        _answers["getent passwd epacs-db"] = Ok("epacs-db:x:998:998::/:/usr/sbin/nologin\n");
        _answers["getent passwd l2r2"] = Fail("", 2);

        var created = await Accounts().EnsureAsync([Entry("db", "epacs-db"), Entry("fas", "l2r2")]);

        created.Should().Equal("l2r2");
        _calls.Should().Equal(
            ("getent", "passwd epacs-db"),
            ("getent", "passwd l2r2"),
            ("useradd", "--system --shell /usr/sbin/nologin --no-create-home l2r2"));
    }

    [Fact]
    public async Task Ensure_fails_loudly_when_useradd_fails()
    {
        _answers["getent passwd l2r2"] = Fail("", 2);
        _answers["useradd --system --shell /usr/sbin/nologin --no-create-home l2r2"] = Fail("useradd: cannot lock /etc/passwd", 1);

        var act = () => Accounts().EnsureAsync([Entry("fas", "l2r2")]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*l2r2*cannot lock*");
    }

    // ── The not-yet-built engines refuse by name ─────────────────────────────

    [Fact]
    public async Task Not_yet_built_engines_refuse_at_use_naming_the_task_and_never_at_construction()
    {
        var acl = new NotYetBuiltAclEngine();
        var fw = new NotYetBuiltFirewallManager();
        var accounts = new NotYetBuiltServiceAccountProvisioner();

        acl.GenerateRules().Should().BeEmpty();
        fw.GenerateRules().Should().BeEmpty();
        (await acl.Invoking(a => a.ApplyRulesAsync([])).Should().ThrowAsync<PlatformNotSupportedException>()).WithMessage("*tasks.md 25*exits 4*");
        (await fw.Invoking(f => f.ApplyRulesAsync([])).Should().ThrowAsync<PlatformNotSupportedException>()).WithMessage("*tasks.md 26*");
        (await accounts.Invoking(a => a.EnsureAsync([])).Should().ThrowAsync<PlatformNotSupportedException>()).WithMessage("*tasks.md X1*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
