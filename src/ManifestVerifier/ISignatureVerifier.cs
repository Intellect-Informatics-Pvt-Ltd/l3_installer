namespace ManifestVerifier;

/// <summary>
/// Verifies Authenticode digital signatures on installer packages and manifests.
/// </summary>
public interface ISignatureVerifier
{
    /// <summary>
    /// Verifies the Authenticode signature of a file.
    /// </summary>
    /// <param name="filePath">Path to the signed file.</param>
    /// <returns>Verification result with details.</returns>
    SignatureVerificationResult VerifyAuthenticode(string filePath);

    /// <summary>
    /// Verifies a detached signature (e.g., manifest .sig file).
    /// </summary>
    /// <param name="contentPath">Path to the content file.</param>
    /// <param name="signaturePath">Path to the detached signature file.</param>
    /// <param name="expectedThumbprint">Expected signing certificate thumbprint (optional).</param>
    /// <returns>Verification result with details.</returns>
    SignatureVerificationResult VerifyDetachedSignature(
        string contentPath,
        string signaturePath,
        string? expectedThumbprint = null);
}

/// <summary>
/// Result of a signature verification operation.
/// </summary>
public sealed record SignatureVerificationResult
{
    /// <summary>Whether the signature is valid.</summary>
    public required bool Valid { get; init; }

    /// <summary>Signing certificate subject name.</summary>
    public string? SubjectName { get; init; }

    /// <summary>Signing certificate thumbprint.</summary>
    public string? Thumbprint { get; init; }

    /// <summary>Whether the signature includes a trusted timestamp.</summary>
    public bool HasTimestamp { get; init; }

    /// <summary>Error message if verification failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static SignatureVerificationResult Success(string subjectName, string thumbprint, bool hasTimestamp) =>
        new() { Valid = true, SubjectName = subjectName, Thumbprint = thumbprint, HasTimestamp = hasTimestamp };

    /// <summary>Creates a failed result.</summary>
    public static SignatureVerificationResult Failure(string errorMessage) =>
        new() { Valid = false, ErrorMessage = errorMessage };
}
