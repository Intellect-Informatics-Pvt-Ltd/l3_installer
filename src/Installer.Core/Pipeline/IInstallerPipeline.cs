using SharedKernel.Contracts;

namespace Installer.Core.Pipeline;

/// <summary>
/// Executes an installer operation end to end, driving the state machine through its phases
/// and checkpointing after each so a power cut resumes rather than restarts.
///
/// This is the composition target: everything else in this repository is a component that does
/// one step. Before this existed, nothing assembled them and no code path had ever run the
/// product from start to finish.
/// </summary>
public interface IInstallerPipeline
{
    Task<PipelineResult> RunAsync(PipelineRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What the caller asked for. Built by the CLI from arguments plus the loaded .epcfg.</summary>
public sealed record PipelineRequest
{
    /// <summary>Explicit mode, or null to auto-detect from what is already installed.</summary>
    public InstallerMode? Mode { get; init; }

    /// <summary>The loaded site configuration pack. Required for every mode except Uninstall.</summary>
    public SiteConfigPack? SiteConfig { get; init; }

    /// <summary>Directory holding the release manifest and payload archives (the delivery media).</summary>
    public string? MediaDirectory { get; init; }

    /// <summary>Purge business data during uninstall. Requires an override token and typed confirmation.</summary>
    public bool PurgeData { get; init; }

    /// <summary>The backup to restore from. Required for Restore; the installer will not guess.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Repair: regenerate configuration even when it looks intact. Discards hand edits.</summary>
    public bool RegenerateConfiguration { get; init; }

    /// <summary>Repair: re-lay binaries even when they look intact. For a suspected quarantine.</summary>
    public bool ReplaceBinaries { get; init; }

    public string? OverrideToken { get; init; }
    public string? TypedConfirmation { get; init; }

    /// <summary>
    /// Run every phase that does not change the machine, and stop before the first that does.
    /// Mirrors the estate's own <c>ops/l2r2</c> contract: dry-run by default, --apply to act.
    /// </summary>
    public bool DryRun { get; init; }
}

/// <summary>The outcome, in the terms the CLI turns into an exit code.</summary>
public sealed record PipelineResult
{
    public required PipelineOutcome Outcome { get; init; }
    public required InstallerMode Mode { get; init; }
    public InstallerPhase ReachedPhase { get; init; }
    public string? Message { get; init; }

    /// <summary>Human-readable lines describing what happened, in order. Never contains secrets.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    public static PipelineResult Success(InstallerMode mode, InstallerPhase phase, IReadOnlyList<string> steps, string? message = null) =>
        new() { Outcome = PipelineOutcome.Success, Mode = mode, ReachedPhase = phase, Steps = steps, Message = message };

    public static PipelineResult Failed(PipelineOutcome outcome, InstallerMode mode, InstallerPhase phase, string message, IReadOnlyList<string>? steps = null) =>
        new() { Outcome = outcome, Mode = mode, ReachedPhase = phase, Message = message, Steps = steps ?? [] };
}

/// <summary>
/// Outcomes, mapped one-to-one onto CLI exit codes.
///
/// <see cref="NotImplemented"/> exists because the alternative was worse. Before this pipeline,
/// <c>/mode:upgrade</c> returned 0 having done nothing at all — on a PACS node that reads as
/// "upgrade succeeded", and the next thing anyone does is decommission the old media. A mode
/// whose engine does not exist must fail loudly and distinguishably.
/// </summary>
public enum PipelineOutcome
{
    Success = 0,
    PrecheckFailed = 1,
    OperationFailed = 2,
    HealthFailed = 3,
    NotImplemented = 4,
    Refused = 5
}
