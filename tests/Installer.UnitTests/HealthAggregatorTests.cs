using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Installer.Actions.Database;
using Installer.Actions.Health;
using Installer.Actions.Install;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Hosting;

namespace Installer.UnitTests;

/// <summary>
/// 13.3, against real sockets: a TCP listener, an HTTP endpoint (the same <see cref="HealthEndpoint"/>
/// the agents host), and a closed port. Three verdicts; "listening" is never called healthy.
/// </summary>
public sealed class HealthAggregatorTests
{
    private readonly Mock<IProcessRunner> _runner = new();

    private HealthAggregator Build(TimeSpan? poll = null) =>
        new(_runner.Object,
            Options.Create(new InstallerOptions { DataRoot = "/d", BinaryRoot = "/b" }),
            Options.Create(new ServicesOptions()),
            new SiteTokenSource(),
            NullLogger<HealthAggregator>.Instance,
            new HttpClient { Timeout = TimeSpan.FromSeconds(2) },
            poll ?? TimeSpan.FromMilliseconds(50));

    private static ServiceMapEntry Entry(string name, ServiceHealthCheck check) => new()
    {
        Name = name, DisplayName = name, Executable = "/x", Account = "l2r2", StartOrder = 1, StopOrder = 1,
        HealthCheck = check,
        Recovery = new ServiceRecovery
        {
            FirstFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
            SecondFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
            Subsequent = new RecoveryAction { Action = "restart", DelaySeconds = 1 }
        }
    };

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static ServiceHealthCheck Tcp(int port, int timeout = 1) =>
        new() { Type = "tcp", Host = "127.0.0.1", Port = port.ToString(CultureInfo.InvariantCulture), TimeoutSeconds = timeout };

    private static ServiceHealthCheck Http(string url, int timeout = 1, int expected = 200) =>
        new() { Type = "http", Url = url, TimeoutSeconds = timeout, ExpectedStatus = expected };

    [Fact]
    public async Task A_tcp_check_that_connects_is_Listening_not_Healthy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var report = await Build().VerifyAsync([Entry("l3_FAS", Tcp(port))], TimeSpan.FromSeconds(1));

        report.Verdicts.Should().ContainSingle().Which.State.Should().Be(HealthState.Listening);
        report.Verdicts[0].Detail.Should().Contain("LISTENING, not known healthy");
        report.Healthy.Should().Be(0);
        report.Listening.Should().Be(1);
        report.Passed.Should().BeTrue("listening passes the gate, stated as listening");
    }

    [Fact]
    public async Task An_http_check_against_the_agents_own_endpoint_is_Healthy_for_live_and_Failed_for_ready_until_flipped()
    {
        var port = FreePort();
        await using var endpoint = new HealthEndpoint(port, NullLogger.Instance, ready: false);
        endpoint.Start();

        var live = await Build().VerifyAsync([Entry("agent", Http($"http://127.0.0.1:{port}/health/live"))], TimeSpan.FromSeconds(1));
        live.Verdicts[0].State.Should().Be(HealthState.Healthy);

        var notReady = await Build().VerifyAsync([Entry("agent", Http($"http://127.0.0.1:{port}/health/ready"))], TimeSpan.FromMilliseconds(200));
        notReady.Verdicts[0].State.Should().Be(HealthState.Failed);
        notReady.Verdicts[0].Detail.Should().Contain("503");

        endpoint.Ready = true;
        var ready = await Build().VerifyAsync([Entry("agent", Http($"http://127.0.0.1:{port}/health/ready"))], TimeSpan.FromSeconds(1));
        ready.Verdicts[0].State.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public async Task A_closed_port_is_Failed_after_the_window_and_the_report_names_it()
    {
        var port = FreePort();

        var report = await Build().VerifyAsync([Entry("l3_Loans", Tcp(port)), Entry("l3_FAS", Tcp(port))], TimeSpan.FromMilliseconds(300));

        report.Failed.Should().Be(2);
        report.Passed.Should().BeFalse();
        report.Summary.Should().Contain("2 failed of 2").And.Contain("l3_Loans").And.Contain("l3_FAS");
        report.Verdicts.Should().OnlyContain(v => v.Elapsed >= TimeSpan.FromMilliseconds(250), "the window was honoured before giving up");
    }

    [Fact]
    public async Task A_service_that_comes_up_inside_the_window_passes()
    {
        var port = FreePort();
        var aggregator = Build(TimeSpan.FromMilliseconds(50));
        var verify = aggregator.VerifyAsync([Entry("slow", Tcp(port, timeout: 3))], TimeSpan.FromSeconds(3));

        await Task.Delay(300);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var report = await verify;
        report.Verdicts[0].State.Should().Be(HealthState.Listening);
        report.Verdicts[0].Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task A_command_check_uses_the_exit_code_the_map_names()
    {
        _runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, null, null, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ProcessResult { ExitCode = 0, StandardOutput = "mysqld is alive", StandardError = "" });
        var check = new ServiceHealthCheck { Type = "command", Command = "${BinaryRoot}/current/mysql/bin/mysqladmin", Arguments = "ping --port=${Services:MySql:Port}", SuccessExitCode = 0, TimeoutSeconds = 1 };

        var report = await Build().VerifyAsync([Entry("ePACSMySQL", check)], TimeSpan.FromMilliseconds(100));

        report.Verdicts[0].State.Should().Be(HealthState.Healthy);
        _runner.Verify(r => r.RunAsync("/b/current/mysql/bin/mysqladmin", "ping --port=3306", null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_unresolved_token_in_the_check_is_the_maps_fault_and_is_said_so()
    {
        var check = new ServiceHealthCheck { Type = "tcp", Host = "127.0.0.1", Port = "${Service:nope:Port}", TimeoutSeconds = 1 };

        var report = await Build().VerifyAsync([Entry("x", check)], TimeSpan.FromMilliseconds(100));

        report.Verdicts[0].State.Should().Be(HealthState.Failed);
        report.Verdicts[0].Detail.Should().Contain("${Service:nope:Port}");
    }

    [Fact]
    public async Task Checks_run_in_parallel_so_27_services_cost_one_window_not_27()
    {
        var port = FreePort();
        var entries = Enumerable.Range(0, 27).Select(i => Entry($"svc{i}", Tcp(port))).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var report = await Build().VerifyAsync(entries, TimeSpan.FromMilliseconds(400));

        report.Failed.Should().Be(27);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "27 sequential 400 ms windows would be ~11 s");
    }
}
