using System.IO.Compression;
using System.Security.Cryptography;
using SharedKernel.Contracts;

namespace Installer.Core.Pipeline;

/// <summary>
/// The control files of a medium — the service map, the sibling-URL table, the table
/// classification, the configuration templates, the baseline schema — read ONLY from payloads
/// the manifest verified, never loose from the stick.
///
/// THE HOLE THIS CLOSES (G35, found 2026-09-13). The manifest hashes and signs payloads. The
/// pipeline read <c>config/service-map.yaml</c>, <c>config-templates/</c> and
/// <c>db/stable_baseline_ddl.sql</c> straight from the medium directory, and none of those was a
/// payload — so an attacker who could not re-sign the manifest could still swap the schema every
/// service binds to, or the map that decides what runs as what, and the "only tamper-evidence in
/// force" would have said nothing. Now a medium carries three control payloads by name —
/// <c>config</c>, <c>config-templates</c>, <c>db</c> — each hashed in the manifest; they are
/// re-hashed here against the manifest before extraction into a root-only staging directory,
/// and every control path resolves through it. A loose file with the same name on the medium is
/// ignored; a control payload the manifest does not carry is a refusal that names it.
/// </summary>
public sealed class VerifiedMedia
{
    public const string ConfigPayload = "config";
    public const string TemplatesPayload = "config-templates";
    public const string SchemaPayload = "db";

    private static readonly string[] ControlPayloads = [ConfigPayload, TemplatesPayload, SchemaPayload];

    private VerifiedMedia(string root, IReadOnlySet<string> present)
    {
        Root = root;
        Present = present;
    }

    /// <summary>Where the control payloads were extracted: <c>&lt;root&gt;/config</c>, <c>/config-templates</c>, <c>/db</c>.</summary>
    public string Root { get; }

    public IReadOnlySet<string> Present { get; }

    /// <summary>
    /// Extracts the control payloads the manifest carries, re-hashing each archive against the
    /// manifest first. A control payload that is absent is simply not present — the caller's
    /// resolve says so when it is needed, with the payload named.
    /// </summary>
    public static async Task<VerifiedMedia> OpenAsync(ReleaseManifest manifest, string mediaDirectory, string stagingRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }

        Directory.CreateDirectory(stagingRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stagingRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in manifest.Payloads.Where(p => ControlPayloads.Contains(p.Name, StringComparer.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            var archive = Path.Combine(mediaDirectory, payload.File);
            if (!File.Exists(archive))
            {
                throw new VerifiedMediaException($"Control payload '{payload.Name}' is listed in the manifest but {payload.File} is not on the medium.");
            }

            // Re-hashed here, at the moment of use, not trusted from the earlier pass: the stick
            // is removable and a copy can be truncated between verification and extraction.
            await using (var stream = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                if (!string.Equals(actual, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new VerifiedMediaException($"Control payload '{payload.Name}' ({payload.File}) does not hash to what the manifest recorded; the medium was altered or truncated after it was built.");
                }
            }

            var target = Path.Combine(stagingRoot, payload.Name);
            Directory.CreateDirectory(target);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true), ct);
            }
            else
            {
                // A single-file payload (the baseline schema is one): placed under its payload name.
                File.Copy(archive, Path.Combine(target, Path.GetFileName(archive)), overwrite: true);
            }

            present.Add(payload.Name);
        }

        return new VerifiedMedia(stagingRoot, present);
    }

    /// <summary>A path inside a control payload, or a refusal naming the payload the medium lacks.</summary>
    public string Resolve(string payloadName, string relativePath, string purpose)
    {
        if (!Present.Contains(payloadName))
        {
            throw new VerifiedMediaException(
                $"The medium carries no '{payloadName}' payload, so {purpose} ({relativePath}) cannot be read from anything the manifest verified. " +
                "Loose files beside the manifest are not trusted. Build the medium with the control payloads (see topology/media-spec.l2r2.yaml).");
        }

        return Path.Combine(Root, payloadName, relativePath);
    }

    /// <summary>The whole payload directory, for a consumer that enumerates it (the config generator's template directory).</summary>
    public string PayloadDirectory(string payloadName, string purpose) => Resolve(payloadName, "", purpose).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>The path if the payload is present, else null — for optional control files (sibling URLs, classification).</summary>
    public string? TryResolve(string payloadName, string relativePath)
    {
        if (!Present.Contains(payloadName))
        {
            return null;
        }

        var path = Path.Combine(Root, payloadName, relativePath);
        return File.Exists(path) ? path : null;
    }
}

public sealed class VerifiedMediaException : Exception
{
    public VerifiedMediaException(string message) : base(message) { }
}
