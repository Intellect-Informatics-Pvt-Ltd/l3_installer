namespace Installer.Agent.Monitors;

/// <summary>
/// Interface for individual monitoring modules within the Installer Agent.
/// Each monitor runs on its own configurable interval.
/// </summary>
public interface IMonitor
{
    /// <summary>Name of this monitor for logging.</summary>
    string Name { get; }

    /// <summary>Interval between executions in seconds (from configuration).</summary>
    int IntervalSeconds { get; }

    /// <summary>
    /// Executes one monitoring cycle.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}
