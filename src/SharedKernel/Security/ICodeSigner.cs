namespace SharedKernel.Security;

/// <summary>
/// Abstraction for code signing operations.
/// Used for signing release manifests, backup manifests, and verifying Authenticode signatures.
/// Signing key is HSM/Key Vault-backed in production (configurable).
/// </summary>
public interface ICodeSigner
{
    /// <summary>
    /// Creates a detached CMS/PKCS#7 signature for a file.
    /// </summary>
    /// <param name="contentPath">Path to the file to sign.</param>
    /// <param name="signaturePath">Path to write the detached signature.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SignFileAsync(string contentPath, string signaturePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a detached CMS/PKCS#7 signature.
    /// </summary>
    /// <param name="contentPath">Path to the content file.</param>
    /// <param name="signaturePath">Path to the signature file.</param>
    /// <param name="expectedThumbprint">Expected signing certificate thumbprint (null = any trusted cert).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    Task<SignatureResult> VerifyFileAsync(string contentPath, string signaturePath, string? expectedThumbprint = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a certificate chain (checks trust, expiry, revocation).
    /// </summary>
    /// <param name="certificateThumbprint">Thumbprint of the certificate to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chain validation result.</returns>
    Task<CertificateChainResult> ValidateChainAsync(string certificateThumbprint, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a signature verification operation.
/// </summary>
public sealed record SignatureResult
{
    public required bool Valid { get; init; }
    public string? SignerSubject { get; init; }
    public string? SignerThumbprint { get; init; }
    public DateTimeOffset? SignedAt { get; init; }
    public bool HasTimestamp { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of certificate chain validation.
/// </summary>
public sealed record CertificateChainResult
{
    public required bool Valid { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    public int DaysUntilExpiry { get; init; }
    public string? ErrorMessage { get; init; }
}
