namespace Installer.Actions.Prechecks;

/// <summary>
/// Interface for individual precheck validators.
/// Each implementation checks one specific prerequisite.
/// </summary>
public interface IPrecheck
{
    /// <summary>Unique identifier for this check.</summary>
    string CheckId { get; }

    /// <summary>Human-readable name.</summary>
    string Name { get; }

    /// <summary>Execution order (lower = earlier).</summary>
    int Order { get; }

    /// <summary>
    /// Executes the precheck validation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of this precheck.</returns>
    Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default);
}
