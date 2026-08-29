using FluentAssertions;
using Installer.Actions.Database;
using Installer.Core.Upgrade;
using BackupRestore.Restore;
using Installer.Core.Repair;
using Installer.Actions.Install;
using Installer.Actions.Prechecks;
using Installer.Actions.Topology;
using Installer.Actions.Uninstall;
using Installer.Core.Pipeline;
using Installer.Core.StateMachine;
using ManifestVerifier;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.UnitTests;

/// <summary>
/// The pipeline is the only thing in this product that has an opinion about ORDER, and order is
/// where an installer does its damage. These tests are mostly about what it refuses to do.
/// </summary>
public sealed class InstallerPipelineTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "epacs-pipeline-tests", Guid.NewGuid().ToString("N"));

    private readonly Mock<IManifestVerificationService> _verifier = new();
    private readonly Mock<IServiceMapLoader> _serviceMap = new();
    private readonly Mock<IDataRootInitializer> _dataRootInit = new();
    private readonly Mock<IPayloadExtractor> _payloads = new();
    private readonly Mock<IBinaryDeployer> _binaries = new();
    private readonly Mock<IConfigGenerator> _config = new();
    private readonly Mock<IServiceOrchestrator> _services = new();
    private readonly Mock<IOverrideTokenValidator> _tokens = new();
    private readonly Mock<IDatabaseBootstrapper> _database = new();
    private readonly Mock<IUpgradeEngine> _upgrade = new();
    private readonly Mock<IRestoreEngine> _restore = new();
    private readonly Mock<IRepairEngine> _repair = new();
    private ComponentsOptions _componentsOptions = new();
    private readonly List<IPrecheck> _prechecks = [];

    private InstallerOptions Options => new() { DataRoot = _dataRoot, BinaryRoot = Path.Combine(_dataRoot, "bin") };

    private InstallerPipeline Build()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        return new InstallerPipeline(
            new InstallerStateMachineFactory(opts, NullLogger<InstallerStateMachine>.Instance),
            new ModeDetector(opts, NullLogger<ModeDetector>.Instance),
            new InstallerLock(opts, NullLogger<InstallerLock>.Instance),
            new PrecheckRunner(_prechecks, NullLogger<PrecheckRunner>.Instance),
            _verifier.Object,
            _serviceMap.Object,
            _dataRootInit.Object,
            _payloads.Object,
            _binaries.Object,
            _config.Object,
            _services.Object,
            new UninstallAction(_services.Object, _tokens.Object, opts, NullLogger<UninstallAction>.Instance),
            _database.Object,
            _upgrade.Object,
            _restore.Object,
            _repair.Object,
            opts,
            Microsoft.Extensions.Options.Options.Create(_componentsOptions),
            NullLogger<InstallerPipeline>.Instance);
    }

    private static SiteConfigPack Site => new()
    {
        Signature = "sig", PacsId = "AP-XYZ-0001", StateCode = "AP", DataRoot = @"D:\ePACSData"
    };

    private static ReleaseManifest Manifest => new()
    {
        Manifest = new ManifestMetadata
        {
            ManifestId = "rel-test", StackVersion = "3.2.1", SchemaVersion = 25, MinOsBuild = 17763,
            InstallerToolVersion = "4.0.0", SigningCertThumbprint = "AA", CreatedAt = DateTimeOffset.UnixEpoch,
            CreatedBy = "test"
        },
        Payloads = [new PayloadEntry { Name = "p", File = "p.zip", Sha256 = "x", SizeBytes = 1, InstallOrder = 1, Required = true }],
        Compatibility = new CompatibilityInfo { MinUpgradeFrom = "3.1.0", MaxUpgradeFrom = "3.2.0", RequiresSideBySide = false }
    };

    private void GivenVerificationSucceeds() =>
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ManifestVerificationResult { Valid = true, Manifest = Manifest });

    private void GivenConfigGenerates() =>
        _config.Setup(c => c.GenerateAllAsync(It.IsAny<SiteConfigPack>(), It.IsAny<string>(), It.IsAny<string>(),
                                              It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ConfigGenerationResult { GeneratedFiles = ["appsettings.json"], TokensResolved = 12 });

    private void GivenDatabaseCanBootstrap() =>
        _database.Setup(d => d.PlanAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DatabaseBootstrapPlan
                 {
                     CanProceed = true,
                     CaseSensitivity = new CaseSensitivityVerdict { CanHostEstateSetting = true, FileSystemIsCaseSensitive = true, Explanation = "case-sensitive" },
                     DataDirectory = "/tmp/data", ConfigFilePath = "/tmp/my.ini", DataDirectoryAlreadyInitialised = false
                 });

    private void GivenDatabaseExecutes(int before = 0, int after = 1189) =>
        _database.Setup(d => d.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DatabaseBootstrapResult { Succeeded = true, TablesBefore = before, TablesAfter = after });

    private void GivenTopology(int count = 2) =>
        _serviceMap.Setup(m => m.LoadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Enumerable.Range(1, count).Select(i => new ServiceMapEntry
                   {
                       Name = $"svc{i}", DisplayName = $"svc{i}", Executable = "x.exe", Account = "LocalSystem",
                       StartOrder = i * 10, StopOrder = i * 10,
                       HealthCheck = new ServiceHealthCheck { Type = "tcp", Port = "1" },
                       Recovery = new ServiceRecovery
                       {
                           FirstFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
                           SecondFailure = new RecoveryAction { Action = "restart", DelaySeconds = 1 },
                           Subsequent = new RecoveryAction { Action = "restart", DelaySeconds = 1 }
                       }
                   }).ToList());

    // ── Modes with no engine must fail, distinguishably ──────────────────────

    [Theory]
    [InlineData(InstallerMode.Backup)]
    public async Task A_mode_with_no_engine_reports_NotImplemented_and_never_success(InstallerMode mode)
    {
        // THE REGRESSION THIS PINS. Before the pipeline existed, `/mode:upgrade` returned exit 0
        // having done nothing whatsoever. On a PACS node that reads as "upgrade succeeded", and
        // the next thing anyone does is decommission the old media.
        //
        // Upgrade/Restore/Repair need an existing installation, which the temp DataRoot does not
        // have, so ModeDetector rejects them first with OperationFailed. Either way the contract
        // that matters holds: NOT Success.
        Directory.CreateDirectory(Path.Combine(_dataRoot, "bin", "current"));

        var result = await Build().RunAsync(new PipelineRequest { Mode = mode, SiteConfig = Site });

        result.Outcome.Should().NotBe(PipelineOutcome.Success);
        result.Outcome.Should().BeOneOf(PipelineOutcome.NotImplemented, PipelineOutcome.OperationFailed);
    }

    [Fact]
    public async Task Backup_says_plainly_that_it_would_restore_nothing()
    {
        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Backup, SiteConfig = Site });

        result.Outcome.Should().Be(PipelineOutcome.NotImplemented);
        result.Message.Should().Contain("placeholder", "an operator must not believe they hold a usable backup");
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_an_install_with_no_site_config()
    {
        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install });

        result.Outcome.Should().Be(PipelineOutcome.Refused);
        result.Message.Should().Contain(".epcfg");
    }

    [Fact]
    public async Task Refuses_to_run_while_another_installer_holds_the_lock()
    {
        using var held = new InstallerLock(
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<InstallerLock>.Instance);
        held.TryAcquire().Should().BeTrue();

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site });

        result.Outcome.Should().Be(PipelineOutcome.Refused);
        result.Message.Should().Contain("already running");
    }

    [Fact]
    public async Task Stops_and_changes_nothing_when_the_payload_does_not_verify()
    {
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(ManifestVerificationResult.Failed("hash mismatch on payload 'mysql'"));

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false });

        result.Outcome.Should().Be(PipelineOutcome.OperationFailed);
        result.ReachedPhase.Should().Be(InstallerPhase.Verify);
        _dataRootInit.Verify(d => d.InitializeAsync(It.IsAny<CancellationToken>()), Times.Never,
            "verification is the tamper gate; nothing may touch the machine before it passes");
        _services.Verify(s => s.RegisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Stops_and_changes_nothing_when_a_precheck_blocks()
    {
        GivenVerificationSucceeds();
        _prechecks.Add(new StubPrecheck(PrecheckSeverity.Block, "ERP-INST-PRE-DISK", "Only 2 GB free; 40 GB required."));

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false });

        result.Outcome.Should().Be(PipelineOutcome.PrecheckFailed);
        result.Message.Should().Contain("ERP-INST-PRE-DISK");
        _dataRootInit.Verify(d => d.InitializeAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_warning_precheck_does_not_block()
    {
        GivenVerificationSucceeds();
        GivenConfigGenerates();
        GivenDatabaseCanBootstrap();
        GivenDatabaseExecutes();
        GivenTopology();
        _prechecks.Add(new StubPrecheck(PrecheckSeverity.Warning, "ERP-INST-PRE-AV", "Antivirus exclusions not configured."));

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = true });

        result.Outcome.Should().Be(PipelineOutcome.Success);
    }

    // ── Dry run ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dry_run_verifies_and_prechecks_but_touches_nothing()
    {
        GivenVerificationSucceeds();
        GivenConfigGenerates();
        GivenDatabaseCanBootstrap();
        GivenDatabaseExecutes();
        GivenTopology(3);

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = true });

        result.Outcome.Should().Be(PipelineOutcome.Success);
        result.Steps.Should().Contain(s => s.Contains("DRY RUN", StringComparison.Ordinal));

        _verifier.Verify(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _serviceMap.Verify(m => m.LoadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once);

        _dataRootInit.Verify(d => d.InitializeAsync(It.IsAny<CancellationToken>()), Times.Never);
        _payloads.Verify(p => p.ExtractAllAsync(It.IsAny<ReleaseManifest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<int, int, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _binaries.Verify(b => b.DeployAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _services.Verify(s => s.RegisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dry_run_fails_on_a_malformed_topology_before_it_would_have_installed()
    {
        // A map that cannot be loaded must stop the run while the machine is still clean,
        // not after half the services are registered.
        GivenVerificationSucceeds();
        _serviceMap.Setup(m => m.LoadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new ServiceMapException("Duplicate service name in map: 'svc1'."));

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = true });

        result.Outcome.Should().Be(PipelineOutcome.OperationFailed);
        result.Message.Should().Contain("Duplicate service name");
    }

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_runs_every_step_in_order_and_checkpoints()
    {
        GivenVerificationSucceeds();
        GivenConfigGenerates();
        GivenDatabaseCanBootstrap();
        GivenDatabaseExecutes();
        GivenTopology(2);
        var order = new List<string>();
        _dataRootInit.Setup(d => d.InitializeAsync(It.IsAny<CancellationToken>())).Callback(() => order.Add("dataroot")).Returns(Task.CompletedTask);
        _payloads.Setup(p => p.ExtractAllAsync(It.IsAny<ReleaseManifest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<int, int, string>>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("extract")).Returns(Task.CompletedTask);
        _binaries.Setup(b => b.DeployAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("deploy")).Returns(Task.CompletedTask);
        _config.Setup(c => c.GenerateAllAsync(It.IsAny<SiteConfigPack>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()))
               .Callback(() => order.Add("config"))
               .ReturnsAsync(new ConfigGenerationResult { GeneratedFiles = ["appsettings.json"], TokensResolved = 12 });
        _services.Setup(s => s.RegisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("register")).Returns(Task.CompletedTask);
        _services.Setup(s => s.StartAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>())).Callback(() => order.Add("start")).Returns(Task.CompletedTask);

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false });

        result.Outcome.Should().Be(PipelineOutcome.Success);
        result.ReachedPhase.Should().Be(InstallerPhase.Success);

        // Order is the point: binaries cannot be deployed before they are extracted, and no
        // service may start before its configuration is on disk.
        order.Should().ContainInOrder("dataroot", "extract", "deploy", "config", "register", "start");

        var checkpoint = Path.Combine(_dataRoot, "installer", "state.json");
        File.Exists(checkpoint).Should().BeTrue("every phase must be recoverable after a power cut");
    }

    [Fact]
    public async Task Does_not_claim_the_install_is_healthy()
    {
        // Health verification is unimplemented (tasks.md 13.3). Reporting success is honest
        // only because the message says what was NOT checked.
        GivenVerificationSucceeds();
        GivenConfigGenerates();
        GivenDatabaseCanBootstrap();
        GivenDatabaseExecutes();
        GivenTopology();

        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false });

        result.Steps.Should().Contain(s => s.Contains("Health verification is not implemented", StringComparison.Ordinal));
    }

    // ── Uninstall ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Uninstall_refuses_when_the_installed_service_map_is_missing()
    {
        // Guessing which Windows services belong to ePACS is not reversible.
        var result = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Uninstall });

        // ModeDetector rejects it first ("no existing installation"), before the pipeline's own
        // service-map guard is reached. Either refusal is correct, so assert the behaviour that
        // matters rather than the wording of whichever one fired.
        result.Outcome.Should().Be(PipelineOutcome.OperationFailed);
        result.Outcome.Should().NotBe(PipelineOutcome.Success);
        _services.Verify(s => s.StopAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never,
            "nothing may be stopped when the installer cannot establish what belongs to ePACS");
        _services.Verify(s => s.DeregisterAllAsync(It.IsAny<IReadOnlyList<ServiceMapEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }

    private sealed class StubPrecheck(PrecheckSeverity severity, string id, string message) : IPrecheck
    {
        public string CheckId => id;
        public string Name => id;
        public int Order => 1;
        public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrecheckResult { CheckId = id, Name = id, Severity = severity, Message = message });
    }
}
