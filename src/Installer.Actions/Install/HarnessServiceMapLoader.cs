using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SharedKernel.Contracts;
using YamlEntry = System.Collections.Generic.Dictionary<string, object>;

namespace Installer.Actions.Install;

/// <summary>
/// Loads the harness service-map.yaml and filters services by group.
/// PACS-side services (group=pacs) are always included.
/// NLDR-side services (group=nldr) are included only in demo mode.
///
/// The YAML is parsed manually (no YamlDotNet dependency) using a simple
/// line-based parser that handles the flat service-map structure.
/// For production, consider replacing with YamlDotNet if the format grows.
/// </summary>
public sealed partial class HarnessServiceMapLoader : IHarnessServiceMapLoader
{
    private readonly ILogger<HarnessServiceMapLoader> _logger;

    public HarnessServiceMapLoader(ILogger<HarnessServiceMapLoader> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<ServiceMapEntry>> LoadAsync(
        string serviceMapPath,
        bool includeNldr = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(serviceMapPath))
        {
            _logger.LogWarning("Harness service map not found at {Path}. No harness services will be registered.", serviceMapPath);
            return [];
        }

        var content = await File.ReadAllTextAsync(serviceMapPath, cancellationToken);
        var allServices = ParseServiceMap(content);

        var filtered = allServices
            .Where(s => s.Group == "pacs" || (includeNldr && s.Group == "nldr"))
            .OrderBy(s => s.StartOrder)
            .Select(s => (ServiceMapEntry)s)
            .ToList();

        _logger.LogInformation(
            "Loaded {Total} harness services from {Path}. Filtered to {Count} (includeNldr={IncludeNldr}).",
            allServices.Count, serviceMapPath, filtered.Count, includeNldr);

        return filtered;
    }

    private static List<HarnessServiceEntry> ParseServiceMap(string yaml)
    {
        var services = new List<HarnessServiceEntry>();
        HarnessServiceEntry? current = null;
        var inRecovery = false;
        var recoverySection = "";

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // New service entry
            if (line.TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    services.Add(current);
                }

                current = new HarnessServiceEntry { Name = ExtractValue(line) };
                inRecovery = false;
                continue;
            }

            if (current is null) continue;

            var trimmed = line.TrimStart();

            // Simple key-value pairs
            if (trimmed.StartsWith("display_name:", StringComparison.Ordinal))
                current.DisplayName = ExtractValue(line);
            else if (trimmed.StartsWith("description:", StringComparison.Ordinal))
                current.Description = ExtractValue(line);
            else if (trimmed.StartsWith("executable:", StringComparison.Ordinal))
                current.Executable = ExtractValue(line);
            else if (trimmed.StartsWith("arguments:", StringComparison.Ordinal))
                current.Arguments = ExtractValue(line);
            else if (trimmed.StartsWith("account:", StringComparison.Ordinal))
                current.Account = ExtractValue(line);
            else if (trimmed.StartsWith("start_order:", StringComparison.Ordinal))
                current.StartOrder = int.Parse(ExtractValue(line), System.Globalization.CultureInfo.InvariantCulture);
            else if (trimmed.StartsWith("stop_order:", StringComparison.Ordinal))
                current.StopOrder = int.Parse(ExtractValue(line), System.Globalization.CultureInfo.InvariantCulture);
            else if (trimmed.StartsWith("startup_type:", StringComparison.Ordinal))
                current.StartupType = ExtractValue(line);
            else if (trimmed.StartsWith("group:", StringComparison.Ordinal))
                current.Group = ExtractValue(line);
            // Health check
            else if (trimmed.StartsWith("type:", StringComparison.Ordinal) && !inRecovery)
                current.HealthCheckType = ExtractValue(line);
            else if (trimmed.StartsWith("url:", StringComparison.Ordinal))
                current.HealthCheckUrl = ExtractValue(line);
            else if (trimmed.StartsWith("timeout_seconds:", StringComparison.Ordinal) && !inRecovery)
                current.HealthCheckTimeout = int.Parse(ExtractValue(line), System.Globalization.CultureInfo.InvariantCulture);
            else if (trimmed.StartsWith("expected_status:", StringComparison.Ordinal))
                current.HealthCheckExpectedStatus = int.Parse(ExtractValue(line), System.Globalization.CultureInfo.InvariantCulture);
            // Recovery
            else if (trimmed.StartsWith("recovery:", StringComparison.Ordinal))
                inRecovery = true;
            else if (trimmed.StartsWith("first_failure:", StringComparison.Ordinal))
            {
                recoverySection = "first";
                ParseRecoveryInline(trimmed, current, recoverySection);
            }
            else if (trimmed.StartsWith("second_failure:", StringComparison.Ordinal))
            {
                recoverySection = "second";
                ParseRecoveryInline(trimmed, current, recoverySection);
            }
            else if (trimmed.StartsWith("subsequent:", StringComparison.Ordinal) && inRecovery)
            {
                recoverySection = "subsequent";
                ParseRecoveryInline(trimmed, current, recoverySection);
            }
            else if (trimmed.StartsWith("reset_after_seconds:", StringComparison.Ordinal))
                current.ResetAfterSeconds = int.Parse(ExtractValue(line), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (current is not null)
        {
            services.Add(current);
        }

        return services;
    }

    private static void ParseRecoveryInline(string line, HarnessServiceEntry entry, string section)
    {
        // Parse inline format: first_failure: { action: "restart", delay_seconds: 10 }
        var actionMatch = ActionPattern().Match(line);
        var delayMatch = DelayPattern().Match(line);

        var action = actionMatch.Success ? actionMatch.Groups[1].Value : "restart";
        var delay = delayMatch.Success ? int.Parse(delayMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 30;

        switch (section)
        {
            case "first":
                entry.FirstFailureAction = action;
                entry.FirstFailureDelay = delay;
                break;
            case "second":
                entry.SecondFailureAction = action;
                entry.SecondFailureDelay = delay;
                break;
            case "subsequent":
                entry.SubsequentAction = action;
                entry.SubsequentDelay = delay;
                break;
        }
    }

    private static string ExtractValue(string line)
    {
        var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex < 0) return "";
        var value = line[(colonIndex + 1)..].Trim();
        // Remove surrounding quotes
        if (value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1];
        return value;
    }

    [GeneratedRegex(@"action:\s*""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex ActionPattern();

    [GeneratedRegex(@"delay_seconds:\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex DelayPattern();

    /// <summary>
    /// Internal mutable model used during parsing, then converted to ServiceMapEntry.
    /// </summary>
    private sealed class HarnessServiceEntry
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Description { get; set; }
        public string Executable { get; set; } = "";
        public string? Arguments { get; set; }
        public string Account { get; set; } = "LocalSystem";
        public int StartOrder { get; set; }
        public int StopOrder { get; set; }
        public string StartupType { get; set; } = "Automatic";
        public string Group { get; set; } = "pacs";
        public string HealthCheckType { get; set; } = "http";
        public string? HealthCheckUrl { get; set; }
        public int HealthCheckTimeout { get; set; } = 15;
        public int HealthCheckExpectedStatus { get; set; } = 200;
        public string FirstFailureAction { get; set; } = "restart";
        public int FirstFailureDelay { get; set; } = 10;
        public string SecondFailureAction { get; set; } = "restart";
        public int SecondFailureDelay { get; set; } = 30;
        public string SubsequentAction { get; set; } = "restart";
        public int SubsequentDelay { get; set; } = 60;
        public int ResetAfterSeconds { get; set; } = 86400;

        public static implicit operator ServiceMapEntry(HarnessServiceEntry e) => new()
        {
            Name = e.Name,
            DisplayName = e.DisplayName,
            Description = e.Description,
            Executable = e.Executable,
            Arguments = e.Arguments,
            Account = e.Account,
            StartOrder = e.StartOrder,
            StopOrder = e.StopOrder,
            StartupType = e.StartupType,
            HealthCheck = new ServiceHealthCheck
            {
                Type = e.HealthCheckType,
                Url = e.HealthCheckUrl,
                TimeoutSeconds = e.HealthCheckTimeout,
                ExpectedStatus = e.HealthCheckExpectedStatus
            },
            Recovery = new ServiceRecovery
            {
                FirstFailure = new RecoveryAction { Action = e.FirstFailureAction, DelaySeconds = e.FirstFailureDelay },
                SecondFailure = new RecoveryAction { Action = e.SecondFailureAction, DelaySeconds = e.SecondFailureDelay },
                Subsequent = new RecoveryAction { Action = e.SubsequentAction, DelaySeconds = e.SubsequentDelay },
                ResetAfterSeconds = e.ResetAfterSeconds
            }
        };
    }
}
