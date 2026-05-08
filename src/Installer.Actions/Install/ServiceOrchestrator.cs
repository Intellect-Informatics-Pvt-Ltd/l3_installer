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
    private readonly ILogger<ServiceOrchestrator> _logger;

    public ServiceOrchestrator(IOptions<InstallerOptions> options, ILogger<ServiceOrchestrator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task RegisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        var ordered = services.OrderBy(s => s.StartOrder).ToList();
        _logger.LogInformation("Registering {Count} Windows services.", ordered.Count);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RegisterServiceAsync(service, cancellationToken);
        }
    }

    public async Task StartAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        var ordered = services.OrderBy(s => s.StartOrder).ToList();
        _logger.LogInformation("Starting {Count} services in dependency order.", ordered.Count);

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
        _logger.LogInformation("Stopping {Count} services in reverse dependency order.", ordered.Count);

        foreach (var service in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopServiceAsync(service, cancellationToken);
        }
    }

    public async Task DeregisterAllAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deregistering {Count} Windows services.", services.Count);

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

        _logger.LogInformation("Registering service {Name} (account: {Account}, startup: {Startup}).",
            service.Name, service.Account, service.StartupType);

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

        // Configure recovery actions
        await ConfigureRecoveryAsync(service, ct);
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
        _logger.LogInformation("Starting service {Name}...", service.Name);

        var result = await RunScCommandAsync($"start {service.Name}", ct);

        if (result.ExitCode != 0 &&
            !result.Output.Contains("already been started", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Failed to start service {Name}. Exit code: {ExitCode}.", service.Name, result.ExitCode);
            throw new InvalidOperationException($"Service start failed for {service.Name}. Exit code: {result.ExitCode}");
        }

        // Wait for service to reach running state
        await WaitForServiceStateAsync(service.Name, "RUNNING", TimeSpan.FromSeconds(service.HealthCheck.TimeoutSeconds), ct);
        _logger.LogInformation("Service {Name} started successfully.", service.Name);
    }

    private async Task StopServiceAsync(ServiceMapEntry service, CancellationToken ct)
    {
        _logger.LogInformation("Stopping service {Name}...", service.Name);

        var result = await RunScCommandAsync($"stop {service.Name}", ct);

        if (result.ExitCode != 0 &&
            !result.Output.Contains("has not been started", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("not exist", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Stop command for {Name} returned exit code {ExitCode}.", service.Name, result.ExitCode);
        }

        await WaitForServiceStateAsync(service.Name, "STOPPED", TimeSpan.FromSeconds(30), ct);
        _logger.LogInformation("Service {Name} stopped.", service.Name);
    }

    private async Task DeregisterServiceAsync(ServiceMapEntry service, CancellationToken ct)
    {
        _logger.LogInformation("Deregistering service {Name}.", service.Name);

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

    private string ResolveTokens(string input)
    {
        var result = input;
        result = result.Replace("${BinaryRoot}", _options.Value.BinaryRoot, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("${DataRoot}", _options.Value.DataRoot, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static async Task<ScResult> RunScCommandAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new ScResult { ExitCode = -1, Output = "Failed to start sc.exe" };
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
