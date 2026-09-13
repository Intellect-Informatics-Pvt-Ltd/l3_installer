using System.IO.Compression;
using System.Security.Cryptography;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// A medium on disk whose manifest hashes are MEASURED from the files beside it, with the three
/// control payloads the pipeline reads through <see cref="Installer.Core.Pipeline.VerifiedMedia"/>:
/// <c>config</c> (a service map, optionally sibling URLs and a classification),
/// <c>config-templates</c>, and <c>db</c> (the baseline schema, a single file). Tests that mock
/// the verifier still need the archives to be real, because the control payloads are re-hashed
/// at the moment of use (G35).
/// </summary>
internal static class MediumFixture
{
    public const string ServiceMapYaml = """
        services:
          - name: "svc1"
            display_name: "svc1"
            executable: "${BinaryRoot}/current/services/svc1/svc1.dll"
            account: "l2r2"
            start_order: 10
            stop_order: 10
            health_check: { type: "tcp", host: "127.0.0.1", port: "5001" }
            recovery:
              first_failure: { action: "restart", delay_seconds: 1 }
              second_failure: { action: "restart", delay_seconds: 1 }
              subsequent: { action: "restart", delay_seconds: 1 }
        """;

    /// <summary>Writes the medium and returns a manifest whose payload entries hash to what was written.</summary>
    public static ReleaseManifest Write(string mediaDir, string version = "3.2.1", bool withSiblingUrls = false, bool withClassification = false)
    {
        Directory.CreateDirectory(mediaDir);

        var config = Path.Combine(mediaDir, "_config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "service-map.yaml"), ServiceMapYaml);
        if (withSiblingUrls)
        {
            File.WriteAllText(Path.Combine(config, "sibling-urls.json"), """{ "services": {} }""");
        }

        if (withClassification)
        {
            File.WriteAllText(Path.Combine(config, "pacs-table-classification.json"), """{ "society": { "t": "PacsId" }, "master": [], "excluded": [] }""");
        }

        Zip(config, Path.Combine(mediaDir, "config.zip"));

        var templates = Path.Combine(mediaDir, "_templates");
        Directory.CreateDirectory(templates);
        File.WriteAllText(Path.Combine(templates, "appsettings.Site.template.json"), """{ "Site": { "PacsId": "${epcfg:pacs_id}" } }""");
        Zip(templates, Path.Combine(mediaDir, "config-templates.zip"));

        File.WriteAllText(Path.Combine(mediaDir, "stable_baseline_ddl.sql"), "-- baseline\nCREATE TABLE t (id INT);\n");

        File.WriteAllText(Path.Combine(mediaDir, "p.zip"), "not really a zip, never extracted by these tests");

        return new ReleaseManifest
        {
            Manifest = new ManifestMetadata
            {
                ManifestId = "rel-test", StackVersion = version, SchemaVersion = 25, MinOsBuild = 17763,
                InstallerToolVersion = "4.0.0", SigningCertThumbprint = "AA", CreatedAt = DateTimeOffset.UnixEpoch,
                CreatedBy = "test"
            },
            Payloads =
            [
                Entry("config", "config.zip", mediaDir, 1),
                Entry("config-templates", "config-templates.zip", mediaDir, 2),
                Entry("db", "stable_baseline_ddl.sql", mediaDir, 3),
                Entry("p", "p.zip", mediaDir, 10)
            ],
            Compatibility = new CompatibilityInfo { MinUpgradeFrom = "3.1.0", MaxUpgradeFrom = "3.2.0", RequiresSideBySide = false }
        };
    }

    private static PayloadEntry Entry(string name, string file, string mediaDir, int order)
    {
        var path = Path.Combine(mediaDir, file);
        using var stream = File.OpenRead(path);
        return new PayloadEntry
        {
            Name = name, File = file, Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            SizeBytes = new FileInfo(path).Length, InstallOrder = order, Required = true
        };
    }

    private static void Zip(string sourceDir, string zipPath)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(sourceDir, recursive: true);
    }
}
