using System.Runtime.InteropServices;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.Actions.Platform;

/// <summary>
/// The engines that do not exist on this platform yet, refusing BY NAME at use — never at
/// resolve, so the graph still validates and a dry run still runs (the same reasoning as
/// <see cref="Install.UnsupportedPlatformServiceOrchestrator"/>). Windows is the case today:
/// the Linux ACL, firewall and account engines are built (2026-09-13); the Windows ones (NTFS
/// ACLs, Windows Firewall, local accounts - tasks.md 25, 26, X1) are not.
/// </summary>
internal static class NotYetBuilt
{
    public static PlatformNotSupportedException For(string engine, string task) =>
        new($"{engine} is not built for {RuntimeInformation.OSDescription} (tasks.md {task}). The Debian/systemd engines exist " +
            "(ADR-0010); the Windows ones do not yet. Nothing was changed. This run exits 4, naming the missing engine, rather " +
            "than 0 - a node with services registered but no ACLs, no firewall and no accounts is not an installed node.");
}

public sealed class NotYetBuiltAclEngine : IAclEngine
{
    public IReadOnlyList<AclRule> GenerateRules() => [];
    public IReadOnlyList<AclRule> GenerateRules(IReadOnlyList<ServiceMapEntry> services) => [];
    public Task ApplyRulesAsync(IReadOnlyList<AclRule> rules, CancellationToken cancellationToken = default) =>
        throw NotYetBuilt.For("The ACL engine", "25 / 8.2 / 18.4");
    public Task<AclVerificationResult> VerifyAsync(IReadOnlyList<AclRule> rules, CancellationToken cancellationToken = default) =>
        throw NotYetBuilt.For("The ACL engine", "25");
}

public sealed class NotYetBuiltFirewallManager : IFirewallManager
{
    public IReadOnlyList<FirewallRule> GenerateRules() => [];
    public Task ApplyRulesAsync(IReadOnlyList<FirewallRule> rules, CancellationToken cancellationToken = default) =>
        throw NotYetBuilt.For("The firewall engine", "26 / 8.8");
    public Task<FirewallVerificationResult> VerifyAsync(IReadOnlyList<FirewallRule> rules, CancellationToken cancellationToken = default) =>
        throw NotYetBuilt.For("The firewall engine", "26");
    // Removal is a no-op, deliberately: this engine never applied anything, so an uninstall on
    // this platform has nothing of ours to take away and must not be blocked by that fact.
    public Task RemoveAllRulesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class NotYetBuiltServiceAccountProvisioner : IServiceAccountProvisioner
{
    public Task<IReadOnlyList<string>> EnsureAsync(IReadOnlyList<ServiceMapEntry> services, CancellationToken cancellationToken = default) =>
        throw NotYetBuilt.For("The service-account provisioner", "X1");
}
