using SharedKernel.Contracts;

namespace Installer.Core.Repair;

/// <summary>
/// Restores an installation to what the release declares, without touching data.
///
/// The boundary is the point: repair owns binaries, generated configuration, service
/// registrations and the <c>current</c> link. The site owns the database, attachments, logs and
/// backups, and repair never touches them. That is what makes it the one operation an operator
/// can run without a backup and without a decision.
/// </summary>
public interface IRepairEngine
{
    Task<RepairResult> RepairAsync(RepairRequest request, CancellationToken cancellationToken = default);
}

public sealed record RepairRequest
{
    /// <summary>The medium carrying the installed release. Verified before anything is re-laid.</summary>
    public required string MediaDirectory { get; init; }

    /// <summary>Needed when configuration is regenerated; the pack supplies the site's identity.</summary>
    public SiteConfigPack? SiteConfig { get; init; }

    /// <summary>Regenerate configuration even when it looks intact — discards hand edits.</summary>
    public bool RegenerateConfiguration { get; init; }

    /// <summary>Re-lay binaries even when they look intact. For a suspected quarantine or corruption.</summary>
    public bool ReplaceBinaries { get; init; }

    public bool DryRun { get; init; } = true;
}

public sealed record RepairResult
{
    public required bool Success { get; init; }
    public required string Version { get; init; }

    /// <summary>What was found wrong, diagnosed before anything was changed.</summary>
    public required IReadOnlyList<RepairFinding> Findings { get; init; }

    /// <summary>What was actually done. Empty on a dry run.</summary>
    public required IReadOnlyList<string> Repaired { get; init; }

    public string? Message { get; init; }
}

public sealed record RepairFinding(RepairArea Area, RepairSeverity Severity, string Message);

public enum RepairArea
{
    Binaries,
    Configuration,
    Services,
    CurrentLink
}

public enum RepairSeverity
{
    /// <summary>Found broken, and repair will fix it.</summary>
    Broken,

    /// <summary>Not broken; the operator asked for it to be re-laid anyway.</summary>
    Requested
}

public sealed class RepairException : Exception
{
    public RepairException(string message) : base(message) { }
    public RepairException(string message, Exception inner) : base(message, inner) { }
    public RepairException() { }
}
