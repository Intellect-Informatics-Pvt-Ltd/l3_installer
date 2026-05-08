using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Prechecks;

/// <summary>
/// Detects if a Windows reboot is pending (from Windows Update, driver install, etc.).
/// Configurable: can be set to block or warn only.
/// </summary>
public sealed class PendingRebootCheck : IPrecheck
{
    private readonly IOptions<PrecheckOptions> _options;
    private readonly ILogger<PendingRebootCheck> _logger;

    // Registry keys that indicate a pending reboot
    private static readonly string[] RebootIndicatorKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        @"SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations"
    ];

    public PendingRebootCheck(IOptions<PrecheckOptions> options, ILogger<PendingRebootCheck> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string CheckId => "PENDING_REBOOT";
    public string Name => "Pending Reboot";
    public int Order => 50;

    public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var rebootPending = IsRebootPending();

        if (!rebootPending)
        {
            _logger.LogInformation("No pending reboot detected.");
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Pass,
                Message = "No pending reboot — OK."
            });
        }

        var severity = _options.Value.BlockOnPendingReboot
            ? PrecheckSeverity.Block
            : PrecheckSeverity.Warning;

        _logger.LogWarning("Pending Windows reboot detected. Severity: {Severity}.", severity);
        return Task.FromResult(new PrecheckResult
        {
            CheckId = CheckId,
            Name = Name,
            Severity = severity,
            Message = "A Windows reboot is pending. Recommended to reboot before installing.",
            TechnicalDetail = "Pending reboot detected via registry indicators.",
            ErrorCode = "ERP-INST-PRE-0011"
        });
    }

    private static bool IsRebootPending()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false; // Not applicable on non-Windows
        }

        foreach (var keyPath in RebootIndicatorKeys)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
                if (key is not null)
                {
                    return true;
                }
            }
            catch
            {
                // Ignore access errors — treat as no reboot pending
            }
        }

        return false;
    }
}
