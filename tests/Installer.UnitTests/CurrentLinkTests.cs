using FluentAssertions;
using Installer.Actions.Install;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.UnitTests;

/// <summary>
/// The <c>current</c> link — the commit step of an upgrade.
///
/// It used to be delete-then-create, and a power cut between those two left the node with no
/// <c>current</c> at all: every service path is <c>{BinaryRoot}\current\...</c>, so nothing
/// starts, and recovery cannot find the release it was part-way through installing. On a machine
/// whose defining characteristic is losing power, that was the wrong shape.
/// </summary>
public sealed class CurrentLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-link-tests", Guid.NewGuid().ToString("N"));

    private InstallerOptions Options => new()
    {
        DataRoot = Path.Combine(_root, "data"),
        BinaryRoot = Path.Combine(_root, "bin")
    };

    private BinaryDeployer Deployer => new(Options.ToOptions(), NullLogger<BinaryDeployer>.Instance);

    private string Release(string version)
    {
        var path = Path.Combine(Options.ReleasesPath, version);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "marker.txt"), version);
        return path;
    }

    private string IntentMarker => Path.Combine(Options.DataRoot, "installer", "current.pending");

    [Fact]
    public async Task Points_current_at_a_release()
    {
        Release("3.2.0");

        await Deployer.SwitchCurrentAsync("3.2.0");

        Deployer.ResolveCurrent().Should().EndWith("3.2.0");
        File.ReadAllText(Path.Combine(Options.CurrentJunctionPath, "marker.txt")).Should().Be("3.2.0");
    }

    [Fact]
    public async Task Switching_between_releases_replaces_the_link_without_touching_either_release()
    {
        Release("3.2.0");
        Release("3.3.0");

        await Deployer.SwitchCurrentAsync("3.2.0");
        await Deployer.SwitchCurrentAsync("3.3.0");

        Deployer.ResolveCurrent().Should().EndWith("3.3.0");
        // recursive:false on the delete path — the link goes, never what it points at.
        Directory.Exists(Path.Combine(Options.ReleasesPath, "3.2.0")).Should().BeTrue();
        File.Exists(Path.Combine(Options.ReleasesPath, "3.2.0", "marker.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_to_point_current_at_a_release_that_is_not_there()
    {
        var act = () => Deployer.SwitchCurrentAsync("9.9.9");

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        Deployer.ResolveCurrent().Should().BeNull();
    }

    [Fact]
    public async Task The_intent_marker_is_cleared_on_success()
    {
        Release("3.2.0");

        await Deployer.SwitchCurrentAsync("3.2.0");

        File.Exists(IntentMarker).Should().BeFalse("a marker outliving its switch invites a pointless recovery");
    }

    // ── Power-cut recovery ───────────────────────────────────────────────────

    [Fact]
    public async Task An_interrupted_switch_is_completed_on_the_next_run()
    {
        // THE REGRESSION. Simulates a cut in the window between removing the old link and
        // creating the new one: the marker is on disk, `current` is gone, and the node cannot
        // start. Recovery must finish the job rather than leave a node with no binaries.
        Release("3.2.0");
        var target = Release("3.3.0");

        await Deployer.SwitchCurrentAsync("3.2.0");

        Directory.Delete(Options.CurrentJunctionPath, recursive: false);
        Directory.CreateDirectory(Path.GetDirectoryName(IntentMarker)!);
        await File.WriteAllTextAsync(IntentMarker, target);

        var completed = await Deployer.TryCompleteInterruptedSwitchAsync();

        completed.Should().Be("3.3.0");
        Deployer.ResolveCurrent().Should().EndWith("3.3.0");
        File.Exists(IntentMarker).Should().BeFalse();
    }

    [Fact]
    public async Task A_marker_whose_target_has_vanished_leaves_current_alone()
    {
        // A cut during extraction, or somebody cleaned up. Pointing `current` at nothing would
        // be strictly worse than leaving it on a complete older release.
        Release("3.2.0");
        await Deployer.SwitchCurrentAsync("3.2.0");

        Directory.CreateDirectory(Path.GetDirectoryName(IntentMarker)!);
        await File.WriteAllTextAsync(IntentMarker, Path.Combine(Options.ReleasesPath, "never-extracted"));

        var completed = await Deployer.TryCompleteInterruptedSwitchAsync();

        completed.Should().BeNull();
        Deployer.ResolveCurrent().Should().EndWith("3.2.0", "a complete older release beats no release at all");
        File.Exists(IntentMarker).Should().BeFalse();
    }

    [Fact]
    public async Task A_marker_left_behind_after_a_completed_switch_is_just_cleared()
    {
        // The cut landed between the switch and the marker's removal. Nothing to redo.
        var target = Release("3.3.0");
        await Deployer.SwitchCurrentAsync("3.3.0");
        await File.WriteAllTextAsync(IntentMarker, target);

        var completed = await Deployer.TryCompleteInterruptedSwitchAsync();

        completed.Should().BeNull();
        Deployer.ResolveCurrent().Should().EndWith("3.3.0");
        File.Exists(IntentMarker).Should().BeFalse();
    }

    [Fact]
    public async Task Recovery_is_a_no_op_when_no_switch_was_in_flight()
    {
        Release("3.2.0");
        await Deployer.SwitchCurrentAsync("3.2.0");

        (await Deployer.TryCompleteInterruptedSwitchAsync()).Should().BeNull();
        Deployer.ResolveCurrent().Should().EndWith("3.2.0");
    }

    [Fact]
    public async Task A_leftover_staging_link_from_an_earlier_cut_does_not_block_the_next_switch()
    {
        Release("3.2.0");
        var target = Release("3.3.0");
        Directory.CreateDirectory(Options.BinaryRoot);
        Directory.CreateSymbolicLink(Options.CurrentJunctionPath + ".new", target);

        await Deployer.SwitchCurrentAsync("3.2.0");

        Deployer.ResolveCurrent().Should().EndWith("3.2.0");
    }

    // ── Staging, the other half of a side-by-side upgrade ────────────────────

    [Fact]
    public async Task Staging_places_a_release_without_switching_to_it()
    {
        // The old release must stay whole and startable until the commit; a deploy that switched
        // as it copied would leave nothing to fall back to in between.
        Release("3.2.0");
        await Deployer.SwitchCurrentAsync("3.2.0");

        var incoming = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(incoming);
        await File.WriteAllTextAsync(Path.Combine(incoming, "marker.txt"), "3.3.0");

        await Deployer.StageAsync(incoming, "3.3.0");

        Directory.Exists(Path.Combine(Options.ReleasesPath, "3.3.0")).Should().BeTrue();
        Deployer.ResolveCurrent().Should().EndWith("3.2.0", "staging must not commit");
    }

    [Fact]
    public async Task Re_staging_after_an_interrupted_attempt_replaces_the_partial_copy()
    {
        var incoming = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(incoming);
        await File.WriteAllTextAsync(Path.Combine(incoming, "marker.txt"), "3.3.0");

        Directory.CreateDirectory(Path.Combine(Options.ReleasesPath, "3.3.0"));
        await File.WriteAllTextAsync(Path.Combine(Options.ReleasesPath, "3.3.0", "half-written.txt"), "junk");

        await Deployer.StageAsync(incoming, "3.3.0");

        File.Exists(Path.Combine(Options.ReleasesPath, "3.3.0", "half-written.txt")).Should().BeFalse();
        File.Exists(Path.Combine(Options.ReleasesPath, "3.3.0", "marker.txt")).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

internal static class OptionsExtensions
{
    public static IOptions<T> ToOptions<T>(this T value) where T : class => Options.Create(value);
}
