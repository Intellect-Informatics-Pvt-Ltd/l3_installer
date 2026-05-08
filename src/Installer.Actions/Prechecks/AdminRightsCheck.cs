using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Installer.Actions.Prechecks;

/// <summary>
/// Validates that the installer is running with administrator privileges.
/// Required for service registration, ACL application, and firewall rules.
/// </summary>
public sealed class AdminRightsCheck : IPrecheck
{
    private readonly ILogger<AdminRightsCheck> _logger;

    public AdminRightsCheck(ILogger<AdminRightsCheck> logger)
    {
        _logger = logger;
    }

    public string CheckId => "ADMIN_RIGHTS";
    public string Name => "Administrator Privileges";
    public int Order => 5; // Run first — everything else requires admin

    public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var isAdmin = IsRunningAsAdministrator();

        if (!isAdmin)
        {
            _logger.LogError("Administrator privileges check failed. Process is not elevated.");
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Block,
                Message = "Administrator privileges are required for installation.",
                TechnicalDetail = "Process is not running with elevated privileges. UAC elevation required.",
                ErrorCode = "ERP-INST-PRE-0010"
            });
        }

        _logger.LogInformation("Administrator privileges check passed.");
        return Task.FromResult(new PrecheckResult
        {
            CheckId = CheckId,
            Name = Name,
            Severity = PrecheckSeverity.Pass,
            Message = "Running as Administrator — OK."
        });
    }

    private static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            // On non-Windows (dev/test), check if running as root
            return Environment.UserName == "root" || Environment.IsPrivilegedProcess;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
