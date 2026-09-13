using SharedKernel.Contracts;

namespace Installer.Actions.Health;

/// <summary>Post-start health, per service, in three verdicts. See <see cref="HealthAggregator"/>.</summary>
public interface IHealthAggregator
{
    /// <param name="minimumWindow">Every service gets at least this long to come up, regardless of its map entry.</param>
    Task<HealthReport> VerifyAsync(IReadOnlyList<ServiceMapEntry> services, TimeSpan minimumWindow, CancellationToken cancellationToken = default);
}

public enum HealthState
{
    /// <summary>An http or command check answered as the map expects.</summary>
    Healthy,

    /// <summary>A tcp check connected. The service maps no health route, so nothing more is known (G31).</summary>
    Listening,

    /// <summary>The check did not pass within its window.</summary>
    Failed
}

public sealed record ServiceHealthVerdict(string Service, HealthState State, string Detail)
{
    public TimeSpan Elapsed { get; init; }
}

public sealed record HealthReport
{
    public required IReadOnlyList<ServiceHealthVerdict> Verdicts { get; init; }

    public int Healthy => Verdicts.Count(v => v.State == HealthState.Healthy);
    public int Listening => Verdicts.Count(v => v.State == HealthState.Listening);
    public int Failed => Verdicts.Count(v => v.State == HealthState.Failed);

    /// <summary>No service failed. Listening counts as passing — stated separately, never upgraded.</summary>
    public bool Passed => Failed == 0;

    public IEnumerable<ServiceHealthVerdict> Failures => Verdicts.Where(v => v.State == HealthState.Failed);

    public string Summary =>
        $"{Healthy} healthy, {Listening} listening (no health route - G31), {Failed} failed of {Verdicts.Count}" +
        (Failed == 0 ? "" : ": " + string.Join("; ", Failures.Select(f => $"{f.Service} ({f.Detail})")));
}
