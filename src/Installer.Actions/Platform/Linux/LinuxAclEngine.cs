using Installer.Actions.Database;
using Installer.Actions.Install;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.Actions.Platform.Linux;

/// <summary>
/// Least privilege on a Debian node, expressed the way Linux expresses it: ownership and mode
/// for the account that writes a directory, group-read for an account that only reads it (root
/// owns, the reader's group reads - no ACL package needed, and <c>stat</c> can verify it), and
/// nothing for everyone else.
///
/// WHAT THE RULES SAY. Each infrastructure service owns its own data and log directories
/// (<c>epacs-db</c> owns <c>mysql/</c>, nothing else can read a society's books off the disk);
/// the application services run as one account (<c>l2r2</c>, the estate's own run user) and
/// own attachments, application logs and the pack directories; the release directory is
/// root-owned and group-readable by <c>l2r2</c>, so a service can read its own
/// <c>appsettings.json</c> (mode 0640 after the rewriter) and cannot modify its own binaries;
/// keys, backups and installer state are root-only.
///
/// WHY VERIFY EXISTS. The Installer Agent's config-drift monitor calls it. A directory whose
/// owner changed is how a support engineer's <c>chown -R</c> during a bad night becomes a
/// service that cannot write its logs three weeks later.
/// </summary>
public sealed class LinuxAclEngine : IAclEngine
{
    private readonly IProcessRunner _runner;
    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ISiteTokenSource _site;
    private readonly ILogger<LinuxAclEngine> _logger;

    /// <summary>The estate's run user for application services (ops/ansible/group_vars/all.yml: l2r2_run_user).</summary>
    public const string ApplicationAccount = "l2r2";

    public LinuxAclEngine(
        IProcessRunner runner,
        IOptions<InstallerOptions> options,
        IOptions<ServicesOptions> services,
        ISiteTokenSource site,
        ILogger<LinuxAclEngine> logger)
    {
        _runner = runner;
        _options = options;
        _services = services;
        _site = site;
        _logger = logger;
    }

    /// <summary>The fixed layout: what DataRootInitializer creates, owned by whoever writes it.</summary>
    public IReadOnlyList<AclRule> GenerateRules() => GenerateRules([]);

    /// <summary>The fixed layout plus every <c>data_directories</c> entry the topology declares, owned by that service's account.</summary>
    public IReadOnlyList<AclRule> GenerateRules(IReadOnlyList<ServiceMapEntry> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var data = _options.Value.DataRoot;
        var bin = _options.Value.BinaryRoot;
        var rules = new List<AclRule>
        {
            // The roots themselves: traversable, root-owned, nobody else writes.
            RootOnly(data, traversable: true, "the data root: traversable, root-owned"),
            RootOnly(bin, traversable: true, "the binary root: traversable, root-owned"),
            RootOnly(Path.Combine(data, "keys"), traversable: false, "the secret store: root only"),
            RootOnly(Path.Combine(data, "backups"), traversable: false, "backups: root only until encryption keys are per-recipient"),
            RootOnly(Path.Combine(data, "installer"), traversable: false, "installer state and checkpoints: root only"),
            RootOnly(Path.Combine(data, "temp"), traversable: false, "installer staging: root only"),
            RootOnly(Path.Combine(data, "logs", "installer"), traversable: false, "installer logs: root only"),

            // What the application services write.
            ReadWrite(Path.Combine(data, "attachments"), ApplicationAccount, "member documents and report output"),
            ReadWrite(Path.Combine(data, "files"), ApplicationAccount, "Storage:AttachmentRoot as the site template names it"),
            ReadWrite(Path.Combine(data, "logs", "app"), ApplicationAccount, "Serilog file sink for every application service"),
            ReadWrite(Path.Combine(data, "logs", "web"), ApplicationAccount, "web tier logs"),
            ReadWrite(Path.Combine(data, "logs", "sync"), ApplicationAccount, "sync agent logs"),
            ReadWrite(Path.Combine(data, "sync"), ApplicationAccount, "sync agent state"),
            ReadWrite(Path.Combine(data, "packs"), ApplicationAccount, "ledger packs out, policy packs in (ADR-0011)"),

            // What the application services READ and must not change: their configuration and
            // their binaries. Root owns both; l2r2 reads through the group.
            ReadOnly(Path.Combine(data, "config"), ApplicationAccount, "generated configuration: read by services, written by the installer"),
            ReadOnly(Path.Combine(bin, "releases"), ApplicationAccount, "every release: binaries and each service's appsettings.json, read-only to the services"),
        };

        // Each service's own data directories, from the topology, owned by its account.
        var tokens = InstallerTokenMap.Merge(InstallerTokenMap.BuildInfrastructure(_options.Value, _services.Value), _site.Tokens);
        foreach (var service in services)
        {
            var account = AccountFor(service);
            foreach (var dir in service.DataDirectories)
            {
                var resolved = InstallerTokenMap.Resolve(dir, tokens, $"data directory of {service.Name}").Replace('\\', '/');
                rules.Add(account == "root"
                    ? RootOnly(resolved, traversable: false, $"{service.Name}: runs as root")
                    : ReadWrite(resolved, account, $"{service.Name}: its own data"));
            }
        }

        return rules;
    }

    public async Task ApplyRulesAsync(IReadOnlyList<AclRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(rule.Path))
            {
                Directory.CreateDirectory(rule.Path);
            }

            foreach (var (exe, args) in CommandsFor(rule))
            {
                var result = await _runner.RunAsync(exe, args, cancellationToken: cancellationToken);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"ACL step failed for {rule.Path} ({rule.Account}, {rule.Permission}): `{exe} {args}` exited {result.ExitCode}: " +
                        $"{result.CombinedOutput.Trim()}. A directory with the wrong owner does not fail now; its service fails later.");
                }
            }

            LogEvents.AclApplied(_logger, rule.Path, rule.Account, rule.Permission);
        }
    }

    public async Task<AclVerificationResult> VerifyAsync(IReadOnlyList<AclRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var mismatches = new List<string>();
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(rule.Path))
            {
                mismatches.Add($"{rule.Path}: missing");
                continue;
            }

            var stat = await _runner.RunAsync("stat", $"-c \"%U %a\" \"{rule.Path}\"", cancellationToken: cancellationToken);
            if (!stat.Succeeded)
            {
                mismatches.Add($"{rule.Path}: stat failed ({stat.CombinedOutput.Trim()})");
                continue;
            }

            var parts = stat.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var (owner, mode) = parts.Length >= 2 ? (parts[0], parts[1]) : ("?", "?");
            var (expectedOwner, expectedMode) = Expected(rule);
            if (!string.Equals(owner, expectedOwner, StringComparison.Ordinal))
            {
                mismatches.Add($"{rule.Path}: owner is {owner}, expected {expectedOwner}");
            }

            if (!string.Equals(mode, expectedMode, StringComparison.Ordinal))
            {
                mismatches.Add($"{rule.Path}: mode is {mode}, expected {expectedMode}");
            }

            if (rule.Permission == AclAccessLevel.ReadOnly)
            {
                // Read-only for a NON-owner is a group grant: the group must be the reader.
                var group = await _runner.RunAsync("stat", $"-c \"%G\" \"{rule.Path}\"", cancellationToken: cancellationToken);
                if (!string.Equals(group.StandardOutput.Trim(), rule.Account, StringComparison.Ordinal))
                {
                    mismatches.Add($"{rule.Path}: group is {group.StandardOutput.Trim()}, expected {rule.Account} (read-only grant)");
                }
            }
        }

        return new AclVerificationResult { Valid = mismatches.Count == 0, Mismatches = mismatches };
    }

    /// <summary>
    /// The commands one rule becomes. Exposed for the contract tests: the whole point of a
    /// least-privilege engine is that what it runs is exactly what it says.
    /// </summary>
    public static IReadOnlyList<(string Executable, string Arguments)> CommandsFor(AclRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var p = $"\"{rule.Path}\"";
        var r = rule.Recursive ? "-R " : "";
        return rule.Permission switch
        {
            // Owner writes; group (the same account) reads; nobody else. Directories keep x through X.
            AclAccessLevel.ReadWrite or AclAccessLevel.FullControl =>
            [
                ("chown", $"{r}{rule.Account}:{rule.Account} {p}"),
                ("chmod", $"{r}u=rwX,g=rX,o= {p}"),
            ],
            // Root owns and writes; the account's GROUP reads. No ACL package needed, and
            // `stat` can verify it - a setfacl grant is invisible to the mode.
            AclAccessLevel.ReadOnly =>
            [
                ("chown", $"{r}root:{rule.Account} {p}"),
                ("chmod", $"{r}u=rwX,g=rX,o= {p}"),
            ],
            // Root only. 0700 for a leaf; 0755 for a root that must stay traversable so the
            // accounts can reach their own subdirectories beneath it.
            _ => rule.Recursive
                ? [("chown", $"-R root:root {p}"), ("chmod", $"-R u=rwX,g=,o= {p}")]
                : [("chown", $"root:root {p}"), ("chmod", $"u=rwx,g=rx,o=rx {p}")],
        };
    }

    private static (string Owner, string Mode) Expected(AclRule rule) => rule.Permission switch
    {
        AclAccessLevel.ReadWrite or AclAccessLevel.FullControl => (rule.Account, "750"),
        AclAccessLevel.ReadOnly => ("root", "750"),
        _ => ("root", rule.Recursive ? "700" : "755"),
    };

    /// <summary>The account a service runs as on Linux; LocalSystem is a Windows word and means root here.</summary>
    public static string AccountFor(ServiceMapEntry service) =>
        string.IsNullOrWhiteSpace(service.Account) || service.Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            ? "root"
            : service.Account;

    private static AclRule ReadWrite(string path, string account, string why) =>
        new() { Path = path, Account = account, Permission = AclAccessLevel.ReadWrite, Recursive = true, Description = why };

    private static AclRule ReadOnly(string path, string account, string why) =>
        new() { Path = path, Account = account, Permission = AclAccessLevel.ReadOnly, Recursive = true, Description = why };

    /// <summary><c>None</c> for every account but root. Traversable roots are non-recursive 0755; leaves are recursive 0700.</summary>
    private static AclRule RootOnly(string path, bool traversable, string why) =>
        new() { Path = path, Account = "root", Permission = AclAccessLevel.None, Recursive = !traversable, Description = why };

}
