using System.Text.Json;
using FluentAssertions;
using Installer.Core.StateMachine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// 6.6: every checkpoint is on disk before the phase runs, recovery resumes from what was
/// written, and a torn write (the power-cut case) is a clean start - never a crash, never a
/// resume from garbage.
/// </summary>
public sealed class InstallerStateMachineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-state-tests", Guid.NewGuid().ToString("N"));

    private InstallerOptions Opts => new() { DataRoot = _root, BinaryRoot = Path.Combine(_root, "bin") };

    private InstallerStateMachine New(InstallerMode mode = InstallerMode.Install, string version = "3.3.0") =>
        new(Options.Create(Opts), NullLogger<InstallerStateMachine>.Instance, mode, version);

    private string StateFile => Opts.ResolvedStateFile;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private InstallationState Read() => JsonSerializer.Deserialize<InstallationState>(File.ReadAllText(StateFile), Json)!;

    [Fact]
    public void Starts_in_Load_with_the_target_version_and_a_fresh_correlation_id()
    {
        var sm = New();

        sm.CurrentState.Phase.Should().Be(InstallerPhase.Load);
        sm.CurrentState.TargetVersion.Should().Be("3.3.0");
        sm.CurrentState.CorrelationId.Should().HaveLength(32);
        sm.IsTerminal.Should().BeFalse();
        File.Exists(StateFile).Should().BeFalse("nothing is written until the first transition");
    }

    [Fact]
    public async Task Every_transition_is_on_disk_before_it_returns()
    {
        var sm = New();

        await sm.TransitionAsync(InstallerPhase.Precheck);
        Read().Phase.Should().Be(InstallerPhase.Precheck);

        await sm.TransitionAsync(InstallerPhase.Install, "deploy", new Dictionary<string, string> { ["release"] = "3.3.0" });
        var state = Read();
        state.Phase.Should().Be(InstallerPhase.Install);
        state.SubPhase.Should().Be("deploy");
        state.Context.Should().ContainKey("release");
        state.ProcessId.Should().Be(Environment.ProcessId);
        Directory.GetFiles(Path.GetDirectoryName(StateFile)!).Should().NotContain(f => f.EndsWith(".tmp"), "write-then-rename leaves no temp file");
    }

    [Fact]
    public async Task Complete_and_Fail_are_terminal_and_Fail_records_the_error()
    {
        var sm = New();
        await sm.TransitionAsync(InstallerPhase.Install);

        await sm.FailAsync("ERP-INST-HEALTH", "l3_Loans did not come up");

        sm.IsTerminal.Should().BeTrue();
        var state = Read();
        state.Phase.Should().Be(InstallerPhase.Failed);
        state.SubPhase.Should().Be("ERP-INST-HEALTH");
        state.Context!["error"].Should().Contain("l3_Loans");

        var again = New();
        await again.TransitionAsync(InstallerPhase.Verify);
        await again.CompleteAsync();
        again.IsTerminal.Should().BeTrue();
        Read().Phase.Should().Be(InstallerPhase.Success);
    }

    [Fact]
    public async Task A_run_that_died_mid_phase_is_offered_for_recovery_and_the_checkpoint_moves_to_Recovery()
    {
        var first = New();
        await first.TransitionAsync(InstallerPhase.Install, "extract");
        // Simulate the process that wrote it being gone: a PID nobody has.
        var written = Read() with { ProcessId = 999_999 };
        File.WriteAllText(StateFile, JsonSerializer.Serialize(written, Json));

        var second = New();
        var recovered = await second.TryRecoverAsync();

        recovered.Should().NotBeNull();
        recovered!.Phase.Should().Be(InstallerPhase.Install);
        recovered.SubPhase.Should().Be("extract");
        recovered.CorrelationId.Should().Be(first.CurrentState.CorrelationId, "the resumed run keeps the original correlation id");
        second.CurrentState.Phase.Should().Be(InstallerPhase.Recovery);
        Read().Phase.Should().Be(InstallerPhase.Recovery);
    }

    [Fact]
    public async Task A_completed_or_failed_previous_run_needs_no_recovery()
    {
        var first = New();
        await first.TransitionAsync(InstallerPhase.Install);
        await first.CompleteAsync();

        (await New().TryRecoverAsync()).Should().BeNull();

        var failed = New();
        await failed.FailAsync("X", "y");
        (await New().TryRecoverAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_torn_checkpoint_is_a_clean_start_not_a_crash()
    {
        // The power-cut case: the file exists and is half a JSON document.
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, "{ \"phase\": \"Install\", \"mode\": \"Ins");

        var recovered = await New().TryRecoverAsync();

        recovered.Should().BeNull();
    }

    [Fact]
    public async Task No_state_file_is_a_clean_start()
    {
        (await New().TryRecoverAsync()).Should().BeNull();
    }

    [Fact]
    public async Task The_checkpoint_survives_a_json_round_trip_with_every_field()
    {
        var sm = New(InstallerMode.Upgrade);
        await sm.TransitionAsync(InstallerPhase.Migrate, "database", new Dictionary<string, string> { ["from"] = "3.2.0" });

        var back = Read();
        back.Mode.Should().Be(InstallerMode.Upgrade);
        back.TargetVersion.Should().Be("3.3.0");
        back.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        back.CorrelationId.Should().Be(sm.CurrentState.CorrelationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
