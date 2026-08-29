namespace Installer.Actions.Database;

/// <summary>
/// Brings the bundled MySQL from nothing to a schema the ERP can run against.
///
/// This is the reason the installer framework exists. The premise of bundling the database is
/// that an offline PACS node runs no database that was not delivered and verified as part of
/// the installation — so nothing outside the installation can be tampered with, and nothing
/// depends on what an unmanaged machine happens to have on it. Everything else in this
/// repository is in service of that, and until now it was the one part with no implementation
/// at all.
/// </summary>
public interface IDatabaseBootstrapper
{
    /// <summary>
    /// Plans the bootstrap without performing it. Every check that can be made before touching
    /// the machine is made here, so a dry run tells an operator whether it would work.
    /// </summary>
    Task<DatabaseBootstrapPlan> PlanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the bootstrap: initialise the data directory, write the configuration, start
    /// the server, set credentials, create the accounts, impose the baseline schema, and count.
    /// </summary>
    /// <param name="baselineDdlPath">
    /// Path to <c>stable_baseline_ddl.sql</c> — the estate's single schema authority. Not a
    /// per-module DDL corpus and not a dump: one generated file whose diff is reviewable.
    /// </param>
    Task<DatabaseBootstrapResult> ExecuteAsync(string baselineDdlPath, CancellationToken cancellationToken = default);
}

/// <summary>What the bootstrap would do, and whether it can.</summary>
public sealed record DatabaseBootstrapPlan
{
    public required bool CanProceed { get; init; }

    /// <summary>Why not, when CanProceed is false. Written for an operator, not a developer.</summary>
    public string? Blocker { get; init; }

    public required CaseSensitivityVerdict CaseSensitivity { get; init; }
    public required string DataDirectory { get; init; }
    public required string ConfigFilePath { get; init; }
    public required bool DataDirectoryAlreadyInitialised { get; init; }

    /// <summary>Ordered description of the steps, for a dry run to print.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];
}

public sealed record DatabaseBootstrapResult
{
    public required bool Succeeded { get; init; }
    public string? Message { get; init; }

    /// <summary>Tables present before the baseline was applied. Zero on a fresh node.</summary>
    public int TablesBefore { get; init; }

    /// <summary>Tables present after. The estate's rule: rc=0 is never the verdict.</summary>
    public int TablesAfter { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = [];
}

/// <summary>
/// Raised when the bootstrap cannot continue. Always fatal — a partially initialised database
/// is the one state from which an unattended installer cannot recover safely.
/// </summary>
public sealed class DatabaseBootstrapException : Exception
{
    public DatabaseBootstrapException(string message) : base(message) { }
    public DatabaseBootstrapException(string message, Exception inner) : base(message, inner) { }
    public DatabaseBootstrapException() { }
}
