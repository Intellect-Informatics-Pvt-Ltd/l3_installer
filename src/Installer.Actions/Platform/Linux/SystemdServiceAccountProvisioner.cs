using Installer.Actions.Database;
using Microsoft.Extensions.Logging;
using SharedKernel.Contracts;

namespace Installer.Actions.Platform.Linux;

/// <summary>
/// <c>useradd --system --shell /usr/sbin/nologin --no-create-home</c> for each account the map
/// names and <c>getent passwd</c> cannot find — the same shape as
/// <c>ops/ansible/roles/deployapp</c> creates the estate's run user with. Root and the Windows
/// word <c>LocalSystem</c> (which means root here) are never created.
/// </summary>
public sealed class SystemdServiceAccountProvisioner : IServiceAccountProvisioner
{
    private readonly IProcessRunner _runner;
    private readonly ILogger<SystemdServiceAccountProvisioner> _logger;

    public SystemdServiceAccountProvisioner(IProcessRunner runner, ILogger<SystemdServiceAccountProvisioner> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> EnsureAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var created = new List<string>();

        foreach (var account in Accounts(services))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exists = await _runner.RunAsync("getent", $"passwd {account}", cancellationToken: cancellationToken);
            if (exists.Succeeded)
            {
                LogEvents.ServiceAccountPresent(_logger, account);
                continue;
            }

            var add = await _runner.RunAsync("useradd", $"--system --shell /usr/sbin/nologin --no-create-home {account}", cancellationToken: cancellationToken);
            if (!add.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create the service account '{account}' (useradd exited {add.ExitCode}: {add.CombinedOutput.Trim()}). " +
                    "A service registered under an account that does not exist is refused by systemd at start with a message that names the symptom, not this cause.");
            }

            created.Add(account);
            LogEvents.ServiceAccountCreated(_logger, account);
        }

        return created;
    }

    /// <summary>Distinct accounts in map order, root and LocalSystem excluded. Exposed for the contract tests.</summary>
    public static IReadOnlyList<string> Accounts(IReadOnlyList<ServiceMapEntry> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var account in services.Select(LinuxAclEngine.AccountFor))
        {
            if (account != "root" && seen.Add(account))
            {
                ordered.Add(account);
            }
        }

        return ordered;
    }
}
