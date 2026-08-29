using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Installer.Core.StateMachine;
using SharedKernel.Configuration;

namespace Installer.UnitTests;

/// <summary>
/// The concurrency guard. These tests exist because the implementation this replaced did not
/// work at all: it treated "I created the named mutex" as "I own the named mutex", so two
/// installers would both have proceeded — and the only symptom was a warning on release that
/// appeared on every successful run and was therefore ignored.
///
/// The lesson worth keeping: a guard with no test is indistinguishable from no guard.
/// </summary>
public sealed class InstallerLockTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "epacs-lock-tests", Guid.NewGuid().ToString("N"));

    private InstallerLock NewLock() => new(
        Options.Create(new InstallerOptions { DataRoot = _dataRoot }),
        NullLogger<InstallerLock>.Instance);

    [Fact]
    public void Acquires_on_a_clean_machine()
    {
        using var sut = NewLock();

        sut.TryAcquire().Should().BeTrue();
        File.Exists(sut.LockFilePath).Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_second_holder_while_the_first_holds_it()
    {
        using var first = NewLock();
        using var second = NewLock();

        first.TryAcquire().Should().BeTrue();

        second.TryAcquire().Should().BeFalse("a second installer must never run against the same machine");
        second.HolderDescription.Should().NotBeNullOrEmpty("the operator needs to be told WHICH process holds it");
        second.HolderDescription.Should().Contain($"pid={Environment.ProcessId}");
    }

    [Fact]
    public void Releases_so_the_next_run_can_proceed()
    {
        using var first = NewLock();
        using var second = NewLock();

        first.TryAcquire().Should().BeTrue();
        first.Release();

        second.TryAcquire().Should().BeTrue("a released lock must not wedge the machine");
    }

    [Fact]
    public void Release_is_idempotent()
    {
        using var sut = NewLock();
        sut.TryAcquire().Should().BeTrue();

        var act = () => { sut.Release(); sut.Release(); sut.Release(); };

        act.Should().NotThrow("the pipeline releases in a finally block that can run after an earlier release");
    }

    [Fact]
    public void Release_from_another_thread_does_not_throw()
    {
        // The regression this pins. Mutex has thread affinity: the previous implementation held
        // the lock across the whole async pipeline and released it on whatever thread-pool
        // thread the last continuation happened to run on, which throws every time.
        using var sut = NewLock();
        sut.TryAcquire().Should().BeTrue();

        var act = () => Task.Run(() => sut.Release()).GetAwaiter().GetResult();

        act.Should().NotThrow();
    }

    [Fact]
    public void Reacquiring_while_already_held_by_this_instance_is_a_no_op()
    {
        using var sut = NewLock();

        sut.TryAcquire().Should().BeTrue();
        sut.TryAcquire().Should().BeTrue("re-entering must not deadlock the process against itself");
    }

    [Fact]
    public void Dispose_releases()
    {
        var first = NewLock();
        first.TryAcquire().Should().BeTrue();
        first.Dispose();

        using var second = NewLock();
        second.TryAcquire().Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }
}
