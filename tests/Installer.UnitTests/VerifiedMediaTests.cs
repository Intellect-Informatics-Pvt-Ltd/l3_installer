using FluentAssertions;
using Installer.Core.Pipeline;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// G35: the control files - map, templates, schema - come from payloads the manifest verified,
/// re-hashed at the moment of use, never loose from the stick.
/// </summary>
public sealed class VerifiedMediaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-verified-media-tests", Guid.NewGuid().ToString("N"));
    private string Media => Path.Combine(_root, "media");
    private string Staging => Path.Combine(_root, "staging");

    [Fact]
    public async Task Control_payloads_are_extracted_and_resolve_by_name()
    {
        var manifest = MediumFixture.Write(Media, withSiblingUrls: true, withClassification: true);

        var media = await VerifiedMedia.OpenAsync(manifest, Media, Staging);

        media.Present.Should().BeEquivalentTo(["config", "config-templates", "db"]);
        File.ReadAllText(media.Resolve(VerifiedMedia.ConfigPayload, "service-map.yaml", "the map")).Should().Contain("svc1");
        File.Exists(media.Resolve(VerifiedMedia.SchemaPayload, "stable_baseline_ddl.sql", "the schema")).Should().BeTrue("a single-file payload lands under its payload name");
        Directory.GetFiles(media.PayloadDirectory(VerifiedMedia.TemplatesPayload, "templates")).Should().ContainSingle(f => f.EndsWith(".template.json"));
        media.TryResolve(VerifiedMedia.ConfigPayload, "sibling-urls.json").Should().NotBeNull();
        media.TryResolve(VerifiedMedia.ConfigPayload, "pacs-table-classification.json").Should().NotBeNull();
        media.TryResolve(VerifiedMedia.ConfigPayload, "nope.json").Should().BeNull();
    }

    [Fact]
    public async Task A_loose_file_beside_the_manifest_is_never_read()
    {
        var manifest = MediumFixture.Write(Media);
        // An attacker drops a service map on the stick, outside any payload.
        Directory.CreateDirectory(Path.Combine(Media, "config"));
        File.WriteAllText(Path.Combine(Media, "config", "service-map.yaml"), "services: [ { name: evil } ]");

        var media = await VerifiedMedia.OpenAsync(manifest, Media, Staging);

        File.ReadAllText(media.Resolve(VerifiedMedia.ConfigPayload, "service-map.yaml", "the map")).Should().NotContain("evil");
    }

    [Fact]
    public async Task A_control_payload_altered_after_the_manifest_was_built_is_refused_naming_it()
    {
        var manifest = MediumFixture.Write(Media);
        File.AppendAllText(Path.Combine(Media, "stable_baseline_ddl.sql"), "DROP TABLE t;\n");

        var act = () => VerifiedMedia.OpenAsync(manifest, Media, Staging);

        await act.Should().ThrowAsync<VerifiedMediaException>().WithMessage("*'db'*does not hash*altered or truncated*");
    }

    [Fact]
    public async Task A_missing_control_payload_is_a_refusal_at_the_point_of_use_not_a_fallback_to_the_stick()
    {
        var manifest = MediumFixture.Write(Media);
        var trimmed = manifest with { Payloads = manifest.Payloads.Where(p => p.Name != "db").ToList() };
        Directory.CreateDirectory(Path.Combine(Media, "db"));
        File.WriteAllText(Path.Combine(Media, "db", "stable_baseline_ddl.sql"), "-- loose, unverified");

        var media = await VerifiedMedia.OpenAsync(trimmed, Media, Staging);

        media.Present.Should().NotContain("db");
        var act = () => media.Resolve(VerifiedMedia.SchemaPayload, "stable_baseline_ddl.sql", "the baseline schema");
        act.Should().Throw<VerifiedMediaException>().WithMessage("*no 'db' payload*Loose files beside the manifest are not trusted*");
    }

    [Fact]
    public async Task A_listed_control_payload_that_is_not_on_the_stick_is_refused()
    {
        var manifest = MediumFixture.Write(Media);
        File.Delete(Path.Combine(Media, "config.zip"));

        var act = () => VerifiedMedia.OpenAsync(manifest, Media, Staging);

        await act.Should().ThrowAsync<VerifiedMediaException>().WithMessage("*'config'*not on the medium*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
