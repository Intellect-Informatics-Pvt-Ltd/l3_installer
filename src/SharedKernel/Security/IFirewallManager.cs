namespace SharedKernel.Security;

/// <summary>
/// Manages Windows Firewall rules for ePACS services.
/// Ensures DB/cache/eventing ports are localhost-only and outbound is restricted to NLDR.
/// All ports and endpoints are configurable.
/// </summary>
public interface IFirewallManager
{
    /// <summary>
    /// Applies firewall rules for all ePACS services.
    /// </summary>
    /// <param name="rules">Firewall rules to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyRulesAsync(IReadOnlyList<FirewallRule> rules, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that expected firewall rules are in place.
    /// </summary>
    /// <param name="rules">Expected rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    Task<FirewallVerificationResult> VerifyAsync(IReadOnlyList<FirewallRule> rules, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates firewall rules from service configuration.
    /// </summary>
    /// <returns>List of firewall rules.</returns>
    IReadOnlyList<FirewallRule> GenerateRules();

    /// <summary>
    /// Removes all ePACS firewall rules (for uninstall).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAllRulesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a Windows Firewall rule.
/// </summary>
public sealed record FirewallRule
{
    /// <summary>Rule display name (prefixed with "ePACS - ").</summary>
    public required string Name { get; init; }

    /// <summary>Direction: Inbound or Outbound.</summary>
    public required FirewallDirection Direction { get; init; }

    /// <summary>Action: Allow or Block.</summary>
    public required FirewallAction Action { get; init; }

    /// <summary>Protocol: TCP or UDP.</summary>
    public string Protocol { get; init; } = "TCP";

    /// <summary>Port number (for port-based rules).</summary>
    public int? Port { get; init; }

    /// <summary>Local address binding (e.g., "127.0.0.1" for localhost-only).</summary>
    public string? LocalAddress { get; init; }

    /// <summary>Remote address (for outbound rules, e.g., NLDR FQDN).</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>Description for documentation.</summary>
    public string? Description { get; init; }
}

public enum FirewallDirection { Inbound, Outbound }
public enum FirewallAction { Allow, Block }

/// <summary>
/// Result of firewall rule verification.
/// </summary>
public sealed record FirewallVerificationResult
{
    public required bool Valid { get; init; }
    public IReadOnlyList<string> MissingRules { get; init; } = [];
    public IReadOnlyList<string> ExtraRules { get; init; } = [];
}
