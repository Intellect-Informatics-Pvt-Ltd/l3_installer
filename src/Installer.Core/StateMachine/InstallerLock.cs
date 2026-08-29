using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Core.StateMachine;

/// <summary>
/// Prevents two installer processes from running against the same machine at once.
///
/// WHAT THIS REPLACED, AND WHY, because the previous implementation is instructive.
/// It used <c>new Mutex(initiallyOwned: false, "Global\\ePACSInstaller", out var createdNew)</c>
/// and treated <c>createdNew == true</c> as "lock acquired". It is not: that constructor
/// creates the mutex without owning it, so the code never waited and never held anything.
/// <b>The concurrent-execution guard did not guard</b> — two installers started together would
/// both have proceeded to register services — and because nothing was owned,
/// <c>ReleaseMutex()</c> then threw <c>ApplicationException</c> on every single run. The
/// exception was caught and logged as a warning, which is how a guard that never worked stayed
/// invisible: the happy path produced a warning nobody read.
///
/// A second problem would have bitten even with ownership fixed. <see cref="Mutex"/> has thread
/// affinity — it must be released by the thread that took it — and this lock is held across the
/// whole async pipeline, so the release lands on whatever thread-pool thread the last
/// continuation ran on. That throws too.
///
/// WHAT IT IS NOW: an exclusive lock file opened with <see cref="FileShare.None"/>.
///   * No thread affinity — a file handle belongs to the process, not the thread.
///   * Cross-platform — the same code guards on Windows, Linux and macOS, so the guard is
///     exercised by CI on every OS rather than only on the target.
///   * Diagnosable while held — the holder's PID goes in a readable sidecar file, because
///     FileShare.None (the only mode that excludes on Unix) makes the lock file itself
///     unreadable. See the note in TryOpen.
///   * Crash-safe — the operating system closes the handle when the process dies, so a power
///     cut mid-install does not leave a machine that refuses to install ever again. That
///     matters more here than anywhere: the recovery path is the normal path at a PACS site.
///   * Diagnosable — the holder's PID and start time are written into the file, so "another
///     installer is running" can name it instead of asserting it.
/// </summary>
public sealed class InstallerLock : IDisposable
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<InstallerLock> _logger;
    private FileStream? _handle;

    public InstallerLock(IOptions<InstallerOptions> options, ILogger<InstallerLock> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Path of the lock file. Exposed so diagnostics and tests can name it.</summary>
    public string LockFilePath => Path.Combine(_options.Value.DataRoot, "installer", "installer.lock");

    /// <summary>Description of the current holder, when acquisition failed. Null otherwise.</summary>
    public string? HolderDescription { get; private set; }

    /// <summary>
    /// Attempts to take the lock.
    /// </summary>
    /// <param name="timeoutMs">
    /// How long to keep retrying. Default 0 — fail immediately. An installer should tell the
    /// operator that another one is running, not sit silently waiting for it.
    /// </param>
    public bool TryAcquire(int timeoutMs = 0)
    {
        if (_handle is not null)
        {
            return true;
        }

        var path = LockFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var attempted = false;

        while (!attempted || DateTime.UtcNow < deadline)
        {
            attempted = true;

            if (TryOpen(path))
            {
                HolderDescription = null;
                LogEvents.InstallerLockAcquired(_logger, path);
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(250);
        }

        HolderDescription = ReadHolder(path);
        LogEvents.InstallerLockUnavailable(_logger, path, HolderDescription ?? "unknown");
        return false;
    }

    private bool TryOpen(string path)
    {
        try
        {
            // TWO FILES, and the split is not tidiness — it is the only shape that is both
            // exclusive and diagnosable on every platform this has to run on.
            //
            // FileShare.None is the exclusion, and it is the ONLY share mode that excludes on
            // Unix: .NET maps FileShare.None to an exclusive flock(LOCK_EX) and every other
            // value to a shared flock(LOCK_SH), so two writers holding with FileShare.Read both
            // succeed on Linux and macOS while correctly excluding each other on Windows. A
            // guard that holds on the target OS and silently fails on the CI OS is worse than
            // no guard: it is a guard nobody can test.
            //
            // FileShare.None also makes the file unreadable while held, so the holder's
            // identity goes in a plain sidecar next to it. At a rural site with one operator
            // and no remote access, "pid=4312, started 14:02" is the difference between a
            // decision and a reboot.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _handle = stream;

            WriteOwnerFile(path);
            return true;
        }
        catch (IOException)
        {
            return false; // held by another process — the expected contention path
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string OwnerFilePath(string lockPath) => lockPath + ".owner";

    /// <summary>
    /// Records who holds the lock, in a file the refused installer can actually read. Failure
    /// here is logged and ignored: not knowing the holder's PID must never stop the lock from
    /// being held.
    /// </summary>
    private static void WriteOwnerFile(string lockPath)
    {
        var holder = string.Create(CultureInfo.InvariantCulture,
            $"pid={Environment.ProcessId} machine={Environment.MachineName} startedUtc={DateTimeOffset.UtcNow:O}");
        try
        {
            File.WriteAllText(OwnerFilePath(lockPath), holder);
        }
#pragma warning disable CA1031 // diagnostics only; must not affect whether the lock is held
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Reads who holds the lock. Best-effort by design: a failure to identify the holder must
    /// never turn into a failure to report the contention, so every error here yields null and
    /// the caller still refuses to run.
    /// </summary>
    private static string? ReadHolder(string lockPath)
    {
        try
        {
            using var stream = new FileStream(OwnerFilePath(lockPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
#pragma warning disable CA1031 // diagnostics only; never allowed to change the outcome
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Releases the lock. Safe to call more than once, and safe to call from any thread — which
    /// the Mutex implementation this replaced was not.
    /// </summary>
    public void Release()
    {
        if (_handle is null)
        {
            return;
        }

        _handle.Dispose();
        _handle = null;

        try
        {
            File.Delete(LockFilePath);
            File.Delete(OwnerFilePath(LockFilePath));
        }
        catch (IOException)
        {
            // Leaving the file behind is harmless: the lock is the open handle, not the file's
            // existence, so the next run opens it with FileMode.Create and proceeds.
        }
        catch (UnauthorizedAccessException)
        {
        }

        LogEvents.InstallerLockReleased(_logger, LockFilePath);
    }

    public void Dispose() => Release();
}
