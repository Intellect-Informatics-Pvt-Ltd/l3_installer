using FluentAssertions;
using Installer.Actions.Install;
using Installer.Actions.Topology;
using Installer.Core.Repair;
using Installer.Core.SiteConfig;
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
        _payloadConfig.Setup(p => p.RewriteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new PayloadConfigResult { Rewritten = new Dictionary<string, int>(), Skipped = [] });
    }

    private readonly Mock<ISiteConfigLoader> _siteLoader = new();
    private readonly Mock<IPayloadConfigRewriter> _payloadConfig = new();
    private readonly SiteTokenSource _siteTokens = new();

    private RepairEngine Build() => new(
        _verifier.Object, _serviceMap.Object, _payloads.Object, _binaries.Object,
        _config.Object, _services.Object, _payloadConfig.Object,
        _siteLoader.Object, _siteTokens,
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

    // ── Which PACS this is ───────────────────────────────────────────────────

    private static ServiceMapEntry StateSelectingEntry() => new()
    {
        Name = "l3_FAS", DisplayName = "FAS", Executable = "${BinaryRoot}/current/dotnet/dotnet",
        Arguments = "${BinaryRoot}/current/services/l3_FAS/FAS.dll", Account = "l2r2",
        StartOrder = 120, StopOrder = 80,
        HealthCheck = new ServiceHealthCheck { Type = "tcp", Host = "127.0.0.1", Port = "5010" },
        Recovery = new ServiceRecovery
        {
            FirstFailure = new RecoveryAction { Action = "restart", DelaySeconds = 30 },
            SecondFailure = new RecoveryAction { Action = "restart", DelaySeconds = 60 },
            Subsequent = new RecoveryAction { Action = "restart", DelaySeconds = 120 }
        },
        Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["ASPNETCORE_ENVIRONMENT"] = "${epcfg:state_code}" }
    };

    [Fact]
    public async Task Refuses_before_stopping_anything_when_the_map_needs_a_site_and_none_is_known()
    {
        // The generated L2-R2 topology puts ${epcfg:state_code} on all 27 application services.
        // Re-registering them under a defaulted state would run the wrong configuration without
        // failing - so the refusal comes BEFORE a service is stopped, and names the installed
        // copy the operator can restore.
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();
        _serviceMap.Setup(m => m.LoadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([StateSelectingEntry()]);

        var act = () => Build().RepairAsync(Request(dryRun: false));

        var ex = await act.Should().ThrowAsync<RepairException>();
        ex.Which.Message.Should().Contain("${epcfg:").And.Contain("site.epcfg").And.Contain("Nothing has been changed");
        _services.Verify(s => s.StopAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
        _services.Verify(s => s.RegisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reads_the_installed_site_pack_when_no_config_is_given_and_binds_it()
    {
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();
        var installed = Path.Combine(Opts.DataRoot, "config", "site.epcfg");
        File.WriteAllText(installed, "{}");
        _siteLoader.Setup(l => l.LoadAsync(installed, false, It.IsAny<CancellationToken>())).ReturnsAsync(Site);
        _serviceMap.Setup(m => m.LoadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([StateSelectingEntry()]);

        await Build().RepairAsync(Request(dryRun: false));

        _siteLoader.Verify(l => l.LoadAsync(installed, false, It.IsAny<CancellationToken>()), Times.Once);
        _siteTokens.Site.Should().NotBeNull();
        _siteTokens.Tokens["epcfg:state_code"].Should().Be("AP");
        _services.Verify(s => s.RegisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_config_on_the_command_line_wins_over_the_installed_copy()
    {
        GivenTheReleaseIsOnDisk();
        GivenConfigurationExists();
        File.WriteAllText(Path.Combine(Opts.DataRoot, "config", "site.epcfg"), "{}");

        await Build().RepairAsync(Request(dryRun: false, site: Site));

        _siteLoader.Verify(l => l.LoadAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _siteTokens.Tokens["epcfg:pacs_id"].Should().Be("AP-1");
    }
}
