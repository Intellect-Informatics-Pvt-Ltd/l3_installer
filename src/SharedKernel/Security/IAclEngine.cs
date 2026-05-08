namespace SharedKernel.Security;

/// <summary>
/// Manages NTFS Access Control Lists for ePACS directories.
/// Applies least-privilege per-service account permissions.
/// All paths and accounts are configurable via service-map.yaml.
/// </summary>
public interface IAclEngine
{
    /// <summary>
    /// Applies ACL rules to all ePACS directories based on service account mappings.
    /// </summary>
    /// <param name="rules">ACL rules to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApplyRulesAsync(IReadOnlyList<AclRule> rules, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that current ACLs match expected rules.
    /// Used by Installer Agent health checks.
    /// </summary>
    /// <param name="rules">Expected ACL rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result with any mismatches.</returns>
    Task<AclVerificationResult> VerifyAsync(IReadOnlyList<AclRule> rules, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates ACL rules from service map and installer options.
    /// </summary>
    /// <returns>List of ACL rules to apply.</returns>
    IReadOnlyList<AclRule> GenerateRules();
}

/// <summary>
/// Represents a single ACL rule for a directory.
/// </summary>
public sealed record AclRule
{
    /// <summary>Directory path to apply the rule to.</summary>
    public required string Path { get; init; }

    /// <summary>Windows account name (e.g., "ePACSDbSvc").</summary>
    public required string Account { get; init; }

    /// <summary>Permission level: ReadOnly, ReadWrite, FullControl.</summary>
    public required AclAccessLevel Permission { get; init; }

    /// <summary>Whether to apply recursively to subdirectories.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Description for logging/documentation.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Access levels for ACL rules.
/// </summary>
public enum AclAccessLevel
{
    /// <summary>Read and list only.</summary>
    ReadOnly,

    /// <summary>Read, write, and modify.</summary>
    ReadWrite,

    /// <summary>Full control including permission changes.</summary>
    FullControl,

    /// <summary>No access (explicit deny).</summary>
    None
}

/// <summary>
/// Result of ACL verification.
/// </summary>
public sealed record AclVerificationResult
{
    public required bool Valid { get; init; }
    public IReadOnlyList<string> Mismatches { get; init; } = [];
}
