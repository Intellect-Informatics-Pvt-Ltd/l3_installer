using FluentAssertions;
using Installer.Actions.Install;
using Installer.Actions.Uninstall;
using Installer.Agent.Monitors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// 9.7 and 10.8: the one operation that can destroy a society's books, and the monitors that
/// watch the node between operations.
/// </summary>
public sealed class UninstallAndMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-uninstall-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IServiceOrchestrator> _services = new();
    private readonly Mock<IOverrideTokenValidator> _tokens = new();
    private readonly Mock<IFirewallManager> _firewall = new();

    private InstallerOptions Opts => new() { DataRoot = Path.Combine(_root, "data"), BinaryRoot = Path.Combine(_root, "bin") };

    private UninstallAction Uninstall() =>
        new(_services.Object, _tokens.Object, _firewall.Object, Options.Create(Opts), NullLogger<UninstallAction>.Instance);

    private static ServiceMapEntry Entry(string name) => new()
    {
        Name = name, DisplayName = name, Executable = "/x", Account = "l2r2", StartOrder = 1, StopOrder = 1,
        HealthCheck = new ServiceHealthCheck { Type = "tcp", Host = "127.0.0.1", Port = "1" },
        Recovery = new ServiceRecovery
        {
            FirstFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
            SecondFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
            Subsequent = new RecoveryAction { Action = "restart", DelaySeconds = 1 }
        }
    };

    private void GivenAnInstalledNode()
    {
        Directory.CreateDirectory(Path.Combine(Opts.BinaryRoot, "releases", "3.3.0"));
        File.WriteAllText(Path.Combine(Opts.BinaryRoot, "releases", "3.3.0", "a.dll"), "x");
        Directory.CreateDirectory(Path.Combine(Opts.DataRoot, "mysql", "data"));
        File.WriteAllText(Path.Combine(Opts.DataRoot, "mysql", "data", "ibdata1"), "the society's books");
    }

    // ── Uninstall ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Uninstall_stops_deregisters_removes_the_firewall_table_and_binaries_and_keeps_the_data()
    {
        GivenAnInstalledNode();
        var order = new List<string>();
        _services.Setup(s => s.StopAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("stop")).Returns(Task.CompletedTask);
        _services.Setup(s => s.DeregisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("deregister")).Returns(Task.CompletedTask);
        _firewall.Setup(f => f.RemoveAllRulesAsync(It.IsAny<CancellationToken>())).Callback(() => order.Add("firewall")).Returns(Task.CompletedTask);

        await Uninstall().ExecuteAsync([Entry("l3_FAS")]);

        order.Should().Equal("stop", "deregister", "firewall");
        Directory.Exists(Opts.BinaryRoot).Should().BeFalse("the release is the installer's to remove");
        File.Exists(Path.Combine(Opts.DataRoot, "mysql", "data", "ibdata1")).Should().BeTrue("the society's books are never removed by an uninstall");
        _tokens.Verify(t => t.ValidateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Purge_without_a_token_is_refused_and_the_data_survives()
    {
        GivenAnInstalledNode();

        var act = () => Uninstall().ExecuteAsync([Entry("l3_FAS")], purgeData: true, overrideToken: null, typedConfirmation: "PURGE AP-1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Override Token*");
        File.Exists(Path.Combine(Opts.DataRoot, "mysql", "data", "ibdata1")).Should().BeTrue();
    }

    [Fact]
    public async Task Purge_with_a_token_the_validator_refuses_is_refused_and_the_data_survives()
    {
        // This build's validator is DenyAll: validation is unimplemented, and a permissive stub
        // would expose an irreversible operation before its gate was written.
        GivenAnInstalledNode();
        _tokens.Setup(t => t.ValidateAsync("eyJ.token", "purge", It.IsAny<CancellationToken>()))
               .ReturnsAsync(OverrideTokenResult.Failure("Override token validation is not implemented in this build (tasks.md 9.5), so data purge is refused."));

        var act = () => Uninstall().ExecuteAsync([Entry("l3_FAS")], purgeData: true, overrideToken: "eyJ.token", typedConfirmation: "PURGE AP-1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not implemented*refused*");
        File.Exists(Path.Combine(Opts.DataRoot, "mysql", "data", "ibdata1")).Should().BeTrue();
    }

    [Fact]
    public async Task Purge_with_a_valid_token_but_the_wrong_typed_confirmation_is_refused()
    {
        GivenAnInstalledNode();
        _tokens.Setup(t => t.ValidateAsync("ok", "purge", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new OverrideTokenResult { Valid = true, PacsId = "AP-1" });

        var act = () => Uninstall().ExecuteAsync([Entry("l3_FAS")], purgeData: true, overrideToken: "ok", typedConfirmation: "PURGE AP-2");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Expected: 'PURGE AP-1'*");
        File.Exists(Path.Combine(Opts.DataRoot, "mysql", "data", "ibdata1")).Should().BeTrue();
    }

    [Fact]
    public async Task Purge_with_a_valid_token_and_the_exact_confirmation_removes_the_data()
    {
        GivenAnInstalledNode();
        _tokens.Setup(t => t.ValidateAsync("ok", "purge", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new OverrideTokenResult { Valid = true, PacsId = "AP-1" });

        await Uninstall().ExecuteAsync([Entry("l3_FAS")], purgeData: true, overrideToken: "ok", typedConfirmation: "PURGE AP-1");

        Directory.Exists(Opts.DataRoot).Should().BeFalse();
    }

    // ── Monitors ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Config_drift_survives_a_restart_and_names_the_changed_and_missing_files()
    {
        var config = Path.Combine(Opts.DataRoot, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "appsettings.Site.json"), "{ \"a\": 1 }");
        File.WriteAllText(Path.Combine(config, "garnet.conf"), "port 6379");
        var first = new ConfigDriftMonitor(Options.Create(new MonitoringOptions()), Options.Create(Opts), NullLogger<ConfigDriftMonitor>.Instance);
        await first.CaptureBaselineAsync();
        (await first.CheckAsync()).Should().BeEmpty();

        // A support engineer's hand edit, and a deleted file - then the agent restarts.
        File.WriteAllText(Path.Combine(config, "appsettings.Site.json"), "{ \"a\": 2 }");
        File.Delete(Path.Combine(config, "garnet.conf"));
        var afterRestart = new ConfigDriftMonitor(Options.Create(new MonitoringOptions()), Options.Create(Opts), NullLogger<ConfigDriftMonitor>.Instance);

        var findings = await afterRestart.CheckAsync();

        findings.Should().NotBeNull("the baseline was persisted, not lost with the process");
        findings.Should().HaveCount(2);
        findings.Should().Contain(f => f.StartsWith("changed:") && f.EndsWith("appsettings.Site.json"));
        findings.Should().Contain(f => f.StartsWith("missing:") && f.EndsWith("garnet.conf"));
        File.Exists(first.BaselinePath).Should().BeTrue();
    }

    [Fact]
    public async Task Config_drift_with_no_baseline_says_so_rather_than_passing()
    {
        var monitor = new ConfigDriftMonitor(Options.Create(new MonitoringOptions()), Options.Create(Opts), NullLogger<ConfigDriftMonitor>.Instance);
        (await monitor.CheckAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Disk_space_monitor_reads_the_real_volume_and_does_not_throw()
    {
        Directory.CreateDirectory(Opts.DataRoot);
        var monitor = new DiskSpaceMonitor(Options.Create(new MonitoringOptions()), Options.Create(Opts), NullLogger<DiskSpaceMonitor>.Instance);

        var act = () => monitor.ExecuteAsync();

        await act.Should().NotThrowAsync();
        monitor.IntervalSeconds.Should().Be(new MonitoringOptions().DiskCheckIntervalSeconds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
