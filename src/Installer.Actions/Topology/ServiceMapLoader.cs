using Microsoft.Extensions.Logging;
using SharedKernel.Contracts;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Installer.Actions.Topology;

/// <summary>
/// YamlDotNet-backed <see cref="IServiceMapLoader"/>.
///
/// WHY THIS EXISTS. Until 2026-08-29 the only way to turn a service map into
/// <see cref="ServiceMapEntry"/> records was <c>HarnessServiceMapLoader</c>, a hand-rolled
/// line-based parser written for the harness map. It recognises <c>http</c> health checks only,
/// and silently drops <c>command</c> and <c>tcp</c> checks and <c>data_directories</c> — so the
/// framework's own canonical map (<c>samples/service-map.yaml</c>, whose MySQL check is
/// <c>command</c> and whose cache and eventing checks are <c>tcp</c>) could not be loaded at all.
/// The chassis could not read its own topology file.
///
/// YamlDotNet is already a dependency of ManifestVerifier, so this adds a version, not a
/// dependency.
/// </summary>
public sealed class ServiceMapLoader : IServiceMapLoader
{
    private readonly ILogger<ServiceMapLoader> _logger;
    private readonly IDeserializer _deserializer;

    public ServiceMapLoader(ILogger<ServiceMapLoader> logger)
    {
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            // Forward compatibility: a map may carry keys this version does not model
            // (`dependencies` is the current example — the orchestrator derives ordering from
            // start_order, so it is documentation today). Unknown keys must not be fatal.
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task<IReadOnlyList<ServiceMapEntry>> LoadAsync(
        string serviceMapPath,
        IReadOnlyCollection<string>? groups = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceMapPath);

        // A missing map is fatal, not a warning. The alternative — returning an empty list —
        // makes "install completed, zero services registered" a success path.
        if (!File.Exists(serviceMapPath))
        {
            throw new ServiceMapException($"Service map not found: {serviceMapPath}");
        }

        var content = await File.ReadAllTextAsync(serviceMapPath, cancellationToken);
        var entries = Parse(content, groups);

        LogEvents.ServiceMapLoaded(_logger, entries.Count, serviceMapPath);

        return entries;
    }

    public IReadOnlyList<ServiceMapEntry> Parse(string yamlContent, IReadOnlyCollection<string>? groups = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlContent);

        ServiceMapDocument? document;
        try
        {
            document = _deserializer.Deserialize<ServiceMapDocument>(yamlContent);
        }
        catch (YamlException ex)
        {
            throw new ServiceMapException(
                $"Service map is not valid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
        }

        if (document?.Services is null || document.Services.Count == 0)
        {
            throw new ServiceMapException("Service map contains no 'services' entries.");
        }

        var wanted = groups is { Count: > 0 }
            ? new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase)
            : null;

        var result = new List<ServiceMapEntry>(document.Services.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in document.Services)
        {
            if (string.IsNullOrWhiteSpace(raw.Name))
            {
                throw new ServiceMapException("A service entry has no 'name'.");
            }

            // Duplicate names would mean the second sc.exe create silently no-ops on
            // "already exists", leaving one service registered against the wrong binary.
            if (!seen.Add(raw.Name))
            {
                throw new ServiceMapException($"Duplicate service name in map: '{raw.Name}'.");
            }

            // An ungrouped service belongs to every group: a map with no groups is one group.
            if (wanted is not null && raw.Group is not null && !wanted.Contains(raw.Group))
            {
                continue;
            }

            result.Add(ToEntry(raw));
        }

        if (result.Count == 0)
        {
            throw new ServiceMapException(
                $"Service map contains {document.Services.Count} services but none matched groups [{string.Join(", ", groups ?? [])}].");
        }

        return result.OrderBy(s => s.StartOrder).ToList();
    }

    private static ServiceMapEntry ToEntry(RawService raw)
    {
        if (string.IsNullOrWhiteSpace(raw.Executable))
        {
            throw new ServiceMapException($"Service '{raw.Name}' has no 'executable'.");
        }

        if (raw.HealthCheck is null)
        {
            throw new ServiceMapException(
                $"Service '{raw.Name}' has no 'health_check'. Every service must declare how the installer knows it is up — a service the framework cannot probe cannot gate the tier behind it.");
        }

        var healthCheck = ToHealthCheck(raw.Name!, raw.HealthCheck);

        return new ServiceMapEntry
        {
            Name = raw.Name!,
            DisplayName = string.IsNullOrWhiteSpace(raw.DisplayName) ? raw.Name! : raw.DisplayName,
            Description = raw.Description,
            Executable = raw.Executable!,
            Arguments = raw.Arguments,
            Account = string.IsNullOrWhiteSpace(raw.Account) ? "LocalSystem" : raw.Account,
            StartOrder = raw.StartOrder,
            StopOrder = raw.StopOrder,
            StartupType = string.IsNullOrWhiteSpace(raw.StartupType) ? "Automatic" : raw.StartupType,
            HealthCheck = healthCheck,
            Recovery = ToRecovery(raw.Recovery),
            DataDirectories = raw.DataDirectories?.ToArray() ?? []
        };
    }

    private static ServiceHealthCheck ToHealthCheck(string serviceName, RawHealthCheck raw)
    {
        var type = (raw.Type ?? "").ToLowerInvariant();

        // Validate per type rather than accepting a half-specified check. A check missing its
        // target does not fail — it passes vacuously, which is the worst outcome for a gate.
        switch (type)
        {
            case "command" when string.IsNullOrWhiteSpace(raw.Command):
                throw new ServiceMapException($"Service '{serviceName}': health_check type 'command' requires 'command'.");
            case "tcp" when string.IsNullOrWhiteSpace(raw.Port):
                throw new ServiceMapException($"Service '{serviceName}': health_check type 'tcp' requires 'port'.");
            case "http" when string.IsNullOrWhiteSpace(raw.Url):
                throw new ServiceMapException($"Service '{serviceName}': health_check type 'http' requires 'url'.");
            case "command":
            case "tcp":
            case "http":
                break;
            default:
                throw new ServiceMapException(
                    $"Service '{serviceName}': unknown health_check type '{raw.Type}'. Expected 'command', 'tcp' or 'http'.");
        }

        return new ServiceHealthCheck
        {
            Type = type,
            Command = raw.Command,
            Arguments = raw.Arguments,
            Host = raw.Host,
            Port = raw.Port,
            Url = raw.Url,
            TimeoutSeconds = raw.TimeoutSeconds ?? 10,
            SuccessExitCode = raw.SuccessExitCode ?? 0,
            ExpectedStatus = raw.ExpectedStatus ?? 200
        };
    }

    private static ServiceRecovery ToRecovery(RawRecovery? raw)
    {
        // Absent recovery is not an error: sc.exe's own default (take no action) is a valid
        // choice for a service whose restart would be harmful. Model it explicitly as "none"
        // so the orchestrator never has to reason about null.
        static RecoveryAction Action(RawRecoveryAction? a, int defaultDelay) => new()
        {
            Action = a?.Action ?? "none",
            DelaySeconds = a?.DelaySeconds ?? defaultDelay
        };

        return new ServiceRecovery
        {
            FirstFailure = Action(raw?.FirstFailure, 30),
            SecondFailure = Action(raw?.SecondFailure, 60),
            Subsequent = Action(raw?.Subsequent, 120),
            ResetAfterSeconds = raw?.ResetAfterSeconds ?? 86400
        };
    }

    // ── YAML shape ───────────────────────────────────────────────────────────
    // Mutable, nullable DTOs: YamlDotNet needs settable properties, and every field must be
    // nullable so "absent" and "explicitly zero" stay distinguishable during validation.

    private sealed class ServiceMapDocument
    {
        public List<RawService>? Services { get; set; }
    }

    private sealed class RawService
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Executable { get; set; }
        public string? Arguments { get; set; }
        public string? Account { get; set; }
        public int StartOrder { get; set; }
        public int StopOrder { get; set; }
        public string? StartupType { get; set; }
        public string? Group { get; set; }
        public RawHealthCheck? HealthCheck { get; set; }
        public RawRecovery? Recovery { get; set; }
        public List<string>? DataDirectories { get; set; }
    }

    private sealed class RawHealthCheck
    {
        public string? Type { get; set; }
        public string? Command { get; set; }
        public string? Arguments { get; set; }
        public string? Host { get; set; }
        public string? Port { get; set; }
        public string? Url { get; set; }
        public int? TimeoutSeconds { get; set; }
        public int? SuccessExitCode { get; set; }
        public int? ExpectedStatus { get; set; }
    }

    private sealed class RawRecovery
    {
        public RawRecoveryAction? FirstFailure { get; set; }
        public RawRecoveryAction? SecondFailure { get; set; }
        public RawRecoveryAction? Subsequent { get; set; }
        public int? ResetAfterSeconds { get; set; }
    }

    private sealed class RawRecoveryAction
    {
        public string? Action { get; set; }
        public int DelaySeconds { get; set; }
    }
}
