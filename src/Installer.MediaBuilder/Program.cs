using System.Security.Cryptography.X509Certificates;
using SharedKernel.Security;

namespace Installer.MediaBuilder;

/// <summary>
/// <c>epacs-media</c> — builds and verifies a release medium.
///
/// The gap this closes: until now nothing in the product turned published binaries into
/// something an operator could carry. `samples/release-manifest.yaml` listed payloads with
/// hand-typed hashes for archives no build produced, so the verification path — the strongest
/// code in the repository — had never been pointed at a real medium.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "build" => await BuildAsync(args),
                "verify" => await VerifyAsync(args),
                _ => Fail($"unknown command '{args[0]}'")
            };
        }
        catch (MediaBuildException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
#pragma warning disable CA1031 // top of the process
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"unexpected error: {ex.Message}");
            return 99;
        }
    }

    private static async Task<int> BuildAsync(string[] args)
    {
        var spec = MediaAssembler.LoadSpec(Arg(args, "--spec") ?? throw new MediaBuildException("--spec is required"));
        var output = Arg(args, "--out") ?? throw new MediaBuildException("--out is required");
        var groups = (Arg(args, "--groups") ?? "core,cache").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unsigned = args.Contains("--unsigned");
        var pfx = Arg(args, "--pfx");

        ICodeSigner? signer = null;
        X509Certificate2? certificate = null;
        if (!unsigned)
        {
            if (pfx is null)
            {
                // Refuses rather than quietly producing an unsigned medium. The failure mode
                // being prevented is a release build that silently loses its signature because
                // a CI secret was not wired, and nobody notices until a node rejects it — or
                // worse, until nobody notices at all.
                throw new MediaBuildException(
                    "No signing certificate given. Pass --pfx <path> (with EPACS_PFX_PASSWORD set), or --unsigned " +
                    "for a development medium. A medium without a manifest signature has no tamper-evidence: detached " +
                    "CMS over the manifest is the only mechanism in force, since Authenticode is unimplemented (ADR-0001).");
            }

            var password = Environment.GetEnvironmentVariable("EPACS_PFX_PASSWORD");

            // Loaded once. The signer does not dispose what the provider returns (the provider
            // owns the lifetime), so handing back a fresh instance per call would leak one
            // certificate handle per signature.
            certificate = X509CertificateLoader.LoadPkcs12FromFile(pfx, password, KeyStorageFlags);
            signer = new CmsCodeSigner(() => certificate);
        }

        MediaBuildResult result;
        try
        {
            result = await new MediaAssembler(Console.Out).BuildAsync(spec, output, groups, signer);
        }
        finally
        {
            certificate?.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"OK  {result.Payloads.Count} payload(s), {result.TotalBytes / (1024.0 * 1024 * 1024):F2} GB, " +
                          $"{(result.IsSigned ? "signed" : "UNSIGNED — not releasable")}");
        return result.IsSigned ? 0 : 2;
    }

    private static async Task<int> VerifyAsync(string[] args)
    {
        var media = Arg(args, "--media") ?? throw new MediaBuildException("--media is required");
        var thumbprint = Arg(args, "--thumbprint");

        var result = await MediaAssembler.VerifyAsync(media, thumbprint);

        if (!result.Valid)
        {
            // Verdict first. Listing "ok" against individual payloads above a failed signature
            // reads as partial success, and there is no such thing here: if the manifest is not
            // trustworthy then neither is any hash it declares, however well those hashes match.
            Console.Error.WriteLine("FAILED  " + string.Join("; ", result.Errors));

            var failed = result.PayloadResults.Where(p => !p.Valid).ToList();
            foreach (var p in failed)
            {
                Console.Error.WriteLine($"  FAIL  {p.PayloadName,-24} {p.ErrorMessage}");
            }

            if (failed.Count == 0 && result.PayloadResults.Count > 0)
            {
                Console.Error.WriteLine(
                    $"  ({result.PayloadResults.Count} payload hash(es) did match — but a manifest that does not " +
                    "verify cannot vouch for them.)");
            }

            return 1;
        }

        foreach (var p in result.PayloadResults)
        {
            Console.WriteLine($"  ok    {p.PayloadName}");
        }

        Console.WriteLine($"OK  {result.PayloadResults.Count} payload(s) verified against the manifest");
        return 0;
    }

    /// <summary>
    /// How the signing key is held while the manifest is signed.
    ///
    /// <c>EphemeralKeySet</c> keeps the private key in memory and never writes it to the user's
    /// key store or to disk — which is what you want on a shared CI runner, where a key
    /// persisted by one job is a key available to the next. macOS does not support the flag, so
    /// it is applied where it works rather than dropped everywhere: a developer signing a test
    /// medium on a Mac is a different threat model from a release runner.
    /// </summary>
    private static X509KeyStorageFlags KeyStorageFlags =>
        OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine(Usage);
        return 64;
    }

    private const string Usage = """
        epacs-media — build and verify an ePACS release medium

        Usage:
          epacs-media build  --spec <media-spec.yaml> --out <dir> [--groups core,cache] (--pfx <p12> | --unsigned)
          epacs-media verify --media <dir> [--thumbprint <sha1>]

        build
          --spec        The medium's composition. A file, not flags, so "why does this build
                        carry Kafka?" is answerable from git history.
          --out         Output directory. CLEANED first — a medium assembled over a previous
                        build can carry a payload the manifest no longer lists.
          --groups      Component groups to include (default: core,cache). Payloads outside the
                        requested groups are left out and reported, never silently dropped.
          --pfx         PKCS#12 with the signing key. Password from EPACS_PFX_PASSWORD.
          --unsigned    Build without a signature. Development only; exits 2 to make it hard to
                        mistake an unsigned medium for a releasable one in a pipeline.

        verify
          --media       An assembled medium. Runs the INSTALLER'S OWN verifier, so a pass here
                        means the same thing a pass on the node means. Worth running again after
                        the medium is copied to a stick — that is where truncation happens.
          --thumbprint  Require this signer. A validly-signed medium from the wrong signer is
                        the case this exists for.

        Exit codes:
          0   verified
          1   failed
          2   built but UNSIGNED — not releasable
          64  usage
          99  unexpected
        """;
}
