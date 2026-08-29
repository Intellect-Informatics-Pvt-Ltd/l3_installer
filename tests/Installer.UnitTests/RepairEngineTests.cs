using FluentAssertions;
using Installer.Actions.Install;
using Installer.Actions.Topology;
using Installer.Core.Repair;
using ManifestVerifier;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// Repair — the third answer to "this node is wrong".
///
/// Upgrade moves version, restore replaces data, repair re-lays what the release owns and
/// touches no data at all. That boundary is what these mostly assert: repair is the one
/// operation an operator can run without a backup and without a decision, and it stays that way
/// only for as long as it cannot destroy anything.
/// </summary>
public sealed class RepairEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-repair-tests", Guid.NewGuid().ToString("N"));

    private readonly Mock<IManifestVerificationService> _verifier = new();
    private readonly Mock<IServiceMapLoader> _serviceMap = new();
    private readonly Mock<IPayloadExtractor> _payloads = new();
    private readonly Mock<IBinaryDeployer> _binaries = new();
    private readonly Mock<IConfigGenerator> _config = new();
    private readonly Mock<IServiceOrchestrator> _services = new();

    private InstallerOptions Opts => new() { DataRoot = Path.Combine(_root, "data"), BinaryRoot = Path.Combine(_root, "bin") };

    private static ReleaseManifest Manifest(string version = "3.3.0") => new()
    {
        Manifest = new ManifestMetadata
        {
            ManifestId = "rel", StackVersion = version, SchemaVersion = 25, MinOsBuild = 1,
            InstallerToolVersion = "4", SigningCertThumbprint = "A", CreatedAt = DateTimeOffset.UnixEpoch, CreatedBy = "t"
        },
        Payloads = [new PayloadEntry { Name = "p", File = "p.zip", Sha256 = "x", SizeBytes = 1, InstallOrder = 1, Required = true }],
        Compatibility = new CompatibilityInfo { MinUpgradeFrom = "3.2.0", MaxUpgradeFrom = "3.2.9", RequiresSideBySide = false }
    };

    public RepairEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ManifestVerificationResult { Valid = true, Manifest = Manifest() });
        _serviceMap.Setup(m => m.LoadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([]);
        _config.Setup(c => c.GenerateAllAsync(It.IsAny<SiteConfigPack>(), It.IsAny<string>(), It.IsAny<string>(),
                                              It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ConfigGenerationResult { GeneratedFiles = ["appsettings.json"], TokensResolved = 3 });
        _binaries.Setup(b => b.ResolveCurrent()).Returns(Path.Combine(_root, "bin", "releases", "3.3.0"));
    }

    private RepairEngine Build() => new(
        _verifier.Object, _serviceMap.Object, _payloads.Object, _binaries.Object,
        _config.Object, _services.Object,
        Options.Create(Opts), Options.Create(new ComponentsOptions()),
        NullLogger<RepairEngine>.Instance);

    private RepairRequest Request(bool dryRun = true, bool regen = false, bool replace = false, SiteConfigPack? site = null) => new()
    {
        MediaDirectory = Path.Combine(_root, "media"),
        DryRun = dryRun,
        RegenerateConfiguration = regen,
        ReplaceBinaries = replace,
        SiteConfig = site
    };

    private static SiteConfigPack Site => new()
    { Signature = "s", PacsId = "AP-1", StateCode = "AP", DataRoot = @"D:\ePACSData" };

    private void GivenConfigurationExists()
    {
        var dir = Path.Combine(Opts.DataRoot, "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
    }

    private void GivenTheReleaseIsOnDisk() =>
        Directory.CreateDirectory(Path.Combine(Opts.ReleasesPath, "3.3.0"));

    // ── The boundary: repair never touches data ──────────────────────────────

    [Fact]
    public async Task Says_plainly_that_data_was_not_touched()
    {
        // The safety argument in one sentence, and it has to reach the operator: repair is
        // runnable without a backup precisely because it cannot destroy anything.
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();

        var result = await Build().RepairAsync(Request(dryRun: false));

        result.Message.Should().Contain("database, attachments and logs were not touched");
    }

    // ── Diagnosis before change ──────────────────────────────────────────────

    [Fact]
    public async Task A_dry_run_diagnoses_everything_and_changes_nothing()
    {
        GivenConfigurationExists();

        var result = await Build().RepairAsync(Request(dryRun: true));

        result.Findings.Should().NotBeEmpty();
        result.Repaired.Should().BeEmpty();
        _services.Verify(s => s.StopAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
        _binaries.Verify(b => b.SwitchCurrentAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Notices_that_current_points_at_nothing()
    {
        _binaries.Setup(b => b.ResolveCurrent()).Returns((string?)null);
        GivenConfigurationExists();

        var result = await Build().RepairAsync(Request());

        result.Findings.Should().Contain(f => f.Area == RepairArea.CurrentLink && f.Severity == RepairSeverity.Broken);
    }

    [Fact]
    public async Task Notices_a_missing_release_directory()
    {
        GivenConfigurationExists();

        var result = await Build().RepairAsync(Request());

        result.Findings.Should().Contain(f => f.Area == RepairArea.Binaries && f.Severity == RepairSeverity.Broken);
    }

    [Fact]
    public async Task Notices_missing_configuration()
    {
        GivenTheReleaseIsOnDisk();

        var result = await Build().RepairAsync(Request());

        result.Findings.Should().Contain(f => f.Area == RepairArea.Configuration && f.Severity == RepairSeverity.Broken);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_media_that_does_not_verify()
    {
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ManifestVerificationResult.Failed("hash mismatch"));

        var act = () => Build().RepairAsync(Request(dryRun: false));

        await act.Should().ThrowAsync<RepairException>().WithMessage("*did not verify*");
        _services.Verify(s => s.StopAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refuses_a_medium_carrying_a_different_version()
    {
        // Changing version under the name "repair" would skip the backup and the migrations that
        // an upgrade takes. The message has to name the right tool.
        _binaries.Setup(b => b.ResolveCurrent()).Returns(Path.Combine(_root, "bin", "releases", "3.2.0"));

        var act = () => Build().RepairAsync(Request());

        var ex = await act.Should().ThrowAsync<RepairException>();
        ex.Which.Message.Should().Contain("3.2.0").And.Contain("upgrade");
    }

    [Fact]
    public async Task Refuses_to_regenerate_configuration_without_a_site_pack()
    {
        // Refused rather than skipped: a repair that leaves broken configuration in place and
        // reports success is worse than one that did not run.
        GivenTheReleaseIsOnDisk();

        var act = () => Build().RepairAsync(Request(dryRun: false));

        await act.Should().ThrowAsync<RepairException>().WithMessage("*no site configuration pack*");
    }

    // ── Doing the work ───────────────────────────────────────────────────────

    [Fact]
    public async Task Re_lays_binaries_when_the_release_is_missing()
    {
        GivenConfigurationExists();

        await Build().RepairAsync(Request(dryRun: false));

        _binaries.Verify(b => b.StageAsync(It.IsAny<string>(), "3.3.0", It.IsAny<CancellationToken>()), Times.Once);
        _binaries.Verify(b => b.SwitchCurrentAsync("3.3.0"), Times.Once);
    }

    [Fact]
    public async Task Leaves_intact_binaries_alone_unless_asked()
    {
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();

        await Build().RepairAsync(Request(dryRun: false));

        _binaries.Verify(b => b.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Re_lays_intact_binaries_when_asked()
    {
        // For a suspected quarantine or on-disk corruption, where "looks intact" is exactly what
        // cannot be trusted.
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();

        await Build().RepairAsync(Request(dryRun: false, replace: true));

        _binaries.Verify(b => b.StageAsync(It.IsAny<string>(), "3.3.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Always_re_registers_services()
    {
        // Idempotent and cheap, and a service whose binary path or environment has drifted is
        // invisible until it fails to start — the situation repair exists for.
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();

        await Build().RepairAsync(Request(dryRun: false));

        _services.Verify(s => s.RegisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Once);
        _services.Verify(s => s.StartAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Regenerating_configuration_is_reported_as_requested_not_broken()
    {
        // It discards hand edits, so the operator should see it listed as something they asked
        // for rather than something that was wrong.
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();

        var result = await Build().RepairAsync(Request(regen: true, site: Site));

        result.Findings.Should().Contain(f =>
            f.Area == RepairArea.Configuration && f.Severity == RepairSeverity.Requested);
        result.Findings.Single(f => f.Area == RepairArea.Configuration).Message.Should().Contain("hand edits");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
