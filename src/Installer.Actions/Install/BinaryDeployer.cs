using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Install;

/// <summary>
/// Deploys binaries using the side-by-side release pattern.
/// Layout: C:\Program Files\ePACS\releases\<version>\ with 'current' as a directory junction.
/// </summary>
public sealed class BinaryDeployer : IBinaryDeployer
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<BinaryDeployer> _logger;

    public BinaryDeployer(IOptions<InstallerOptions> options, ILogger<BinaryDeployer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task DeployAsync(string stagingDirectory, string version, CancellationToken cancellationToken = default)
    {
        var releasesPath = _options.Value.ReleasesPath;
        var versionPath = Path.Combine(releasesPath, version);

        LogEvents.DeployingVersion(_logger, version, versionPath);

        // Ensure releases directory exists
        if (!Directory.Exists(releasesPath))
        {
            Directory.CreateDirectory(releasesPath);
        }

        // If version directory already exists (retry after power-cut), remove it
        if (Directory.Exists(versionPath))
        {
            _logger.LogWarning("Version directory already exists (possible retry). Removing: {Path}.", versionPath);
            Directory.Delete(versionPath, recursive: true);
        }

        // Copy staging to release directory
        await Task.Run(() => CopyDirectory(stagingDirectory, versionPath), cancellationToken);

        LogEvents.BinariesDeployed(_logger, versionPath);

        // Create/update the 'current' junction
        await SwitchCurrentAsync(version);
    }

    /// <summary>
    /// Places a release beside the others WITHOUT pointing <c>current</c> at it.
    ///
    /// The staging half of a side-by-side upgrade. Separated from <see cref="DeployAsync"/>
    /// because an upgrade must be able to put the new release on disk, stop services, migrate,
    /// and only then commit — and until it commits, the old release has to remain whole and
    /// startable. A deploy that switched as it copied would leave no version to fall back to
    /// between those steps.
    /// </summary>
    public async Task StageAsync(string stagingDirectory, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var versionPath = Path.Combine(_options.Value.ReleasesPath, version);
        Directory.CreateDirectory(_options.Value.ReleasesPath);

        if (Directory.Exists(versionPath))
        {
            // A retry after a power cut. Removing and re-copying is safe precisely because
            // `current` does not point here yet.
            Directory.Delete(versionPath, recursive: true);
        }

        await Task.Run(() => CopyDirectory(stagingDirectory, versionPath), cancellationToken);
        LogEvents.BinariesDeployed(_logger, versionPath);
    }

    /// <summary>
    /// Points <c>current</c> at a release. The commit step of an upgrade, and the rollback step
    /// of a failed one.
    ///
    /// ── WHY THIS IS NOT THREE LINES ─────────────────────────────────────────────────────────
    ///
    /// It used to be: delete the link, create the link. A power cut between those two leaves the
    /// node with <b>no <c>current</c> at all</b> — every service path is
    /// <c>{BinaryRoot}\current\...</c>, so nothing starts, and the installer's own recovery
    /// cannot find the release it was part-way through installing. On a machine whose defining
    /// characteristic is that it loses power, a two-step commit is the wrong shape.
    ///
    /// Two mechanisms, in order:
    ///
    /// 1. <b>Atomic replace.</b> Create the new link under a temporary name, then move it over
    ///    the old one. On POSIX <c>File.Move(overwrite: true)</c> is <c>rename(2)</c>, which
    ///    replaces a symlink atomically — the link points at the old release or the new one and
    ///    never at nothing. Verified by experiment before being relied on.
    ///
    /// 2. <b>Recorded intent.</b> Windows will not rename over an existing directory reparse
    ///    point, so there the fallback is still delete-then-create. Before either path runs, a
    ///    marker naming the intended target is written and flushed to the platter, and removed
    ///    only once the link is correct. <see cref="TryCompleteInterruptedSwitchAsync"/> finishes
    ///    the job on the next run — so the window is recoverable even where it is unavoidable.
    /// </summary>
    public async Task SwitchCurrentAsync(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var junctionPath = _options.Value.CurrentJunctionPath;
        var targetPath = Path.Combine(_options.Value.ReleasesPath, version);

        if (!Directory.Exists(targetPath))
        {
            throw new DirectoryNotFoundException(
                $"Refusing to point 'current' at {targetPath}: it does not exist. Deploy the release first.");
        }

        LogEvents.SwitchingJunction(_logger, version);

        // The marker goes down FIRST and is flushed, so a cut anywhere after this point leaves
        // evidence of what was intended.
        await WriteSwitchIntentAsync(targetPath);

        SwitchTo(junctionPath, targetPath);

        ClearSwitchIntent();
        LogEvents.JunctionSwitched(_logger, junctionPath, targetPath);
    }

    /// <summary>
    /// Completes a switch that a power cut interrupted, if one was in flight. Called during
    /// recovery, before anything else looks at <c>current</c>. Returns the version it completed,
    /// or null when there was nothing to do.
    /// </summary>
    public async Task<string?> TryCompleteInterruptedSwitchAsync(CancellationToken cancellationToken = default)
    {
        var markerPath = SwitchIntentPath;
        if (!File.Exists(markerPath))
        {
            return null;
        }

        var target = (await File.ReadAllTextAsync(markerPath, cancellationToken)).Trim();
        var junctionPath = _options.Value.CurrentJunctionPath;

        if (!Directory.Exists(target))
        {
            // The intended release is gone — a cut during extraction, or somebody cleaned up.
            // Leave `current` alone: whatever it points at is at least a complete release, and
            // pointing it at nothing would be strictly worse.
            LogEvents.SwitchIntentAbandoned(_logger, target);
            ClearSwitchIntent();
            return null;
        }

        var currentTarget = ResolveCurrent(junctionPath);
        if (string.Equals(currentTarget, target, StringComparison.Ordinal))
        {
            // The switch had completed; only the marker's removal had not.
            ClearSwitchIntent();
            return null;
        }

        LogEvents.CompletingInterruptedSwitch(_logger, currentTarget ?? "(none)", target);
        SwitchTo(junctionPath, target);
        ClearSwitchIntent();

        return Path.GetFileName(target);
    }

    /// <summary>Where <c>current</c> points, or null if it is absent.</summary>
    public string? ResolveCurrent() => ResolveCurrent(_options.Value.CurrentJunctionPath);

    private static string? ResolveCurrent(string junctionPath)
    {
        try
        {
            return Directory.Exists(junctionPath)
                ? Directory.ResolveLinkTarget(junctionPath, returnFinalTarget: true)?.FullName
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void SwitchTo(string junctionPath, string targetPath)
    {
        var staging = junctionPath + ".new";

        if (Directory.Exists(staging) || File.Exists(staging))
        {
            Directory.Delete(staging);
        }

        Directory.CreateSymbolicLink(staging, targetPath);

        try
        {
            // POSIX rename(2): atomic.
            File.Move(staging, junctionPath, overwrite: true);
        }
        catch (IOException)
        {
            // Windows will not rename over an existing directory reparse point. The intent
            // marker the caller wrote is what makes this fallback recoverable.
            if (Directory.Exists(junctionPath))
            {
                // recursive: false — removes the LINK, never what it points at.
                Directory.Delete(junctionPath, recursive: false);
            }

            Directory.Move(staging, junctionPath);
        }
    }

    private string SwitchIntentPath =>
        Path.Combine(_options.Value.DataRoot, "installer", "current.pending");

    private async Task WriteSwitchIntentAsync(string targetPath)
    {
        var path = SwitchIntentPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            await writer.WriteAsync(targetPath);
            await writer.FlushAsync();
        }

        // To the platter, not just to the OS buffer. A marker still in a write-back cache when
        // the power goes is a marker that was never written, and surviving exactly that is its
        // entire purpose.
        stream.Flush(flushToDisk: true);
    }

    private void ClearSwitchIntent()
    {
        try
        {
            File.Delete(SwitchIntentPath);
        }
        catch (IOException)
        {
            // A marker outliving its switch is harmless: the next recovery sees the link already
            // points where it says, and clears it.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(destination, relativePath);
            var destDir = Path.GetDirectoryName(destFile);

            if (destDir is not null && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(file, destFile, overwrite: true);
        }
    }
}
