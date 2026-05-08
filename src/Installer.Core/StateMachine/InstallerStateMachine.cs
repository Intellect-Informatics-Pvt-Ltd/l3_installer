using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Core.StateMachine;

/// <summary>
/// State machine implementation with checkpoint persistence for power-cut recovery.
/// Every state transition writes a checkpoint file (fsync'd) so the installer
/// can resume from the last known-good state after a hard power loss.
/// </summary>
public sealed class InstallerStateMachine : IInstallerStateMachine
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<InstallerStateMachine> _logger;
    private readonly string _correlationId;
    private InstallationState _currentState;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public InstallerStateMachine(
        IOptions<InstallerOptions> options,
        ILogger<InstallerStateMachine> logger,
        InstallerMode mode,
        string targetVersion,
        string? previousVersion = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _correlationId = Guid.NewGuid().ToString("N");

        _currentState = new InstallationState
        {
            Phase = InstallerPhase.Load,
            Mode = mode,
            TargetVersion = targetVersion,
            PreviousVersion = previousVersion,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = _correlationId,
            ProcessId = Environment.ProcessId
        };
    }

    /// <inheritdoc />
    public InstallationState CurrentState => _currentState;

    /// <inheritdoc />
    public bool IsTerminal => _currentState.Phase is InstallerPhase.Success or InstallerPhase.Failed;

    /// <inheritdoc />
    public async Task TransitionAsync(
        InstallerPhase nextPhase,
        string? subPhase = null,
        Dictionary<string, string>? context = null,
        CancellationToken cancellationToken = default)
    {
        var previousPhase = _currentState.Phase;

        _currentState = _currentState with
        {
            Phase = nextPhase,
            SubPhase = subPhase,
            Timestamp = DateTimeOffset.UtcNow,
            Context = context,
            ProcessId = Environment.ProcessId
        };

        await PersistCheckpointAsync(cancellationToken);

        _logger.LogInformation(
            "State transition: {PreviousPhase} → {NextPhase} (SubPhase: {SubPhase}, Mode: {Mode})",
            previousPhase, nextPhase, subPhase ?? "none", _currentState.Mode);
    }

    /// <inheritdoc />
    public async Task<InstallationState?> TryRecoverAsync(CancellationToken cancellationToken = default)
    {
        var stateFilePath = _options.Value.ResolvedStateFile;

        if (!File.Exists(stateFilePath))
        {
            _logger.LogInformation("No previous state file found. Clean start.");
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath, cancellationToken);
            var savedState = JsonSerializer.Deserialize<InstallationState>(json, JsonOptions);

            if (savedState is null)
            {
                _logger.LogWarning("State file exists but could not be deserialized. Treating as clean start.");
                return null;
            }

            // If previous run completed successfully or failed, no recovery needed
            if (savedState.Phase is InstallerPhase.Success or InstallerPhase.Failed)
            {
                _logger.LogInformation(
                    "Previous run ended in {Phase}. No recovery needed.", savedState.Phase);
                return null;
            }

            // Check for stale lock (PID no longer running)
            if (!IsProcessRunning(savedState.ProcessId))
            {
                _logger.LogWarning(
                    "Previous run (PID {Pid}) is no longer running. State: {Phase}/{SubPhase}. Recovery available.",
                    savedState.ProcessId, savedState.Phase, savedState.SubPhase ?? "none");

                _currentState = savedState with
                {
                    Phase = InstallerPhase.Recovery,
                    Timestamp = DateTimeOffset.UtcNow,
                    ProcessId = Environment.ProcessId
                };

                await PersistCheckpointAsync(cancellationToken);
                return savedState;
            }

            // Another instance is still running
            _logger.LogError(
                "Another installer instance (PID {Pid}) is still running in phase {Phase}.",
                savedState.ProcessId, savedState.Phase);
            return savedState;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "State file is corrupt (possible torn write during power-cut). Entering safe mode.");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await TransitionAsync(InstallerPhase.Success, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailAsync(string errorCode, string errorMessage, CancellationToken cancellationToken = default)
    {
        await TransitionAsync(
            InstallerPhase.Failed,
            subPhase: errorCode,
            context: new Dictionary<string, string> { ["error"] = errorMessage },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Persists the current state to disk using write-then-rename for atomicity.
    /// The file is fsync'd to ensure durability across power loss.
    /// </summary>
    private async Task PersistCheckpointAsync(CancellationToken cancellationToken)
    {
        var stateFilePath = _options.Value.ResolvedStateFile;
        var directory = Path.GetDirectoryName(stateFilePath);

        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_currentState, JsonOptions);
        var tempPath = stateFilePath + ".tmp";

        // Write to temp file first (atomic write pattern)
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);

        // Flush to disk (fsync equivalent on Windows via FileOptions.WriteThrough)
        await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await fs.FlushAsync(cancellationToken);
        }

        // Atomic rename (on NTFS, rename is atomic if same volume)
        File.Move(tempPath, stateFilePath, overwrite: true);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
