using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Core.StateMachine;

/// <summary>
/// Detects the appropriate installer mode based on existing installation state.
/// </summary>
public sealed class ModeDetector
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<ModeDetector> _logger;

    public ModeDetector(IOptions<InstallerOptions> options, ILogger<ModeDetector> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Detects the installer mode based on existing installation.
    /// </summary>
    /// <param name="requestedMode">Mode explicitly requested by the operator (null = auto-detect).</param>
    /// <returns>The detected or validated installer mode.</returns>
    public InstallerMode Detect(InstallerMode? requestedMode = null)
    {
        var installationExists = IsExistingInstallation();

        if (requestedMode.HasValue)
        {
            ValidateRequestedMode(requestedMode.Value, installationExists);
            return requestedMode.Value;
        }

        // Auto-detect
        if (!installationExists)
        {
            _logger.LogInformation("No existing installation detected. Mode: Install.");
            return InstallerMode.Install;
        }

        _logger.LogInformation("Existing installation detected at {Path}. Mode: Upgrade.",
            _options.Value.CurrentJunctionPath);
        return InstallerMode.Upgrade;
    }

    /// <summary>
    /// Gets the currently installed version, if any.
    /// </summary>
    /// <returns>The installed version string, or null if not installed.</returns>
    public string? GetInstalledVersion()
    {
        var junctionPath = _options.Value.CurrentJunctionPath;

        if (!Directory.Exists(junctionPath))
        {
            return null;
        }

        // The junction target is releases\<version>\ — extract version from path
        try
        {
            var target = Directory.ResolveLinkTarget(junctionPath, returnFinalTarget: true);
            if (target is null)
            {
                return null;
            }

            // Extract version from path like "C:\Program Files\ePACS\releases\3.2.1"
            var versionDir = Path.GetFileName(target.FullName);
            return versionDir;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve junction target at {Path}", junctionPath);
            return null;
        }
    }

    private bool IsExistingInstallation()
    {
        var binaryRoot = _options.Value.BinaryRoot;
        var currentJunction = _options.Value.CurrentJunctionPath;

        return Directory.Exists(binaryRoot) && Directory.Exists(currentJunction);
    }

    private void ValidateRequestedMode(InstallerMode mode, bool installationExists)
    {
        switch (mode)
        {
            case InstallerMode.Install when installationExists:
                throw new InvalidOperationException(
                    "Cannot perform fresh install — ePACS is already installed. Use Upgrade or Repair mode.");

            case InstallerMode.Upgrade when !installationExists:
                throw new InvalidOperationException(
                    "Cannot upgrade — no existing ePACS installation found. Use Install mode.");

            case InstallerMode.Repair when !installationExists:
                throw new InvalidOperationException(
                    "Cannot repair — no existing ePACS installation found. Use Install mode.");

            case InstallerMode.Uninstall when !installationExists:
                throw new InvalidOperationException(
                    "Cannot uninstall — no existing ePACS installation found.");
        }

        _logger.LogInformation("Requested mode {Mode} validated against installation state.", mode);
    }
}
