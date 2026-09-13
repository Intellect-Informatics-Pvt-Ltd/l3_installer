using System.Security.Cryptography;
using System.Text;
using BackupRestore.Backup;
using BackupRestore.Restore;
using FluentAssertions;
using Installer.Actions.Database;
using Installer.Actions.Health;
using Installer.Actions.Install;
using Installer.Actions.Platform;
using Installer.Actions.Prechecks;
using Installer.Actions.Topology;
using Installer.Actions.Uninstall;
using Installer.Core.Packs;
using Installer.Core.Pipeline;
using Installer.Core.Repair;
using Installer.Core.StateMachine;
using Installer.Core.Upgrade;
using ManifestVerifier;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.UnitTests;

/// <summary>
/// The refusal table in tasks.md, as tests: one per row that the pipeline itself owns, each
/// asserting the outcome AND that nothing under the data root or the binary root changed - a
/// hash of every file before and after. A refusal that mutates is not a refusal.
///
/// The real medium fixture, the real service-map loader, the real state machine and the real
/// lock are used; only what needs a machine (services, database, engines) is mocked, and those
/// mocks record every call so "nothing was touched" is also "nothing was asked to be".
/// </summary>
public sealed class RefusalMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "epacs-refusal-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IManifestVerificationService> _verifier = new();
    private readonly Mock<IDataRootInitializer> _dataRootInit = new(MockBehavior.Strict);
    private readonly Mock<IPayloadExtractor> _payloads = new(MockBehavior.Strict);
    private readonly Mock<IBinaryDeployer> _binaries = new(MockBehavior.Strict);
    private readonly Mock<IConfigGenerator> _config = new(MockBehavior.Strict);
    private readonly Mock<IServiceOrchestrator> _services = new(MockBehavior.Strict);
    private readonly Mock<IDatabaseBootstrapper> _database = new(MockBehavior.Strict);
    private readonly Mock<IUpgradeEngine> _upgrade = new(MockBehavior.Strict);
    private readonly Mock<IRestoreEngine> _restore = new(MockBehavior.Strict);
    private readonly Mock<IRepairEngine> _repair = new(MockBehavior.Strict);
    private readonly Mock<IPayloadConfigRewriter> _rewriter = new(MockBehavior.Strict);
    private readonly Mock<IServiceAccountProvisioner> _accounts = new(MockBehavior.Strict);
    private readonly Mock<IAclEngine> _acl = new(MockBehavior.Strict);
    private readonly Mock<IFirewallManager> _firewall = new(MockBehavior.Strict);
    private readonly Mock<IHealthAggregator> _health = new(MockBehavior.Strict);
    private readonly Mock<IBackupEngine> _backup = new(MockBehavior.Strict);
    private readonly Mock<IPolicyPackApplier> _packs = new(MockBehavior.Strict);
    private readonly List<IPrecheck> _prechecks = [];
    private ReleaseManifest? _manifest;

    private string DataRoot => Path.Combine(_root, "data");
    private string BinaryRoot => Path.Combine(_root, "bin");
    private string MediaDir => Path.Combine(_root, "media");

    private InstallerOptions Options => new() { DataRoot = DataRoot, BinaryRoot = BinaryRoot, ManifestPath = Path.Combine(MediaDir, "release-manifest.yaml") };

    private static SiteConfigPack Site => new() { Signature = "sig", PacsId = "AP-XYZ-0001", StateCode = "AP", DataRoot = "/d" };

    public RefusalMatrixTests()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BinaryRoot);
        File.WriteAllText(Path.Combine(DataRoot, "existing.txt"), "a society's file");
        _manifest = MediumFixture.Write(MediaDir);
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(() => new ManifestVerificationResult { Valid = true, Manifest = _manifest });
    }

    private InstallerPipeline Build()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        return new InstallerPipeline(
            new InstallerStateMachineFactory(opts, NullLogger<InstallerStateMachine>.Instance),
            new ModeDetector(opts, NullLogger<ModeDetector>.Instance),
            new InstallerLock(opts, NullLogger<InstallerLock>.Instance),
            new PrecheckRunner(_prechecks, NullLogger<PrecheckRunner>.Instance),
            _verifier.Object,
            new ServiceMapLoader(NullLogger<ServiceMapLoader>.Instance),
            _dataRootInit.Object, _payloads.Object, _binaries.Object, _config.Object, _services.Object,
            new UninstallAction(_services.Object, new Mock<IOverrideTokenValidator>(MockBehavior.Strict).Object, _firewall.Object, opts, NullLogger<UninstallAction>.Instance),
            _database.Object, _upgrade.Object, _restore.Object, _repair.Object,
            new SiteTokenSource(), _rewriter.Object, _accounts.Object, _acl.Object, _firewall.Object, _health.Object, _backup.Object, _packs.Object,
            opts, Microsoft.Extensions.Options.Options.Create(new ComponentsOptions()), NullLogger<InstallerPipeline>.Instance);
    }

    /// <summary>A hash over every file's path and bytes under the roots - the "nothing changed" oracle.</summary>
    private string Snapshot()
    {
        var sb = new StringBuilder();
        foreach (var root in new[] { DataRoot, BinaryRoot })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                // The installer's own lock, checkpoint and scratch (verified-media staging under
                // temp/) are the pipeline's, not the node's; everything else must be untouched.
                if (file.Contains(Path.Combine("data", "installer"), StringComparison.Ordinal)
                    || file.Contains(Path.Combine("data", "temp"), StringComparison.Ordinal)
                    || file.EndsWith(".lock", StringComparison.Ordinal) || file.EndsWith(".owner", StringComparison.Ordinal))
                {
                    continue;
                }

                sb.Append(file).Append('=').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private async Task<PipelineResult> RunAndAssertUntouchedAsync(PipelineRequest request, PipelineOutcome expected, string messagePart)
    {
        var before = Snapshot();
        var result = await Build().RunAsync(request);
        result.Outcome.Should().Be(expected, result.Message);
        result.Message.Should().ContainEquivalentOf(messagePart);
        Snapshot().Should().Be(before, "a refusal must leave the node exactly as it found it");
        return result;
    }

    [Fact]
    public Task Install_with_no_site_pack_is_refused() =>
        RunAndAssertUntouchedAsync(new PipelineRequest { Mode = InstallerMode.Install, DryRun = false }, PipelineOutcome.Refused, "site configuration pack");

    [Fact]
    public async Task Another_installer_holding_the_lock_is_refused_naming_it()
    {
        using var held = new InstallerLock(Microsoft.Extensions.Options.Options.Create(Options), NullLogger<InstallerLock>.Instance);
        held.TryAcquire().Should().BeTrue();

        await RunAndAssertUntouchedAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false }, PipelineOutcome.Refused, "already running");
    }

    [Fact]
    public async Task A_payload_that_fails_verification_is_refused_before_anything_is_touched()
    {
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ManifestVerificationResult { Valid = false, Errors = ["services.zip: hash mismatch"] });

        await RunAndAssertUntouchedAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false }, PipelineOutcome.OperationFailed, "hash mismatch");
    }

    [Fact]
    public async Task A_control_payload_altered_on_the_medium_is_refused_before_anything_is_touched()
    {
        File.AppendAllText(Path.Combine(MediaDir, "stable_baseline_ddl.sql"), "DROP TABLE t;\n");

        await RunAndAssertUntouchedAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false }, PipelineOutcome.OperationFailed, "altered or truncated");
    }

    [Fact]
    public async Task A_blocking_precheck_is_refused_before_anything_is_touched()
    {
        _prechecks.Add(new Stub(PrecheckSeverity.Block, "ERP-INST-PRE-0004", "2 GB free; 40 GB required."));

        await RunAndAssertUntouchedAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false }, PipelineOutcome.PrecheckFailed, "ERP-INST-PRE-0004");
    }

    [Fact]
    public async Task A_malformed_service_map_is_refused_while_the_machine_is_clean()
    {
        // Rebuild the medium with a map the loader cannot accept.
        Directory.Delete(MediaDir, recursive: true);
        _manifest = MediumFixtureWithMap("services:\n  - name: \"broken\"\n    display_name: \"x\"\n");   // no executable, no health check

        await RunAndAssertUntouchedAsync(new PipelineRequest { Mode = InstallerMode.Install, SiteConfig = Site, DryRun = false }, PipelineOutcome.OperationFailed, "broken");
    }

    [Fact]
    public Task Restore_with_no_backup_named_is_refused() =>
        RunAndAssertUntouchedAsync(new PipelineRequest { Mode = InstallerMode.Restore, SiteConfig = Site, DryRun = false }, PipelineOutcome.Refused, "--backup");

    [Fact]
    public async Task A_dry_run_of_every_mode_touches_nothing()
    {
        // The dry run is the default, and it must be a complete answer - so it exercises the
        // verify/precheck/topology path for install and refuses or previews the rest.
        _repair.Setup(r => r.RepairAsync(It.IsAny<RepairRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new RepairResult { Success = true, Version = "3.2.1", Findings = [], Repaired = [], Message = "Dry run. Nothing was changed." });
        // The database PLAN is read-only and part of a complete dry-run answer.
        _database.Setup(d => d.PlanAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DatabaseBootstrapPlan
                 {
                     CanProceed = true,
                     CaseSensitivity = new CaseSensitivityVerdict { CanHostEstateSetting = true, FileSystemIsCaseSensitive = true, Explanation = "case-sensitive" },
                     DataDirectory = "/tmp/data", ConfigFilePath = "/tmp/my.ini", DataDirectoryAlreadyInitialised = false
                 });
        var before = Snapshot();

        foreach (var mode in new[] { InstallerMode.Install, InstallerMode.Backup })
        {
            var result = await Build().RunAsync(new PipelineRequest { Mode = mode, SiteConfig = Site, DryRun = true });
            result.Outcome.Should().Be(PipelineOutcome.Success, $"{mode}: {result.Message}");
        }

        // Repair on a node with no installation is itself a refusal - and touches nothing.
        var repair = await Build().RunAsync(new PipelineRequest { Mode = InstallerMode.Repair, SiteConfig = Site, DryRun = true });
        repair.Outcome.Should().Be(PipelineOutcome.OperationFailed);
        repair.Message.Should().Contain("no existing ePACS installation");

        Snapshot().Should().Be(before);
        _dataRootInit.VerifyNoOtherCalls();
        _services.VerifyNoOtherCalls();
        _database.Verify(d => d.PlanAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce, "the plan is read-only and part of the answer");
        _database.Verify(d => d.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ReleaseManifest MediumFixtureWithMap(string yaml)
    {
        var manifest = MediumFixture.Write(MediaDir);
        // Replace the config payload's map and re-hash.
        var tmp = Path.Combine(_root, "cfg");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "service-map.yaml"), yaml);
        var zip = Path.Combine(MediaDir, "config.zip");
        File.Delete(zip);
        System.IO.Compression.ZipFile.CreateFromDirectory(tmp, zip);
        using var stream = File.OpenRead(zip);
        var entry = manifest.Payloads.Single(p => p.Name == "config") with { Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), SizeBytes = new FileInfo(zip).Length };
        return manifest with { Payloads = manifest.Payloads.Select(p => p.Name == "config" ? entry : p).ToList() };
    }

    private sealed class Stub(PrecheckSeverity severity, string id, string message) : IPrecheck
    {
        public string CheckId => id;
        public string Name => id;
        public int Order => 1;
        public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrecheckResult { CheckId = id, Name = id, Severity = severity, Message = message });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
