using System.Globalization;
using System.Text;
using Installer.Actions.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Security;

namespace Installer.Actions.Platform.Linux;

/// <summary>
/// The node's firewall, as one nftables table it owns outright: <c>inet epacs</c>.
///
/// THE POLICY, IN ONE PARAGRAPH. Loopback is open, established traffic is allowed back in, SSH
/// stays reachable for the state's field support, and the application ports are reachable from
/// the society's LAN — the cashier's terminal is another machine. The database, the cache,
/// eventing and the two agents' health ports are reachable from this machine only: nothing on
/// the LAN gets a socket to MySQL. Everything else inbound is dropped. Outbound is not
/// restricted: on an offline node there is nothing to reach, and on a connected one the pack
/// path (ADR-0011) needs whatever link the site has; the Windows-era "outbound only to NLDR"
/// rule targeted a counterparty that does not exist.
///
/// WHY ITS OWN TABLE. <c>nft delete table inet epacs</c> is the whole uninstall, and it cannot
/// take a site's own rules with it. The ruleset is rendered to a file first and checked with
/// <c>nft -c -f</c> before it is loaded, so a rendering mistake is a refusal, not a locked-out
/// box.
/// </summary>
public sealed class NftablesFirewallManager : IFirewallManager
{
    public const string TableName = "epacs";
    public const string RulesetPath = "/etc/nftables.d/epacs.nft";

    private readonly IProcessRunner _runner;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ILogger<NftablesFirewallManager> _logger;

    public NftablesFirewallManager(IProcessRunner runner, IOptions<ServicesOptions> services, ILogger<NftablesFirewallManager> logger)
    {
        _runner = runner;
        _services = services;
        _logger = logger;
    }

    public IReadOnlyList<FirewallRule> GenerateRules()
    {
        var s = _services.Value;
        var rules = new List<FirewallRule>
        {
            Local("MySQL", s.MySql.Port, "the database: this machine only; nothing on the LAN gets a socket to a society's books"),
            Local("Cache", s.Cache.Port, "Garnet: this machine only"),
            Local("Eventing", s.Eventing.Port, "Kafka, when enabled: this machine only"),
            Local("SyncAgentHealth", s.Sync.HealthPort, "the sync agent's health endpoint: this machine only"),
            Local("InstallerAgentHealth", s.Agent.HealthPort, "the installer agent's health endpoint: this machine only"),
            new() { Name = "SSH", Direction = FirewallDirection.Inbound, Action = FirewallAction.Allow, Port = 22, Description = "field support from the state" },
        };

        foreach (var (name, app) in s.Applications.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            rules.Add(new FirewallRule
            {
                Name = name, Direction = FirewallDirection.Inbound, Action = FirewallAction.Allow, Port = app.Port,
                Description = $"{name}: reachable from the society's LAN"
            });
        }

        return rules;
    }

    public async Task ApplyRulesAsync(IReadOnlyList<FirewallRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var ruleset = Render(rules);
        Directory.CreateDirectory(Path.GetDirectoryName(RulesetPath)!);
        await File.WriteAllTextAsync(RulesetPath, ruleset, cancellationToken);

        // Check before load. A syntax error in a ruleset that has already replaced the live one
        // is a box nobody can reach.
        var check = await _runner.RunAsync("nft", $"-c -f {RulesetPath}", cancellationToken: cancellationToken);
        if (!check.Succeeded)
        {
            throw new InvalidOperationException(
                $"The rendered firewall ruleset does not pass `nft -c -f`: {check.CombinedOutput.Trim()}. Nothing was loaded. " +
                $"The file is at {RulesetPath} for inspection.");
        }

        var load = await _runner.RunAsync("nft", $"-f {RulesetPath}", cancellationToken: cancellationToken);
        if (!load.Succeeded)
        {
            throw new InvalidOperationException($"`nft -f {RulesetPath}` failed: {load.CombinedOutput.Trim()}.");
        }

        LogEvents.FirewallApplied(_logger, rules.Count, RulesetPath);
    }

    public async Task<FirewallVerificationResult> VerifyAsync(IReadOnlyList<FirewallRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var live = await _runner.RunAsync("nft", $"list table inet {TableName}", cancellationToken: cancellationToken);
        if (!live.Succeeded)
        {
            return new FirewallVerificationResult { Valid = false, MissingRules = ["table inet epacs (not loaded)"] };
        }

        var missing = new List<string>();
        foreach (var rule in rules.Where(r => r.Port is not null))
        {
            var needle = $"dport {rule.Port!.Value.ToString(CultureInfo.InvariantCulture)}";
            if (!live.StandardOutput.Contains(needle, StringComparison.Ordinal))
            {
                missing.Add($"{rule.Name} ({needle})");
            }
        }

        return new FirewallVerificationResult { Valid = missing.Count == 0, MissingRules = missing };
    }

    public async Task RemoveAllRulesAsync(CancellationToken cancellationToken = default)
    {
        // Our table and only our table. A site's own rules are not ours to touch.
        var result = await _runner.RunAsync("nft", $"delete table inet {TableName}", cancellationToken: cancellationToken);
        if (!result.Succeeded && !result.CombinedOutput.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not remove the ePACS firewall table: {result.CombinedOutput.Trim()}.");
        }

        if (File.Exists(RulesetPath))
        {
            File.Delete(RulesetPath);
        }

        LogEvents.FirewallRemoved(_logger, TableName);
    }

    /// <summary>The ruleset text. Exposed so the contract tests can hand it to <c>nft -c -f</c> on a real Debian.</summary>
    public static string Render(IReadOnlyList<FirewallRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/sbin/nft -f");
        sb.AppendLine("# GENERATED BY THE ePACS INSTALLER. DO NOT EDIT ON THE HOST - repair rewrites it.");
        sb.AppendLine("# One table, owned outright: `nft delete table inet epacs` is the whole uninstall.");
        sb.AppendLine(c, $"table inet {TableName} {{");
        sb.AppendLine("  chain input {");
        sb.AppendLine("    type filter hook input priority 0; policy drop;");
        sb.AppendLine("    iif lo accept");
        sb.AppendLine("    ct state established,related accept");
        sb.AppendLine("    ct state invalid drop");
        sb.AppendLine("    ip protocol icmp accept");
        sb.AppendLine("    ip6 nexthdr icmpv6 accept");

        foreach (var rule in rules.Where(r => r.Direction == FirewallDirection.Inbound && r.Port is not null))
        {
            var proto = rule.Protocol.ToLowerInvariant();
            var port = rule.Port!.Value.ToString(c);
            var comment = $" comment \"ePACS - {rule.Name}\"";
            if (rule.Action == FirewallAction.Allow && rule.LocalAddress is "127.0.0.1")
            {
                // Loopback is already accepted above; a localhost-only service needs nothing
                // more than the default drop. The rule is stated so `nft list` shows the intent.
                sb.AppendLine(c, $"    {proto} dport {port} iif != lo drop comment \"ePACS - {rule.Name} (localhost only)\"");
            }
            else if (rule.Action == FirewallAction.Allow)
            {
                sb.AppendLine(c, $"    {proto} dport {port} accept{comment}");
            }
            else
            {
                sb.AppendLine(c, $"    {proto} dport {port} drop{comment}");
            }
        }

        sb.AppendLine("  }");
        sb.AppendLine("  chain forward {");
        sb.AppendLine("    type filter hook forward priority 0; policy drop;");
        sb.AppendLine("  }");
        sb.AppendLine("  chain output {");
        sb.AppendLine("    type filter hook output priority 0; policy accept;");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static FirewallRule Local(string name, int port, string why) => new()
    {
        Name = name, Direction = FirewallDirection.Inbound, Action = FirewallAction.Allow, Port = port,
        LocalAddress = "127.0.0.1", Description = why
    };
}
