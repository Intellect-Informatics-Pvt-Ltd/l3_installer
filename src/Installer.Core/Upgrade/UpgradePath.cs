using SharedKernel.Contracts;

namespace Installer.Core.Upgrade;

/// <summary>
/// Decides whether a version jump is one the release supports.
///
/// Static and pure, deliberately. It is the cheapest place to stop a bad upgrade — before a
/// backup, before services stop, before anything is staged — and keeping it free of the engine's
/// dependencies means it can be tested exhaustively without a backup engine, a restore engine, a
/// fingerprinter and a service orchestrator having to exist first. Every case this lets through
/// is one that gets found out on a state's production data instead.
/// </summary>
public static class UpgradePath
{
    /// <param name="compatibility">
    /// The window the release declares it was built and tested for. Honouring it is the
    /// difference between "we have not tested that path" and "we tested it in production".
    /// </param>
    public static UpgradePathValidation Validate(
        string currentVersion, string targetVersion, CompatibilityInfo? compatibility)
    {
        if (!Version.TryParse(currentVersion, out var current))
        {
            return new UpgradePathValidation
            {
                Valid = false,
                ErrorMessage = $"The installed version '{currentVersion}' cannot be parsed, so no upgrade path can be checked."
            };
        }

        if (!Version.TryParse(targetVersion, out var target))
        {
            return new UpgradePathValidation
            {
                Valid = false,
                ErrorMessage = $"The target version '{targetVersion}' cannot be parsed."
            };
        }

        if (target == current)
        {
            return new UpgradePathValidation
            {
                Valid = false,
                ErrorMessage = $"Version {targetVersion} is already installed."
            };
        }

        if (target < current)
        {
            // Refused outright. A downgrade runs old code against a schema a newer migration has
            // already changed, and there is no migration in that direction.
            return new UpgradePathValidation
            {
                Valid = false,
                ErrorMessage =
                    $"{targetVersion} is older than the installed {currentVersion}. Downgrade is not an upgrade path: " +
                    "the schema has already moved and there is no migration back. Restore a backup instead."
            };
        }

        if (compatibility is null)
        {
            return new UpgradePathValidation { Valid = true };
        }

        var withinWindow =
            Version.TryParse(compatibility.MinUpgradeFrom, out var min) &&
            Version.TryParse(compatibility.MaxUpgradeFrom, out var max) &&
            current >= min && current <= max;

        return withinWindow
            ? new UpgradePathValidation
            {
                Valid = true,
                RequiresSideBySide = compatibility.RequiresSideBySide,
                HasBreakingSchemaChange = compatibility.BreakingSchemaChange
            }
            : new UpgradePathValidation
            {
                Valid = false,
                ErrorMessage =
                    $"This release upgrades from {compatibility.MinUpgradeFrom} to {compatibility.MaxUpgradeFrom}; " +
                    $"{currentVersion} is installed. Upgrade to a supported version first — the path from " +
                    $"{currentVersion} has not been tested and a PACS node is the wrong place to find out."
            };
    }
}
