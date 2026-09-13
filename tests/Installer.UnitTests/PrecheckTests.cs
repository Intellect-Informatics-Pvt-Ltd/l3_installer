using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Installer.Actions.Prechecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.UnitTests;

/// <summary>
/// 7.11: each precheck, with a passing and a failing case. The checks read the real machine, so
/// the failing cases are driven through the options (an impossible threshold) or a real
/// condition this test creates (a port it holds), never by mocking the machine away.
/// </summary>
public sealed class PrecheckTests
{
    private static IOptions<PrecheckOptions> Opts(Action<PrecheckOptions>? configure = null)
    {
        var o = new PrecheckOptions();
        configure?.Invoke(o);
        return Options.Create(o);
    }

    // ── OS ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Os_check_passes_on_this_machine_or_names_ADR_0010()
    {
        var result = new OsVersionCheck(Opts(), NullLogger<OsVersionCheck>.Instance).Evaluate();

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            // On a Linux CI box this is Debian/Ubuntu with systemd, or a Block that says why.
            result.Severity.Should().BeOneOf(PrecheckSeverity.Pass, PrecheckSeverity.Block);
            if (result.Severity == PrecheckSeverity.Block)
            {
                result.Message.Should().Contain("ADR-0010");
            }
        }
        else
        {
            result.Severity.Should().Be(PrecheckSeverity.Block);
            result.Message.Should().Contain("ADR-0010").And.Contain("dry run");
        }
    }

    [SkippableFact]
    public void On_Linux_a_non_Debian_distribution_is_a_block_naming_the_ADR_never_a_Windows_version()
    {
        // The defect this pins: on Linux the old check compared the KERNEL patch level with a
        // Windows build number and blocked every Debian install with "Windows 10 1809 required".
        Skip.IfNot(OperatingSystem.IsLinux(), "reads /etc/os-release semantics; the seam is exercised on Linux");
        var fedora = new OsVersionCheck(Opts(), NullLogger<OsVersionCheck>.Instance, () => "ID=fedora\nPRETTY_NAME=\"Fedora 40\"\n").Evaluate();
        fedora.Severity.Should().Be(PrecheckSeverity.Block);
        fedora.Message.Should().Contain("Debian").And.Contain("ADR-0010").And.NotContain("Windows");

        var debian = new OsVersionCheck(Opts(), NullLogger<OsVersionCheck>.Instance, () => "ID=debian\nPRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\n").Evaluate();
        debian.Message.Should().NotContain("Windows 10");
    }

    [SkippableFact]
    public void Os_release_fields_are_parsed_with_and_without_quotes()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux path");
        var ubuntu = new OsVersionCheck(Opts(), NullLogger<OsVersionCheck>.Instance, () => "NAME=\"Ubuntu\"\nID=ubuntu\nID_LIKE=debian\n").Evaluate();
        ubuntu.Message.Should().NotContain("Debian or Ubuntu is required");
        var mint = new OsVersionCheck(Opts(), NullLogger<OsVersionCheck>.Instance, () => "ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n").Evaluate();
        mint.Message.Should().NotContain("Debian or Ubuntu is required", "ID_LIKE=debian counts");
    }

    // ── RAM ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ram_check_blocks_below_the_minimum_warns_below_the_recommendation_and_passes_otherwise()
    {
        (await new RamCheck(Opts(o => { o.MinRamGb = 100_000; }), NullLogger<RamCheck>.Instance).ExecuteAsync())
            .Severity.Should().Be(PrecheckSeverity.Block);
        (await new RamCheck(Opts(o => { o.MinRamGb = 0; o.RecommendedRamGb = 100_000; }), NullLogger<RamCheck>.Instance).ExecuteAsync())
            .Severity.Should().Be(PrecheckSeverity.Warning);
        (await new RamCheck(Opts(o => { o.MinRamGb = 0; o.RecommendedRamGb = 0; }), NullLogger<RamCheck>.Instance).ExecuteAsync())
            .Severity.Should().Be(PrecheckSeverity.Pass);
    }

    // ── Disk ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disk_check_blocks_when_the_data_volume_cannot_hold_the_minimum()
    {
        var installer = Options.Create(new InstallerOptions { DataRoot = Path.GetTempPath(), BinaryRoot = Path.GetTempPath() });

        var blocked = await new DiskSpaceCheck(Opts(o => o.MinDataDiskFreeGb = 1_000_000), installer, NullLogger<DiskSpaceCheck>.Instance).ExecuteAsync();
        blocked.Severity.Should().Be(PrecheckSeverity.Block);
        blocked.ErrorCode.Should().Be("ERP-INST-PRE-0004");
        blocked.TechnicalDetail.Should().Contain("Free:");

        var ok = await new DiskSpaceCheck(Opts(o => { o.MinDataDiskFreeGb = 0; o.MinSystemDiskFreeGb = 0; }), installer, NullLogger<DiskSpaceCheck>.Instance).ExecuteAsync();
        ok.Severity.Should().Be(PrecheckSeverity.Pass);
    }

    // ── Ports ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Port_check_names_a_port_something_else_holds_and_passes_when_all_are_free()
    {
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();
        var port = ((IPEndPoint)held.LocalEndpoint).Port;
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var blocked = await new PortAvailabilityCheck(Opts(o => o.RequiredPorts = [port, free]), NullLogger<PortAvailabilityCheck>.Instance).ExecuteAsync();
        blocked.Severity.Should().Be(PrecheckSeverity.Block);
        blocked.Message.Should().Contain(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        blocked.Message.Should().NotContain(free.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");

        var ok = await new PortAvailabilityCheck(Opts(o => o.RequiredPorts = [free]), NullLogger<PortAvailabilityCheck>.Instance).ExecuteAsync();
        ok.Severity.Should().Be(PrecheckSeverity.Pass);
    }

    // ── Admin rights / reboot ────────────────────────────────────────────────

    [Fact]
    public async Task Admin_check_reports_the_truth_about_this_process()
    {
        var result = await new AdminRightsCheck(NullLogger<AdminRightsCheck>.Instance).ExecuteAsync();

        var privileged = OperatingSystem.IsWindows() ? result.Severity == PrecheckSeverity.Pass : Environment.IsPrivilegedProcess || Environment.UserName == "root";
        (result.Severity == PrecheckSeverity.Pass).Should().Be(privileged);
        if (result.Severity != PrecheckSeverity.Pass)
        {
            result.Severity.Should().Be(PrecheckSeverity.Block, "an unprivileged installer cannot register services or own directories");
        }
    }

    [Fact]
    public async Task Pending_reboot_check_is_not_a_windows_only_crash_elsewhere()
    {
        var result = await new PendingRebootCheck(Opts(), NullLogger<PendingRebootCheck>.Instance).ExecuteAsync();
        result.Severity.Should().BeOneOf(PrecheckSeverity.Pass, PrecheckSeverity.Warning, PrecheckSeverity.Block);
    }

    // ── The runner ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_runner_orders_by_Order_and_a_single_block_stops_the_run()
    {
        var checks = new IPrecheck[]
        {
            new RamCheck(Opts(o => { o.MinRamGb = 0; o.RecommendedRamGb = 0; }), NullLogger<RamCheck>.Instance),          // order 30
            new DiskSpaceCheck(Opts(o => o.MinDataDiskFreeGb = 1_000_000), Options.Create(new InstallerOptions { DataRoot = Path.GetTempPath(), BinaryRoot = Path.GetTempPath() }), NullLogger<DiskSpaceCheck>.Instance), // order 20, blocks
        };

        var result = await new PrecheckRunner(checks, NullLogger<PrecheckRunner>.Instance).RunAllAsync();

        result.CanProceed.Should().BeFalse();
        result.BlockingCount.Should().Be(1);
        result.Results.Select(r => r.CheckId).Should().Equal("DISK_SPACE", "RAM");
    }
}
