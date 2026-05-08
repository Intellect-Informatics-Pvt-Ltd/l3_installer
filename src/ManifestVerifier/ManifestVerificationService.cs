using SharedKernel.Contracts;

namespace ManifestVerifier;

/// <summary>
/// Orchestrates full manifest verification: parse → verify signature → verify payload hashes.
/// This is the main entry point for the VERIFY phase of the installer state machine.
/// </summary>
public sealed class ManifestVerificationService : IManifestVerificationService
{
    private readonly IManifestParser _parser;
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IHashVerifier _hashVerifier;

    public ManifestVerificationService(
        IManifestParser parser,
        ISignatureVerifier signatureVerifier,
        IHashVerifier hashVerifier)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _hashVerifier = hashVerifier ?? throw new ArgumentNullException(nameof(hashVerifier));
    }

    /// <inheritdoc />
    public async Task<ManifestVerificationResult> VerifyAsync(
        string manifestPath,
        string payloadDirectory,
        string? signaturePath = null,
        string? expectedThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);

        var errors = new List<string>();

        // Step 1: Parse manifest
        ReleaseManifest manifest;
        try
        {
            manifest = _parser.ParseFile(manifestPath);
        }
        catch (Exception ex)
        {
            return ManifestVerificationResult.Failed($"Manifest parse error: {ex.Message}");
        }

        // Step 2: Verify manifest signature (if signature file provided)
        if (signaturePath is not null)
        {
            var sigResult = _signatureVerifier.VerifyDetachedSignature(
                manifestPath, signaturePath, expectedThumbprint);

            if (!sigResult.Valid)
            {
                errors.Add($"Manifest signature invalid: {sigResult.ErrorMessage}");
            }
        }

        // Step 3: Verify each payload hash
        var payloadResults = new List<PayloadVerificationResult>();
        foreach (var payload in manifest.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payloadPath = Path.Combine(payloadDirectory, payload.File);

            if (!File.Exists(payloadPath))
            {
                payloadResults.Add(new PayloadVerificationResult
                {
                    PayloadName = payload.Name,
                    FileName = payload.File,
                    Valid = false,
                    ErrorMessage = "File not found"
                });
                errors.Add($"Payload '{payload.Name}' file not found: {payload.File}");
                continue;
            }

            var hashValid = await _hashVerifier.VerifyAsync(payloadPath, payload.Sha256, cancellationToken);

            payloadResults.Add(new PayloadVerificationResult
            {
                PayloadName = payload.Name,
                FileName = payload.File,
                Valid = hashValid,
                ErrorMessage = hashValid ? null : "SHA-256 hash mismatch"
            });

            if (!hashValid)
            {
                errors.Add($"Payload '{payload.Name}' hash mismatch for file: {payload.File}");
            }
        }

        return new ManifestVerificationResult
        {
            Valid = errors.Count == 0,
            Manifest = manifest,
            PayloadResults = payloadResults,
            Errors = errors
        };
    }
}

/// <summary>
/// Orchestrates manifest verification (parse + signature + hash).
/// </summary>
public interface IManifestVerificationService
{
    /// <summary>
    /// Performs full manifest verification: parse, signature check, and payload hash verification.
    /// </summary>
    Task<ManifestVerificationResult> VerifyAsync(
        string manifestPath,
        string payloadDirectory,
        string? signaturePath = null,
        string? expectedThumbprint = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of full manifest verification.
/// </summary>
public sealed record ManifestVerificationResult
{
    public required bool Valid { get; init; }
    public ReleaseManifest? Manifest { get; init; }
    public IReadOnlyList<PayloadVerificationResult> PayloadResults { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ManifestVerificationResult Failed(string error) =>
        new() { Valid = false, Errors = [error] };
}

/// <summary>
/// Result of verifying a single payload file.
/// </summary>
public sealed record PayloadVerificationResult
{
    public required string PayloadName { get; init; }
    public required string FileName { get; init; }
    public required bool Valid { get; init; }
    public string? ErrorMessage { get; init; }
}
