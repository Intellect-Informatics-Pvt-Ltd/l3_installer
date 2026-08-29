using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace SharedKernel.Security;

/// <summary>
/// Detached CMS/PKCS#7 signing — the producing half of what
/// <c>ManifestVerifier.SignatureVerifier.VerifyDetachedSignature</c> already checks.
///
/// ── WHY THIS AND NOT AUTHENTICODE ───────────────────────────────────────────────────────────
///
/// Detached CMS over the manifest is currently the **only** tamper-evidence in force. ADR-0001's
/// WiX Burn bootstrapper — which was to provide Authenticode over the outer EXE — does not exist,
/// and <c>SignatureVerifier.VerifyAuthenticode</c> returns failure on every call while deferring
/// to it. So this signature is not a belt-and-braces extra; it is the mechanism.
///
/// It is also the right shape for this product independent of that. The manifest names every
/// payload and its SHA-256, so one signature over the manifest transitively covers ~1.8 GB of
/// media without signing 1.8 GB. And it is platform-neutral: the same signature verifies on
/// Windows and Debian, which matters now that both are targets (ADR-0010).
///
/// ── WHAT THIS CLASS DOES NOT DECIDE ─────────────────────────────────────────────────────────
///
/// Where the key lives. It signs with a certificate handed to it — from a PFX for a developer or
/// a CI secret, or from an <see cref="X509Store"/> where an HSM or Key Vault CNG provider has
/// made the private key available without exporting it. The production ceremony (EV certificate,
/// key escrow, who may invoke it) is a governance question with an owner, and this class is
/// deliberately agnostic so that answer can change without touching code.
/// </summary>
public sealed class CmsCodeSigner : ICodeSigner
{
    private readonly Func<X509Certificate2?> _certificateProvider;

    /// <param name="certificateProvider">
    /// Supplies the signing certificate, or null when none is configured. A factory rather than
    /// an instance so an unsigned build does not have to construct one, and so a key that lives
    /// in an HSM is fetched at the moment of signing rather than held.
    ///
    /// <b>The provider owns the certificate's lifetime.</b> This class never disposes what it is
    /// handed: a provider returning a long-lived instance would otherwise find it dead after the
    /// first signature.
    /// </param>
    public CmsCodeSigner(Func<X509Certificate2?> certificateProvider) =>
        _certificateProvider = certificateProvider ?? throw new ArgumentNullException(nameof(certificateProvider));

    public async Task SignFileAsync(string contentPath, string signaturePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);

        if (!File.Exists(contentPath))
        {
            throw new FileNotFoundException($"Nothing to sign: {contentPath} does not exist.", contentPath);
        }

        // NOT disposed here. The provider owns the certificate's lifetime — it may be handing
        // back a long-lived instance from an X509Store or a cached HSM handle, and disposing
        // something we did not create leaves the caller with a dead handle on the next call.
        // Found by a test that signed twice with the same provider and got
        // "m_safeCertContext is an invalid handle" on the second.
        var certificate = _certificateProvider()
            ?? throw new InvalidOperationException(
                "No signing certificate is configured, so the manifest cannot be signed. A medium without a manifest " +
                "signature has no tamper-evidence at all — detached CMS over the manifest is the only mechanism in " +
                "force, since Authenticode verification is unimplemented (ADR-0001). Build with --unsigned only for " +
                "a development medium, and never ship one.");

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"The signing certificate {certificate.Subject} has no private key available. A certificate loaded " +
                "for verification cannot sign; check that the PFX was loaded with its key, or that the HSM/Key Vault " +
                "provider is reachable.");
        }

        var content = await File.ReadAllBytesAsync(contentPath, cancellationToken);
        var signedCms = new SignedCms(new ContentInfo(content), detached: true);

        var signer = new CmsSigner(certificate)
        {
            // SHA-256 explicitly. The default has changed across .NET versions, and a signature
            // whose digest algorithm depends on the SDK that produced it is not reproducible.
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),

            // Embed the whole chain, not just the leaf. A PACS node is offline: it cannot fetch
            // an intermediate from an AIA URL, so a signature that omits one is unverifiable at
            // exactly the place it needs to be verified.
            IncludeOption = X509IncludeOption.WholeChain
        };

        signedCms.ComputeSignature(signer);

        var directory = Path.GetDirectoryName(signaturePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(signaturePath, signedCms.Encode(), cancellationToken);
    }

    public Task<SignatureResult> VerifyFileAsync(
        string contentPath, string signaturePath, string? expectedThumbprint = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);

        try
        {
            var content = File.ReadAllBytes(contentPath);
            var signature = File.ReadAllBytes(signaturePath);

            var signedCms = new SignedCms(new ContentInfo(content), detached: true);
            signedCms.Decode(signature);

            // Same rule as ManifestVerifier.SignatureVerifier, and for the same reason: a pinned
            // thumbprint is a narrower statement than chain trust and does not depend on a trust
            // store the target machine may not have. Unpinned falls back to full chain
            // validation. Keeping the two implementations consistent matters — a medium that
            // verifies in the builder and not on the node, or the reverse, is the worst outcome.
            var pinned = !string.IsNullOrWhiteSpace(expectedThumbprint);
            signedCms.CheckSignature(verifySignatureOnly: pinned);

            var signerInfo = signedCms.SignerInfos[0];
            var cert = signerInfo.Certificate;
            if (cert is null)
            {
                return Task.FromResult(new SignatureResult { Valid = false, ErrorMessage = "No signing certificate in the signature." });
            }

            if (expectedThumbprint is not null &&
                !string.Equals(cert.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new SignatureResult
                {
                    Valid = false,
                    SignerThumbprint = cert.Thumbprint,
                    ErrorMessage = $"Signed by {cert.Thumbprint}, but the manifest declares {expectedThumbprint}. " +
                                   "A validly-signed medium from the wrong signer is the case this check exists for."
                });
            }

            var hasTimestamp = signerInfo.UnsignedAttributes
                .Cast<CryptographicAttributeObject>()
                .Any(a => a.Oid?.Value == "1.2.840.113549.1.9.6");

            return Task.FromResult(new SignatureResult
            {
                Valid = true,
                SignerSubject = cert.Subject,
                SignerThumbprint = cert.Thumbprint,
                HasTimestamp = hasTimestamp
            });
        }
        catch (CryptographicException ex)
        {
            return Task.FromResult(new SignatureResult { Valid = false, ErrorMessage = ex.Message });
        }
    }

    public Task<CertificateChainResult> ValidateChainAsync(string certificateThumbprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateThumbprint);

        var certificate = _certificateProvider();   // see SignFileAsync: the provider owns this
        if (certificate is null)
        {
            return Task.FromResult(new CertificateChainResult { Valid = false, ErrorMessage = "No certificate configured." });
        }

        using var chain = new X509Chain();

        // Revocation is NOT checked, deliberately, and the reason is the product: this runs on
        // an air-gapped node where a CRL or OCSP fetch cannot succeed. Setting Online here would
        // mean every verification either hangs on a timeout or fails for the wrong reason.
        // Revocation must be enforced at the point the medium is BUILT, where there is a
        // network, not at the point it is installed, where there is not.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        var valid = chain.Build(certificate);
        var daysLeft = (int)(certificate.NotAfter - DateTime.Now).TotalDays;

        return Task.FromResult(new CertificateChainResult
        {
            Valid = valid,
            Subject = certificate.Subject,
            NotBefore = certificate.NotBefore,
            NotAfter = certificate.NotAfter,
            DaysUntilExpiry = daysLeft,
            ErrorMessage = valid
                ? null
                : string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()))
        });
    }
}
