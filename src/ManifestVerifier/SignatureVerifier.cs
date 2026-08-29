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

            // ── PINNED SIGNER vs CHAIN TRUST ────────────────────────────────────────────────
            //
            // When an expected thumbprint is supplied we verify the signature cryptographically
            // and require it to come from that exact certificate, WITHOUT walking the machine's
            // trust store. When none is supplied we fall back to full chain validation.
            //
            // Why pinning is the right primary mechanism for THIS product, not a weakening:
            //
            //   * The node is air-gapped. Chain validation consults the local trust store and,
            //     for revocation, the network. An internal signing chain is not in a stock
            //     Windows or Debian trust store, so a correctly-signed medium would be REFUSED
            //     on a fresh node — and refused with "certificate not trusted", which reads like
            //     tampering. That is a false alarm at the worst possible site.
            //   * Pinning one leaf is a narrower statement than "anything this CA issued".
            //   * Certificates expire. A medium built today and used to rebuild a node in four
            //     years must still verify; chain validation fails on expiry, and there is nobody
            //     at a PACS site to reason about that.
            //
            // WHERE THE PIN MUST COME FROM, and this is the part that is easy to get wrong:
            // the installer's OWN configuration, fixed when the installer was built. NOT the
            // manifest's `signing_cert_thumbprint`, which is self-asserted — an attacker who
            // re-signs a tampered manifest with their own key would simply write their own
            // thumbprint into it, and the check would pass. The manifest's declaration is
            // compared and reported, never trusted.
            var pinned = !string.IsNullOrWhiteSpace(expectedThumbprint);
            signedCms.CheckSignature(verifySignatureOnly: pinned);

            var signerCert = signedCms.SignerInfos[0].Certificate;
            if (signerCert is null)
            {
                return SignatureVerificationResult.Failure("No signing certificate found in signature.");
            }

            var thumbprint = signerCert.Thumbprint;
            var subjectName = signerCert.Subject;

            if (pinned && !string.Equals(thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return SignatureVerificationResult.Failure(
                    $"Signed by {thumbprint} ({subjectName}), but this installer is pinned to {expectedThumbprint}. " +
                    "A validly-signed medium from an unexpected signer is exactly what this check is for.");
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
