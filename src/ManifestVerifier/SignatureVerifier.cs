using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace ManifestVerifier;

/// <summary>
/// Verifies Authenticode and CMS/PKCS#7 detached signatures.
/// Used to validate installer packages and release manifests.
/// </summary>
public sealed class SignatureVerifier : ISignatureVerifier
{
    /// <inheritdoc />
    public SignatureVerificationResult VerifyAuthenticode(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return SignatureVerificationResult.Failure($"File not found: {filePath}");
        }

        // On Windows, use AuthenticodeSignatureInformation via WinVerifyTrust.
        // For cross-platform development/testing, we verify the file exists and
        // delegate to the platform-specific implementation at runtime.
        // This is a placeholder that will use P/Invoke on Windows.
        try
        {
            // In production on Windows, this would call WinVerifyTrust.
            // For now, return a result indicating the check is not available on this platform.
            if (!OperatingSystem.IsWindows())
            {
                return SignatureVerificationResult.Failure(
                    "Authenticode verification is only available on Windows. Skipping in development.");
            }

            // Windows implementation would go here using WinVerifyTrust P/Invoke
            // For the initial implementation, we trust the file if it exists
            // and defer full Authenticode to the WiX Burn engine which handles this natively.
            return SignatureVerificationResult.Failure(
                "Authenticode verification not yet implemented. Will be handled by WiX Burn engine.");
        }
        catch (Exception ex)
        {
            return SignatureVerificationResult.Failure($"Authenticode verification error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public SignatureVerificationResult VerifyDetachedSignature(
        string contentPath,
        string signaturePath,
        string? expectedThumbprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);

        if (!File.Exists(contentPath))
        {
            return SignatureVerificationResult.Failure($"Content file not found: {contentPath}");
        }

        if (!File.Exists(signaturePath))
        {
            return SignatureVerificationResult.Failure($"Signature file not found: {signaturePath}");
        }

        try
        {
            var contentBytes = File.ReadAllBytes(contentPath);
            var signatureBytes = File.ReadAllBytes(signaturePath);

            var contentInfo = new ContentInfo(contentBytes);
            var signedCms = new SignedCms(contentInfo, detached: true);
            signedCms.Decode(signatureBytes);

            // Verify the signature (checks certificate chain and integrity)
            signedCms.CheckSignature(verifySignatureOnly: false);

            var signerCert = signedCms.SignerInfos[0].Certificate;
            if (signerCert is null)
            {
                return SignatureVerificationResult.Failure("No signing certificate found in signature.");
            }

            var thumbprint = signerCert.Thumbprint;
            var subjectName = signerCert.Subject;

            // Verify thumbprint if expected
            if (expectedThumbprint is not null &&
                !string.Equals(thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return SignatureVerificationResult.Failure(
                    $"Certificate thumbprint mismatch. Expected: {expectedThumbprint}, Got: {thumbprint}");
            }

            // Check for timestamp
            var hasTimestamp = signedCms.SignerInfos[0].UnsignedAttributes
                .Cast<CryptographicAttributeObject>()
                .Any(a => a.Oid?.Value == "1.2.840.113549.1.9.6"); // id-smime-aa-timeStampToken

            return SignatureVerificationResult.Success(subjectName, thumbprint, hasTimestamp);
        }
        catch (CryptographicException ex)
        {
            return SignatureVerificationResult.Failure($"Signature verification failed: {ex.Message}");
        }
    }
}
