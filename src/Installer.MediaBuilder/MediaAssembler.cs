using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ManifestVerifier;
using SharedKernel.Security;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Installer.MediaBuilder;

/// <summary>
/// Assembles a release medium: stage the payloads, hash them, write the manifest, sign it, and
/// then <b>verify the result with the installer's own verifier</b> before declaring success.
///
/// ── THE ONE RULE THIS TOOL EXISTS TO ENFORCE ────────────────────────────────────────────────
///
/// A medium is not built until it verifies. The last step runs
/// <c>ManifestVerificationService</c> — the same class the installer runs on the node, not a
/// reimplementation — over the assembled directory. So "the build passed" and "the installer
/// will accept this" are the same statement, and a hash or signature mistake is caught on a CI
/// runner instead of on a machine in a village with no network and one operator.
///
/// Everything the release manifest previously asserted was invented:
/// <c>samples/release-manifest.yaml</c> carries hand-typed SHA-256 values and round-number
/// sizes for payloads no build produces. This replaces that with measurement.
/// </summary>
public sealed class MediaAssembler
{
    private readonly TextWriter _out;

    public MediaAssembler(TextWriter output) => _out = output;

    public async Task<MediaBuildResult> BuildAsync(
        MediaSpec spec,
        string outputDirectory,
        IReadOnlyCollection<string> groups,
        ICodeSigner? signer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var specDir = spec.SpecDirectory ?? Directory.GetCurrentDirectory();
        var wanted = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

        // Clean, not merge. A medium assembled on top of a previous build can carry a payload
        // the manifest no longer lists — which verifies fine and installs something nobody
        // intended, because verification checks that listed payloads are present and correct,
        // not that present payloads are listed.
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
        Directory.CreateDirectory(outputDirectory);

        var included = spec.Payloads
            .Where(p => wanted.Contains(p.Group))
            .OrderBy(p => p.InstallOrder)
            .ToList();

        var excluded = spec.Payloads.Except(included).ToList();

        if (included.Count == 0)
        {
            throw new MediaBuildException(
                $"No payload matched the requested groups [{string.Join(", ", groups)}]. A medium with no payloads " +
                "would verify successfully and install nothing.");
        }

        _out.WriteLine($"Assembling {included.Count} payload(s) into {outputDirectory}");

        var entries = new List<PayloadRecord>();
        foreach (var payload in included)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await StagePayloadAsync(payload, specDir, outputDirectory, cancellationToken));
        }

        foreach (var skipped in excluded)
        {
            // Stated, never silent. A payload dropped without a word is how a medium reaches a
            // site missing the component somebody assumed was on it.
            _out.WriteLine($"  excluded  {skipped.Name} (group '{skipped.Group}' not requested)");
        }

        var manifestPath = Path.Combine(outputDirectory, "release-manifest.yaml");
        await File.WriteAllTextAsync(manifestPath, RenderManifest(spec, entries), cancellationToken);
        _out.WriteLine($"  manifest  {Path.GetFileName(manifestPath)}");

        string? signaturePath = null;
        string? signerThumbprint = null;
        if (signer is not null)
        {
            signaturePath = manifestPath + ".sig";
            await signer.SignFileAsync(manifestPath, signaturePath, cancellationToken);

            // Read WHO signed it, which is a different question from whether the signature is
            // trusted. The trust answer comes from the verification pass below, pinned to this
            // signer — exactly as an air-gapped node does it, and deliberately not by consulting
            // the build machine's trust store, which a node cannot reproduce.
            var (subject, thumbprint) = ReadSigner(signaturePath);
            signerThumbprint = thumbprint;
            _out.WriteLine($"  signed    {Path.GetFileName(signaturePath)}  by {subject}");
            _out.WriteLine($"            thumbprint {thumbprint}");
            _out.WriteLine("            → pin this into the installer's Installer:ExpectedSigningThumbprint");
        }
        else
        {
            _out.WriteLine("  UNSIGNED  no signing certificate supplied — this medium has no tamper-evidence");
        }

        // Pin to the signer we actually used, not to whatever the spec declared: the spec's
        // thumbprint is an assertion about intent, and this step is a measurement of fact.
        var verification = await VerifyAsync(outputDirectory, signerThumbprint, cancellationToken);
        if (!verification.Valid)
        {
            throw new MediaBuildException(
                "The medium was assembled but does not verify: " + string.Join("; ", verification.Errors) +
                ". It has not been declared built. This check runs the installer's own verifier, so a failure here " +
                "is a failure the node would have had.");
        }

        var totalBytes = entries.Sum(e => e.SizeBytes);
        _out.WriteLine($"  verified  {entries.Count} payload(s), {totalBytes / (1024.0 * 1024 * 1024):F2} GB");

        return new MediaBuildResult
        {
            OutputDirectory = outputDirectory,
            ManifestPath = manifestPath,
            SignaturePath = signaturePath,
            Payloads = entries,
            TotalBytes = totalBytes,
            IsSigned = signaturePath is not null
        };
    }

    /// <summary>
    /// Verifies an assembled medium using the installer's own verification service.
    /// Also exposed as the <c>verify</c> verb, so a medium can be re-checked after it has been
    /// copied to a stick — which is where bit-rot and truncated copies actually happen.
    /// </summary>
    public static async Task<ManifestVerificationResult> VerifyAsync(
        string mediaDirectory, string? expectedThumbprint, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(mediaDirectory, "release-manifest.yaml");
        var signaturePath = manifestPath + ".sig";

        var service = new ManifestVerificationService(
            new ManifestParser(), new SignatureVerifier(), new HashVerifier());

        return await service.VerifyAsync(
            manifestPath,
            mediaDirectory,
            File.Exists(signaturePath) ? signaturePath : null,
            expectedThumbprint,
            cancellationToken);
    }

    private async Task<PayloadRecord> StagePayloadAsync(
        MediaSpecPayload payload, string specDir, string outputDirectory, CancellationToken ct)
    {
        var source = Path.GetFullPath(Path.Combine(specDir, payload.Source));
        string fileName;
        string destination;

        if (Directory.Exists(source))
        {
            // A directory becomes one archive. Not because compression matters much — most of
            // this is already-compressed binaries — but because the manifest hashes FILES, and a
            // directory that is copied loose has no single hash to record. One archive, one
            // hash, one thing to verify.
            fileName = $"{payload.Name}.zip";
            destination = Path.Combine(outputDirectory, fileName);
            await Task.Run(() => ZipFile.CreateFromDirectory(source, destination, CompressionLevel.Optimal, includeBaseDirectory: false), ct);
        }
        else if (File.Exists(source))
        {
            fileName = Path.GetFileName(source);
            destination = Path.Combine(outputDirectory, fileName);
            File.Copy(source, destination, overwrite: true);
        }
        else
        {
            throw new MediaBuildException(
                $"Payload '{payload.Name}' points at {source}, which does not exist. Publish it before building the " +
                "medium; the builder will not emit a manifest entry for something it cannot hash.");
        }

        var sha = await ComputeSha256Async(destination, ct);
        var size = new FileInfo(destination).Length;

        _out.WriteLine($"  payload   {payload.Name,-24} {size / (1024.0 * 1024):F1} MB  {sha[..16]}…");

        return new PayloadRecord
        {
            Name = payload.Name,
            File = fileName,
            Sha256 = sha,
            SizeBytes = size,
            InstallOrder = payload.InstallOrder,
            Required = payload.Required,
            Group = payload.Group
        };
    }

    /// <summary>
    /// Reads the signer's identity out of a detached signature without validating trust.
    ///
    /// "Who signed this?" and "should I trust it?" are separate questions, and conflating them
    /// is what stopped the builder reporting its own signer: the trust answer needs the signing
    /// chain in a trust store, which is exactly what an air-gapped node does not have — and what
    /// a build runner having it would prove nothing about.
    /// </summary>
    private static (string Subject, string Thumbprint) ReadSigner(string signaturePath)
    {
        var signedCms = new System.Security.Cryptography.Pkcs.SignedCms();
        signedCms.Decode(File.ReadAllBytes(signaturePath));

        var cert = signedCms.SignerInfos[0].Certificate
            ?? throw new MediaBuildException(
                "The signature carries no signer certificate, so the medium cannot declare who produced it. " +
                "Sign with a certificate whose chain is embedded (X509IncludeOption.WholeChain).");

        return (cert.Subject, cert.Thumbprint);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Renders the manifest.
    ///
    /// Written by hand rather than serialised, for two reasons that outlast convenience: the
    /// output is byte-reproducible for the same inputs (a serialiser's key order and quoting can
    /// change between library versions, and this file is *signed* — a reordered manifest is a
    /// broken signature), and it can carry comments explaining itself to whoever opens it on a
    /// stick in five years.
    /// </summary>
    private static string RenderManifest(MediaSpec spec, IReadOnlyList<PayloadRecord> payloads)
    {
        var c = CultureInfo.InvariantCulture;
        var r = spec.Release;
        var sb = new StringBuilder();

        sb.AppendLine("# ePACS Release Manifest — GENERATED. Do not edit.");
        sb.AppendLine("#");
        sb.AppendLine("# Every hash below was MEASURED from the file beside it, not typed. The installer verifies");
        sb.AppendLine("# each payload against these values before extracting anything, and this manifest is itself");
        sb.AppendLine("# covered by release-manifest.yaml.sig — detached CMS, which is the only tamper-evidence in");
        sb.AppendLine("# force (Authenticode verification is unimplemented; see ADR-0001).");
        sb.AppendLine();

        sb.AppendLine("manifest:");
        sb.AppendLine(c, $"  manifest_id: \"rel-{r.StackVersion}\"");
        sb.AppendLine(c, $"  stack_version: \"{r.StackVersion}\"");
        sb.AppendLine(c, $"  schema_version: {r.SchemaVersion}");
        sb.AppendLine(c, $"  min_os_build: {r.MinOsBuild}");
        sb.AppendLine(c, $"  installer_tool_version: \"{r.InstallerToolVersion}\"");
        sb.AppendLine(c, $"  signing_cert_thumbprint: \"{r.SigningCertThumbprint ?? ""}\"");
        // No timestamp. A build must be byte-reproducible for the same inputs, so that two runs
        // of the same release produce the same manifest and the same signature — otherwise
        // "did this medium change?" cannot be answered by comparing hashes.
        sb.AppendLine("  created_at: \"1970-01-01T00:00:00Z\"");
        sb.AppendLine(c, $"  created_by: \"{r.CreatedBy}\"");
        sb.AppendLine(c, $"  hotfix_base_version: {(r.HotfixBaseVersion is null ? "null" : $"\"{r.HotfixBaseVersion}\"")}");
        sb.AppendLine();

        sb.AppendLine("payloads:");
        foreach (var p in payloads)
        {
            sb.AppendLine(c, $"  - name: \"{p.Name}\"");
            sb.AppendLine(c, $"    file: \"{p.File}\"");
            sb.AppendLine(c, $"    sha256: \"{p.Sha256}\"");
            sb.AppendLine(c, $"    size_bytes: {p.SizeBytes}");
            sb.AppendLine(c, $"    install_order: {p.InstallOrder}");
            sb.AppendLine(c, $"    required: {p.Required.ToString().ToLowerInvariant()}");
            sb.AppendLine(c, $"    group: \"{p.Group}\"");
        }
        sb.AppendLine();

        var compat = spec.Compatibility ?? new MediaSpecCompatibility();
        sb.AppendLine("compatibility:");
        sb.AppendLine(c, $"  min_upgrade_from: \"{compat.MinUpgradeFrom}\"");
        sb.AppendLine(c, $"  max_upgrade_from: \"{compat.MaxUpgradeFrom}\"");
        sb.AppendLine(c, $"  requires_side_by_side: {compat.RequiresSideBySide.ToString().ToLowerInvariant()}");
        sb.AppendLine(c, $"  breaking_schema_change: {compat.BreakingSchemaChange.ToString().ToLowerInvariant()}");

        return sb.ToString();
    }

    public static MediaSpec LoadSpec(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new MediaBuildException($"Media spec not found: {path}");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var spec = deserializer.Deserialize<MediaSpec>(File.ReadAllText(path))
            ?? throw new MediaBuildException($"Media spec {path} is empty.");

        spec.SpecDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        return spec;
    }
}

public sealed record PayloadRecord
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required int InstallOrder { get; init; }
    public required bool Required { get; init; }
    public required string Group { get; init; }
}

public sealed record MediaBuildResult
{
    public required string OutputDirectory { get; init; }
    public required string ManifestPath { get; init; }
    public string? SignaturePath { get; init; }
    public required IReadOnlyList<PayloadRecord> Payloads { get; init; }
    public required long TotalBytes { get; init; }
    public required bool IsSigned { get; init; }
}

public sealed class MediaBuildException : Exception
{
    public MediaBuildException(string message) : base(message) { }
    public MediaBuildException(string message, Exception inner) : base(message, inner) { }
    public MediaBuildException() { }
}
