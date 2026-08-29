namespace Installer.Actions.Database;

/// <summary>
/// Runs an external executable and captures its result.
///
/// An interface so the bootstrap sequence — which is where the irreversible steps live — can be
/// tested without a MySQL server. Everything that decides WHAT to run is pure and covered;
/// this is the thin seam that actually runs it.
/// </summary>
public interface IProcessRunner
{
    /// <param name="executable">Full path to the binary.</param>
    /// <param name="arguments">Arguments, already quoted.</param>
    /// <param name="workingDirectory">Working directory, or null for the current one.</param>
    /// <param name="stdin">Text piped to standard input — how SQL reaches the mysql client without touching disk.</param>
    /// <param name="secrets">
    /// Values that must never appear in a log or a support bundle. The runner redacts them from
    /// captured output before returning. Passwords reach the child through the environment or
    /// stdin, never through the command line, where any user on the box can read them from the
    /// process table.
    /// </param>
    /// <param name="environment">Extra environment variables for the child process.</param>
    Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        IReadOnlyCollection<string>? secrets = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public bool Succeeded => ExitCode == 0;

    /// <summary>Both streams, for an error message. Already redacted by the runner.</summary>
    public string CombinedOutput =>
        string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
