using Installer.Actions.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// The Debian/systemd <see cref="IServiceOrchestrator"/> — ADR-0010.
///
/// Writes one unit per service into <c>/etc/systemd/system</c>, then drives them with
/// <c>systemctl</c>. The unit content comes from <see cref="SystemdUnitWriter"/>, which is pure
/// and tested; this class is the part that touches the machine.
///
/// ── WHERE IT DIFFERS FROM THE WINDOWS ORCHESTRATOR, AND WHY ─────────────────────────────────
///
/// <b>Environment variables are not a separate step.</b> On Windows they need a REG_MULTI_SZ
/// write because <c>sc.exe</c> has no verb for them; in a unit file they are three lines in the
/// <c>[Service]</c> section. The Windows path can fail after the service is registered, which is
/// why it aborts registration; here the unit is written atomically or not at all, so the failure
/// mode does not exist.
///
/// <b>Ordering is enforced by us, not by systemd.</b> The units carry
/// <c>After=network-online.target</c> and nothing else, deliberately. systemd's <c>After=</c>
/// orders process *start*, not readiness — it cannot know when an HTTP service is answering. The
/// estate's own Ansible says exactly this and gates tiers with a health check instead. Encoding
/// a dependency graph here would look like ordering while providing none.
///
/// <b>Removal deletes the unit file.</b> `systemctl disable` alone leaves a masked-looking unit
/// behind that confuses the next install.
/// </summary>
public sealed class SystemdServiceOrchestrator : IServiceOrchestrator
{
    /// <summary>
    /// Where units are written. <c>/etc/systemd/system</c> rather than <c>/usr/lib/systemd/system</c>:
    /// these are locally-generated units for a locally-installed product, not distribution files,
    /// and /etc is what a package manager will not overwrite.
    /// </summary>
    private const string UnitDirectory = "/etc/systemd/system";

    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ServicesOptions> _services;
    private readonly IProcessRunner _runner;
    private readonly ILogger<SystemdServiceOrchestrator> _logger;

    public SystemdServiceOrchestrator(
        IOptions<InstallerOptions> options,
        IOptions<ServicesOptions> services,
        IProcessRunner runner,
        ILogger<SystemdServiceOrchestrator> logger)
    {
        _options = options;
        _services = services;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// The unprivileged account services run as, matching the estate's own <c>l2r2_run_user</c>.
    /// Taken from the service map's <c>account</c> where one is given; <c>LocalSystem</c> is a
    /// Windows word and is translated rather than passed through, because a unit with
    /// <c>User=LocalSystem</c> fails at start with an error naming a user nobody will recognise.
    /// </summary>
    private static string RunUserFor(ServiceMapEntry service) =>
        string.IsNullOrWhiteSpace(service.Account) || service.Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            ? "root"
            : service.Account;

    public async Task RegisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var ordered = services.OrderBy(s => s.StartOrder).ToList();
        LogEvents.RegisteringServices(_logger, ordered.Count);

        Directory.CreateDirectory(UnitDirectory);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runUser = RunUserFor(service);
            var unit = SystemdUnitWriter.Render(service, runUser, ResolveTokens);
            var path = Path.Combine(UnitDirectory, SystemdUnitWriter.UnitFileName(service));

            // Write-then-rename. systemd may be reading the directory, and a half-written unit
            // parses as far as the truncation and then silently omits the rest — including,
            // plausibly, the Environment lines that tell the service which state it serves.
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, unit, cancellationToken);
            File.Move(temp, path, overwrite: true);

            LogEvents.RegisteringService(_logger, service.Name, runUser, service.StartupType);
        }

        await RunOrThrowAsync("systemctl", "daemon-reload", cancellationToken);

        foreach (var service in ordered.Where(s => !s.StartupType.Equals("Disabled", StringComparison.OrdinalIgnoreCase)))
        {
            await RunOrThrowAsync("systemctl", $"enable {SystemdUnitWriter.UnitName(service)}", cancellationToken);
        }
    }

    public async Task StartAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var ordered = services.OrderBy(s => s.StartOrder).ToList();
        LogEvents.StartingServices(_logger, ordered.Count);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogEvents.StartingService(_logger, service.Name);
            await RunOrThrowAsync("systemctl", $"start {SystemdUnitWriter.UnitName(service)}", cancellationToken);
            LogEvents.ServiceStarted(_logger, service.Name);
        }
    }

    public async Task StopAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var ordered = services.OrderByDescending(s => s.StopOrder).ToList();
        LogEvents.StoppingServices(_logger, ordered.Count);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogEvents.StoppingService(_logger, service.Name);

            // Stop is best-effort by design: a service that is already down, or was never
            // registered, must not prevent the rest of an uninstall from proceeding. A half-
            // stopped estate is worse than a fully stopped one.
            await RunAsync("systemctl", $"stop {SystemdUnitWriter.UnitName(service)}", cancellationToken);
            LogEvents.ServiceStopped(_logger, service.Name);
        }
    }

    public async Task DeregisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        LogEvents.DeregisteringServices(_logger, services.Count);

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogEvents.DeregisteringService(_logger, service.Name);

            await RunAsync("systemctl", $"disable {SystemdUnitWriter.UnitName(service)}", cancellationToken);

            // Delete the file too. `disable` only removes the WantedBy symlink; the unit stays
            // visible to systemctl, and the next install's daemon-reload picks up a stale
            // definition that no longer matches the service map.
            var path = Path.Combine(UnitDirectory, SystemdUnitWriter.UnitFileName(service));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        await RunAsync("systemctl", "daemon-reload", cancellationToken);
    }

    private string ResolveTokens(string input) =>
        InstallerTokenMap
            .Resolve(input, InstallerTokenMap.BuildInfrastructure(_options.Value, _services.Value), "Service map entry")
            // The service map is authored with Windows separators because that was the first
            // target. A unit file with backslashes in ExecStart fails at start with a path
            // nobody can read.
            .Replace('\\', '/');

    private Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct) =>
        _runner.RunAsync(exe, args, cancellationToken: ct);

    private async Task RunOrThrowAsync(string exe, string args, CancellationToken ct)
    {
        var result = await RunAsync(exe, args, ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"`{exe} {args}` failed with exit {result.ExitCode}. {result.CombinedOutput}");
        }
    }
}
