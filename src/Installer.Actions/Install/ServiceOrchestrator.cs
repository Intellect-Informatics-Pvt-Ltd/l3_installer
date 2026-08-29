using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Manages Windows service lifecycle using sc.exe commands.
/// All service definitions come from service-map.yaml — no hardcoded service names or paths.
/// </summary>
public sealed class ServiceOrchestrator : IServiceOrchestrator
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ILogger<ServiceOrchestrator> _logger;

    public ServiceOrchestrator(
        IOptions<InstallerOptions> options,
        IOptions<ServicesOptions> services,
        ILogger<ServiceOrchestrator> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    public async Task RegisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        var ordered = services.OrderBy(s => s.StartOrder).ToList();
        LogEvents.RegisteringServices(_logger, ordered.Count);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RegisterServiceAsync(service, cancellationToken);
        }
    }

    public async Task StartAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        var ordered = services.OrderBy(s => s.StartOrder).ToList();
        LogEvents.StartingServices(_logger, ordered.Count);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StartServiceAsync(service, cancellationToken);
        }
    }

    public async Task StopAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        // Stop in reverse order (highest stop_order first)
        var ordered = services.OrderByDescending(s => s.StopOrder).ToList();
        LogEvents.StoppingServices(_logger, ordered.Count);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopServiceAsync(service, cancellationToken);
        }
    }

    public async Task DeregisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        LogEvents.DeregisteringServices(_logger, services.Count);

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeregisterServiceAsync(service, cancellationToken);
        }
    }

    private async Task RegisterServiceAsync(ServiceMapEntry service, CancellationToken ct)
    {
        var executablePath = ResolveTokens(service.Executable);
        var arguments = service.Arguments is not null ? ResolveTokens(service.Arguments) : "";
        var binPath = string.IsNullOrEmpty(arguments)
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {arguments}";

        LogEvents.RegisteringService(_logger, service.Name, service.Account, service.StartupType);

        var startType = service.StartupType.ToLowerInvariant() switch
        {
            "automatic" => "auto",
            "manual" => "demand",
            "disabled" => "disabled",
            _ => "auto"
        };

        // sc.exe create <name> binPath= <path> start= <type> obj= <account> DisplayName= <display>
        var result = await RunScCommandAsync(
            $"create {service.Name} binPath= \"{binPath}\" start= {startType} " +
            $"obj= \".\\{service.Account}\" DisplayName= \"{service.DisplayName}\"", ct);

        if (result.ExitCode != 0 && !result.Output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Failed to register service {Name}. Exit code: {ExitCode}. Output: {Output}.",
                service.Name, result.ExitCode, result.Output);
            throw new InvalidOperationException(
                $"Service registration failed for {service.Name}. Exit code: {result.ExitCode}");
        }

        // Set description
        if (service.Description is not null)
        {
            await RunScCommandAsync($"description {service.Name} \"{service.Description}\"", ct);
        }

        await ApplyEnvironmentAsync(service, ct);

        // Configure recovery actions
        await ConfigureRecoveryAsync(service, ct);
    }

    /// <summary>
    /// Gives a Windows service its environment variables.
    ///
    /// WHY THIS IS NOT AN sc.exe CALL. `sc.exe` has no verb for environment variables — it can
    /// set the binary path, the account, the start type and the recovery actions, and nothing
    /// else. The Service Control Manager reads a service's environment from a REG_MULTI_SZ
    /// value named `Environment` under its own registry key, so that is what this writes.
    ///
    /// WHY IT MATTERS. `ASPNETCORE_ENVIRONMENT` is how an L2-R2 service is told which state it
    /// is serving: it selects `appsettings.&lt;STATE&gt;.json`. Getting it wrong does not fail —
    /// the service starts and runs another state's configuration. Getting it MISSING does not
    /// fail either, which is why this method exists at all: before it, every service on a
    /// Windows node would have run under the compiled-in default with nothing to indicate it.
    /// </summary>
    private async Task ApplyEnvironmentAsync(ServiceMapEntry service, CancellationToken ct)
    {
        if (service.Environment.Count == 0)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Not silently skipped. On any other platform the environment cannot be applied,
            // and a service running without ASPNETCORE_ENVIRONMENT is a service serving the
            // wrong state's configuration - the one failure mode that produces no error.
            LogEvents.ServiceEnvironmentUnsupported(_logger, service.Name, service.Environment.Count);
            return;
        }

        // REG_MULTI_SZ: NAME=VALUE entries separated by \0 on the reg.exe command line.
        var entries = string.Join("\\0", service.Environment.Select(kv => $"{kv.Key}={ResolveTokens(kv.Value)}"));
        var key = $@"HKLM\SYSTEM\CurrentControlSet\Services\{service.Name}";

        var result = await RunProcessAsync("reg.exe", $"add \"{key}\" /v Environment /t REG_MULTI_SZ /d \"{entries}\" /f", ct);

        if (result.ExitCode != 0)
        {
            // Fatal. A service registered without its environment is worse than one that failed
            // to register: it starts, it looks healthy, and it serves the wrong state.
            throw new InvalidOperationException(
                $"Could not set environment variables for service {service.Name} (exit {result.ExitCode}): {result.Output}. " +
                "The service would start under the wrong state's configuration without failing, so registration is aborted.");
        }

        LogEvents.ServiceEnvironmentApplied(_logger, service.Name, service.Environment.Count);
    }

    private static async Task ConfigureRecoveryAsync(ServiceMapEntry service, CancellationToken ct)
    {
        var recovery = service.Recovery;
        var resetPeriod = recovery.ResetAfterSeconds;

        // sc.exe failure <name> reset= <seconds> actions= restart/<delay>/restart/<delay>/restart/<delay>
        var firstDelay = recovery.FirstFailure.DelaySeconds * 1000; // ms
        var secondDelay = recovery.SecondFailure.DelaySeconds * 1000;
        var subsequentDelay = recovery.Subsequent.DelaySeconds * 1000;

        await RunScCommandAsync(
            $"failure {service.Name} reset= {resetPeriod} " +
            $"actions= restart/{firstDelay}/restart/{secondDelay}/restart/{subsequentDelay}", ct);
    }

    private async Task StartServiceAsync(ServiceMapEntry service, CancellationToken ct)
    {
        LogEvents.StartingService(_logger, service.Name);

        var result = await RunScCommandAsync($"start {service.Name}", ct);

        if (result.ExitCode != 0 &&
            !result.Output.Contains("already been started", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Failed to start service {Name}. Exit code: {ExitCode}.", service.Name, result.ExitCode);
            throw new InvalidOperationException($"Service start failed for {service.Name}. Exit code: {result.ExitCode}");
        }

        // Wait for service to reach running state
        await WaitForServiceStateAsync(service.Name, "RUNNING", TimeSpan.FromSeconds(service.HealthCheck.TimeoutSeconds), ct);
        LogEvents.ServiceStarted(_logger, service.Name);
    }

    private async Task StopServiceAsync(ServiceMapEntry service, CancellationToken ct)
    {
        LogEvents.StoppingService(_logger, service.Name);

        var result = await RunScCommandAsync($"stop {service.Name}", ct);

        if (result.ExitCode != 0 &&
            !result.Output.Contains("has not been started", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("not exist", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Stop command for {Name} returned exit code {ExitCode}.", service.Name, result.ExitCode);
        }

        await WaitForServiceStateAsync(service.Name, "STOPPED", TimeSpan.FromSeconds(30), ct);
        LogEvents.ServiceStopped(_logger, service.Name);
    }

    private async Task DeregisterServiceAsync(ServiceMapEntry service, CancellationToken ct)
    {
        LogEvents.DeregisteringService(_logger, service.Name);

        // Stop first if running
        await StopServiceAsync(service, ct);

        var result = await RunScCommandAsync($"delete {service.Name}", ct);

        if (result.ExitCode != 0 &&
            !result.Output.Contains("not exist", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Failed to delete service {Name}. Exit code: {ExitCode}.", service.Name, result.ExitCode);
        }
    }

    private async Task WaitForServiceStateAsync(string serviceName, string expectedState, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var result = await RunScCommandAsync($"query {serviceName}", ct);
            if (result.Output.Contains(expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        _logger.LogWarning("Timeout waiting for service {Name} to reach state {State}.", serviceName, expectedState);
    }

    /// <summary>
    /// Substitutes via the shared vocabulary. This used to handle ${BinaryRoot} and ${DataRoot}
    /// only, which meant ePACSWeb was registered with a literal ${Services:Web:HttpsPort} as its
    /// --urls value and MySQL's health check pinged a port of the same name. See
    /// <see cref="InstallerTokenMap"/>.
    /// </summary>
    private string ResolveTokens(string input) =>
        InstallerTokenMap.Resolve(
            input,
            InstallerTokenMap.BuildInfrastructure(_options.Value, _services.Value),
            "Service map entry");

    private static Task<ScResult> RunScCommandAsync(string arguments, CancellationToken ct) =>
        RunProcessAsync("sc.exe", arguments, ct);

    /// <summary>
    /// Runs a Windows service-management tool. Generalised from the sc.exe-only helper because
    /// environment variables need reg.exe: the Service Control Manager reads them from the
    /// registry and sc.exe has no verb for them.
    /// </summary>
    private static async Task<ScResult> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new ScResult { ExitCode = -1, Output = $"Failed to start {fileName}" };
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new ScResult
        {
            ExitCode = process.ExitCode,
            Output = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}"
        };
    }

    private sealed record ScResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
    }
}
