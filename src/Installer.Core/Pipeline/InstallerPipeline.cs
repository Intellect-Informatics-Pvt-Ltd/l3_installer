using Installer.Actions.Database;
using Installer.Actions.Health;
using Installer.Actions.Install;
using Installer.Actions.Platform;
using Installer.Actions.Prechecks;
using Installer.Actions.Topology;
using Installer.Actions.Uninstall;
using Installer.Core.StateMachine;
using Installer.Core.Upgrade;
using Installer.Core.Repair;
using BackupRestore.Backup;
using BackupRestore.Models;
using BackupRestore.Restore;
using ManifestVerifier;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Security;

namespace Installer.Core.Pipeline;

/// <summary>
/// The composition target — see <see cref="IInstallerPipeline"/>.
///
/// SHAPE. Every mode is a sequence of phases, and every phase transition is checkpointed by
/// <see cref="IInstallerStateMachine"/> before the work begins, not after. That ordering is the
/// whole power-cut story: a node that loses power mid-extract restarts knowing it was extracting.
///
/// WHAT IT REFUSES, and why refusing is the feature:
///   * A second concurrent installer   - two processes registering services is unrecoverable
///   * A blocking precheck             - proceeding produces a half-installed node
///   * A mode with no engine           - see PipelineOutcome.NotImplemented
///   * An unverified payload           - the manifest gate is the only tamper-evidence we have
/// </summary>
public sealed class InstallerPipeline : IInstallerPipeline
{
    private readonly IInstallerStateMachineFactory _stateMachineFactory;
    private readonly ModeDetector _modeDetector;
    private readonly InstallerLock _lock;
    private readonly PrecheckRunner _prechecks;
    private readonly IManifestVerificationService _manifestVerifier;
    private readonly IServiceMapLoader _serviceMapLoader;
    private readonly IDataRootInitializer _dataRoot;
    private readonly IPayloadExtractor _payloads;
    private readonly IBinaryDeployer _binaries;
    private readonly IConfigGenerator _configGenerator;
    private readonly IServiceOrchestrator _services;
    private readonly UninstallAction _uninstall;
    private readonly IDatabaseBootstrapper _database;
    private readonly IUpgradeEngine _upgrade;
    private readonly IRestoreEngine _restore;
    private readonly IRepairEngine _repair;
    private readonly ISiteTokenSource _siteTokens;
    private readonly IPayloadConfigRewriter _payloadConfig;
    private readonly IServiceAccountProvisioner _accounts;
    private readonly IAclEngine _acl;
    private readonly IFirewallManager _firewall;
    private readonly IHealthAggregator _health;
    private readonly IBackupEngine _backup;
    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ComponentsOptions> _components;
    private readonly ILogger<InstallerPipeline> _logger;

    /// <summary>
    /// Created per run, once the mode and target version are known, and only then. Null until
    /// that point, which is deliberate: a checkpoint written before the manifest is verified
    /// would name a version nothing had cryptographically accounted for, and a recovery run
    /// reads exactly that field to decide what to resume.
    /// </summary>
    private IInstallerStateMachine? _stateMachine;

    public InstallerPipeline(
        IInstallerStateMachineFactory stateMachineFactory,
        ModeDetector modeDetector,
        InstallerLock installerLock,
        PrecheckRunner prechecks,
        IManifestVerificationService manifestVerifier,
        IServiceMapLoader serviceMapLoader,
        IDataRootInitializer dataRoot,
        IPayloadExtractor payloads,
        IBinaryDeployer binaries,
        IConfigGenerator configGenerator,
        IServiceOrchestrator services,
        UninstallAction uninstall,
        IDatabaseBootstrapper database,
        IUpgradeEngine upgrade,
        IRestoreEngine restore,
        IRepairEngine repair,
        ISiteTokenSource siteTokens,
        IPayloadConfigRewriter payloadConfig,
        IServiceAccountProvisioner accounts,
        IAclEngine acl,
        IFirewallManager firewall,
        IHealthAggregator health,
        IBackupEngine backup,
        IOptions<InstallerOptions> options,
        IOptions<ComponentsOptions> components,
        ILogger<InstallerPipeline> logger)
    {
        _stateMachineFactory = stateMachineFactory;
        _modeDetector = modeDetector;
        _lock = installerLock;
        _prechecks = prechecks;
        _manifestVerifier = manifestVerifier;
        _serviceMapLoader = serviceMapLoader;
        _dataRoot = dataRoot;
        _payloads = payloads;
        _binaries = binaries;
        _configGenerator = configGenerator;
        _services = services;
        _uninstall = uninstall;
        _database = database;
        _upgrade = upgrade;
        _restore = restore;
        _repair = repair;
        _siteTokens = siteTokens;
        _payloadConfig = payloadConfig;
        _accounts = accounts;
        _acl = acl;
        _firewall = firewall;
        _health = health;
        _backup = backup;
        _options = options;
        _components = components;
        _logger = logger;
    }

    public async Task<PipelineResult> RunAsync(PipelineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var steps = new List<string>();
        var mode = InstallerMode.Install;

        // A second installer on the same machine is not a race to be won, it is a corrupted
        // installation: both processes call sc.exe, both write the state file, and neither
        // result is recoverable. Take the lock before anything else, including mode detection.
        if (!_lock.TryAcquire())
        {
            return PipelineResult.Failed(
                PipelineOutcome.Refused, mode, InstallerPhase.Load,
                "Another ePACS installer process is already running on this machine. Wait for it to finish, or reboot if it has crashed.");
        }

        try
        {
            mode = _modeDetector.Detect(request.Mode);
            steps.Add($"Mode: {mode}" + (request.Mode is null ? " (auto-detected)" : " (requested)"));

            return mode switch
            {
                InstallerMode.Install   => await RunInstallAsync(request, mode, steps, cancellationToken),
                InstallerMode.Uninstall => await RunUninstallAsync(request, mode, steps, cancellationToken),

                InstallerMode.Upgrade   => await RunUpgradeAsync(request, mode, steps, cancellationToken),
                InstallerMode.Restore   => await RunRestoreAsync(request, mode, steps, cancellationToken),

                InstallerMode.Repair    => await RunRepairAsync(request, mode, steps, cancellationToken),

                // Still without an implementing type. Saying so is the correct behaviour: see
                // PipelineOutcome.NotImplemented.
                InstallerMode.Backup    => await RunBackupAsync(request, mode, steps, cancellationToken),

                _ => NotImplemented(mode, steps, $"Mode {mode} is not supported by this build.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlatformNotSupportedException ex)
        {
            // An engine this build does not have for this platform. Exit 4, by name - never 0,
            // and distinct from an operation that was attempted and failed.
            LogEvents.PipelineFailed(_logger, ex, mode);
            await SafeFailAsync("ERP-INST-NOT-BUILT", ex.Message, cancellationToken);
            return PipelineResult.Failed(PipelineOutcome.NotImplemented, mode,
                _stateMachine?.CurrentState.Phase ?? InstallerPhase.Load, ex.Message, steps);
        }
#pragma warning disable CA1031 // The pipeline is the top of the stack: an unhandled exception
        catch (Exception ex) // here would lose the checkpoint and the operator's diagnosis alike.
#pragma warning restore CA1031
        {
            LogEvents.PipelineFailed(_logger, ex, mode);
            await SafeFailAsync("ERP-INST-UNEXPECTED", ex.Message, cancellationToken);
            return PipelineResult.Failed(PipelineOutcome.OperationFailed, mode,
                _stateMachine?.CurrentState.Phase ?? InstallerPhase.Load, ex.Message, steps);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Install ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Keeps a copy of the signed <c>.epcfg</c> beside the generated configuration. Repair and
    /// upgrade read it when no <c>--config</c> is given, because re-registering a service means
    /// resolving <c>${epcfg:state_code}</c> again, and a node whose state is unknown must refuse
    /// rather than register under a default. The copy is the signed original, byte for byte, so
    /// the same signature check applies when it is read back.
    /// </summary>
    private static async Task<string?> PersistSitePackAsync(InstallerOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.SiteConfigPath) || !File.Exists(opts.SiteConfigPath))
        {
            return null;
        }

        var target = InstalledSitePackPath(opts);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var source = File.OpenRead(opts.SiteConfigPath))
        await using (var dest = File.Create(target))
        {
            await source.CopyToAsync(dest, ct);
            await dest.FlushAsync(ct);
        }

        return target;
    }

    /// <summary>Where the install keeps the site pack: <c>&lt;DataRoot&gt;/config/site.epcfg</c>.</summary>
    public static string InstalledSitePackPath(InstallerOptions opts) =>
        Path.Combine(opts.DataRoot, "config", "site.epcfg");

    private async Task<PipelineResult> RunInstallAsync(
        PipelineRequest request, InstallerMode mode, List<string> steps, CancellationToken ct)
    {
        if (request.SiteConfig is null)
        {
            return PipelineResult.Failed(PipelineOutcome.Refused, mode, InstallerPhase.Load,
                "No site configuration pack was supplied. An install needs /config:<path-to-.epcfg> to know which PACS this is.", steps);
        }

        var opts = _options.Value;

        // The site's tokens reach the orchestrators through here. Bound before anything else
        // so a service map carrying ${epcfg:state_code} - the generated L2-R2 topology does, on
        // every application service - registers with the right state or not at all.
        _siteTokens.Bind(request.SiteConfig);

        // ── Verify ───────────────────────────────────────────────────────────
        // Before anything touches the machine, and BEFORE the state machine exists.
        //
        // The ordering is not incidental. The first checkpoint stamps the target version, and a
        // recovery run reads that field to decide what to resume — so the version must come
        // from a manifest whose signature and payload hashes have already been checked. Writing
        // a checkpoint first would mean recording a version nothing had accounted for.
        //
        // Nothing has changed on this machine yet, so there is nothing to resume and nothing
        // lost by having no checkpoint during verification. This is also the only
        // tamper-evidence in force (Authenticode verification is unimplemented — ADR-0001), so
        // it is not skippable.
        var mediaDir = request.MediaDirectory
            ?? Path.GetDirectoryName(Path.GetFullPath(opts.ManifestPath))
            ?? Directory.GetCurrentDirectory();
        var manifestPath = Path.IsPathRooted(opts.ManifestPath)
            ? opts.ManifestPath
            : Path.Combine(mediaDir, Path.GetFileName(opts.ManifestPath));

        // The pin comes from THIS INSTALLER'S configuration, never from the manifest — see
        // InstallerOptions.ExpectedSigningThumbprint for why a manifest-supplied thumbprint is
        // worthless as a trust anchor.
        var verification = await _manifestVerifier.VerifyAsync(
            manifestPath, mediaDir,
            signaturePath: File.Exists(manifestPath + ".sig") ? manifestPath + ".sig" : null,
            expectedThumbprint: opts.ExpectedSigningThumbprint,
            cancellationToken: ct);

        if (!verification.Valid || verification.Manifest is null)
        {
            var detail = string.Join("; ", verification.Errors);
            await SafeFailAsync("ERP-INST-PRE-VERIFY", detail, ct);
            return PipelineResult.Failed(PipelineOutcome.OperationFailed, mode, InstallerPhase.Verify,
                $"Payload verification failed — nothing was installed. {detail}", steps);
        }

        var manifest = verification.Manifest;
        steps.Add($"Verified manifest {manifest.Manifest.ManifestId} and {verification.PayloadResults.Count} payload(s).");

        // The version is now trustworthy, so the run can start checkpointing.
        _stateMachine = _stateMachineFactory.Create(
            mode, manifest.Manifest.StackVersion, _modeDetector.GetInstalledVersion());

        // Resume rather than restart. A node that lost power mid-install comes back here.
        var recovered = await _stateMachine.TryRecoverAsync(ct);
        if (recovered is not null)
        {
            steps.Add($"Recovered an incomplete run from phase {recovered.Phase} (correlation {recovered.CorrelationId}).");
            LogEvents.PipelineResuming(_logger, recovered.Phase, recovered.CorrelationId);
        }

        // ── Precheck ─────────────────────────────────────────────────────────
        await _stateMachine.TransitionAsync(InstallerPhase.Precheck, cancellationToken: ct);
        var precheck = await _prechecks.RunAllAsync(ct);
        steps.Add($"Prechecks: {precheck.PassedCount} passed, {precheck.WarningCount} warning(s), {precheck.BlockingCount} blocking.");

        if (!precheck.CanProceed)
        {
            var blocking = string.Join("; ", precheck.Results.Where(r => r.Blocking).Select(r => $"{r.CheckId}: {r.Message}"));
            await SafeFailAsync("ERP-INST-PRE-BLOCK", blocking, ct);
            return PipelineResult.Failed(PipelineOutcome.PrecheckFailed, mode, InstallerPhase.Precheck,
                $"Blocking precheck(s) — nothing was installed. {blocking}", steps);
        }

        // ── Topology ─────────────────────────────────────────────────────────
        // Loaded before any mutation so a malformed map fails while the machine is still clean.
        var serviceMapPath = Path.IsPathRooted(opts.ServiceMapPath)
            ? opts.ServiceMapPath
            : Path.Combine(mediaDir, opts.ServiceMapPath);
        // Only the components this installation includes. A component that is off must not be
        // registered as a Windows service and left stopped - a stopped service looks like a
        // failed install to every operator and every monitoring tool that ever sees it.
        var groups = _components.Value.EnabledGroups();
        var services = await _serviceMapLoader.LoadAsync(serviceMapPath, groups, ct);
        steps.Add($"Topology: {services.Count} service(s) from {Path.GetFileName(serviceMapPath)} (components: {string.Join(", ", groups)}).");

        if (!_components.Value.Eventing.Enabled)
        {
            steps.Add("Eventing (Kafka + JRE, ~290 MB) is NOT installed. No deployment in the L2-R2 estate provisions Kafka, " +
                      "and every publisher sits behind the orchestration kill-switch. Enable Components:Eventing:Enabled if this site needs it.");
        }

        // ── Database plan ────────────────────────────────────────────────────
        // Planned here, while everything is still read-only, so a dry run tells the operator
        // whether the bootstrap would work rather than discovering it after extraction.
        var dbPlan = await _database.PlanAsync(ct);
        steps.AddRange(dbPlan.Steps.Select(x => $"database: {x}"));

        if (!dbPlan.CanProceed)
        {
            LogEvents.DatabaseBootstrapRefused(_logger, dbPlan.Blocker ?? "unknown");
            await SafeFailAsync("ERP-INST-DB-REFUSED", dbPlan.Blocker ?? "Database bootstrap refused.", ct);
            return PipelineResult.Failed(PipelineOutcome.PrecheckFailed, mode, InstallerPhase.Precheck,
                $"Database bootstrap refused — nothing was installed. {dbPlan.Blocker}", steps);
        }

        // Everything above this line is read-only. This is the last honest stopping point.
        if (request.DryRun)
        {
            steps.Add("DRY RUN — stopping before the first change to this machine. Re-run with --apply to install.");
            return PipelineResult.Success(mode, InstallerPhase.Precheck, steps,
                "Dry run complete. Verification, prechecks and topology all pass; nothing was changed.");
        }

        // ── Install ──────────────────────────────────────────────────────────
        await _stateMachine.TransitionAsync(InstallerPhase.Install, "data-root", cancellationToken: ct);
        await _dataRoot.InitializeAsync(ct);
        steps.Add($"Data root initialised at {opts.DataRoot}.");

        await _stateMachine.TransitionAsync(InstallerPhase.Install, "extract", cancellationToken: ct);
        var staging = Path.Combine(opts.ResolvedTempRoot, "staging");
        await _payloads.ExtractAllAsync(manifest, mediaDir, staging, cancellationToken: ct);
        steps.Add($"Extracted {manifest.Payloads.Count} payload(s) to staging.");

        await _stateMachine.TransitionAsync(InstallerPhase.Install, "deploy", cancellationToken: ct);
        await _binaries.DeployAsync(staging, manifest.Manifest.StackVersion, ct);
        steps.Add($"Deployed version {manifest.Manifest.StackVersion} and switched 'current'.");

        await _stateMachine.TransitionAsync(InstallerPhase.Install, "config", cancellationToken: ct);
        // The topology is passed in so templates can address any of the N application services
        // by name (${Service:l3_FAS:Port}), not just the four infrastructure ports.
        var configResult = await _configGenerator.GenerateAllAsync(
            request.SiteConfig,
            Path.Combine(mediaDir, "config-templates"),
            Path.Combine(opts.DataRoot, "config"),
            services,
            ct);
        steps.Add($"Generated {configResult.GeneratedFiles.Count} config file(s), {configResult.TokensResolved} token(s) resolved.");
        var installedSitePack = await PersistSitePackAsync(opts, ct);
        if (installedSitePack is not null)
        {
            steps.Add($"Kept the site pack at {installedSitePack} so repair and upgrade know which PACS this is.");
        }

        await _stateMachine.TransitionAsync(InstallerPhase.Migrate, "database", cancellationToken: ct);
        var baselineDdl = Path.Combine(mediaDir, "db", "stable_baseline_ddl.sql");
        var dbResult = await _database.ExecuteAsync(baselineDdl, ct);
        steps.AddRange(dbResult.Steps.Select(x => $"database: {x}"));

        // The node's facts go INTO each service's own appsettings.json - after the database
        // bootstrap, because the application password now exists, and before registration,
        // because a service must never start against the committed dev-server defaults (G26).
        await _stateMachine.TransitionAsync(InstallerPhase.Install, "service-config", cancellationToken: ct);
        var rewrite = await _payloadConfig.RewriteAsync(
            Path.Combine(_binaries.ResolveCurrent() ?? Path.Combine(opts.ReleasesPath, manifest.Manifest.StackVersion), "services"),
            Path.Combine(opts.DataRoot, "config", "appsettings.Site.json"),
            Path.Combine(mediaDir, "config", PayloadConfigRewriter.SiblingUrlsFileName),
            services,
            ct);
        steps.Add($"Rewrote {rewrite.Rewritten.Count} service configuration(s) for this node" +
                  (rewrite.PasswordWritten ? " with the database credential." : "."));

        // The least-privilege boundary, before a service exists to be inside it: the accounts the
        // map names, ownership of what each one writes and read-only on what it must not change,
        // and a firewall that keeps the database off the LAN. On a platform whose engines are not
        // built this is where the run exits 4 by name (tasks.md 25, 26, X1).
        await _stateMachine.TransitionAsync(InstallerPhase.Install, "platform", cancellationToken: ct);
        var created = await _accounts.EnsureAsync(services, ct);
        steps.Add(created.Count == 0 ? "Service accounts present." : $"Created service account(s): {string.Join(", ", created)}.");
        var aclRules = _acl.GenerateRules(services);
        await _acl.ApplyRulesAsync(aclRules, ct);
        steps.Add($"Applied {aclRules.Count} ownership/permission rule(s).");
        var firewallRules = _firewall.GenerateRules();
        await _firewall.ApplyRulesAsync(firewallRules, ct);
        steps.Add($"Loaded {firewallRules.Count} firewall rule(s): database, cache, eventing and agents reachable from this machine only.");

        await _stateMachine.TransitionAsync(InstallerPhase.Install, "services", cancellationToken: ct);
        await _services.RegisterAllAsync(services, ct);
        await _services.StartAllAsync(services, ct);
        steps.Add($"Registered and started {services.Count} service(s) in dependency order.");

        // ── Health ───────────────────────────────────────────────────────────
        // Three verdicts, counted separately. "Listening" is what a service with no health
        // route can prove (20 of 27 today, G31) and it is never upgraded to "healthy"; a failed
        // service fails the install by name.
        await _stateMachine.TransitionAsync(InstallerPhase.Health, cancellationToken: ct);
        var health = await _health.VerifyAsync(services, TimeSpan.FromSeconds(opts.HealthWindowSeconds), ct);
        steps.Add($"Health: {health.Summary}");
        if (!health.Passed)
        {
            await SafeFailAsync("ERP-INST-HEALTH", health.Summary, ct);
            return PipelineResult.Failed(PipelineOutcome.HealthFailed, mode, InstallerPhase.Health,
                $"{health.Failed} service(s) did not come up: " +
                string.Join("; ", health.Failures.Select(f => $"{f.Service} ({f.Detail})")) +
                ". The services are registered and the boundary is laid; fix the named service and run --mode repair, or read its journal.", steps);
        }

        await _stateMachine.CompleteAsync(ct);
        LogEvents.PipelineSucceeded(_logger, mode, manifest.Manifest.StackVersion);

        return PipelineResult.Success(mode, InstallerPhase.Success, steps,
            $"Installed ePACS {manifest.Manifest.StackVersion} for PACS {request.SiteConfig.PacsId} ({request.SiteConfig.StateCode}); " +
            $"{dbResult.TablesAfter} tables in the database.");
    }

    // ── Upgrade ──────────────────────────────────────────────────────────────

    private async Task<PipelineResult> RunUpgradeAsync(
        PipelineRequest request, InstallerMode mode, List<string> steps, CancellationToken ct)
    {
        var opts = _options.Value;
        var mediaDir = request.MediaDirectory
            ?? Path.GetDirectoryName(Path.GetFullPath(opts.ManifestPath))
            ?? Directory.GetCurrentDirectory();
        var manifestPath = Path.IsPathRooted(opts.ManifestPath)
            ? opts.ManifestPath
            : Path.Combine(mediaDir, Path.GetFileName(opts.ManifestPath));

        if (request.DryRun)
        {
            // The last honest stopping point. Everything the upgrade does after the backup
            // changes the node, so a dry run reports the path and stops.
            steps.Add("DRY RUN — an upgrade would verify the media, check the upgrade path, take and VERIFY a " +
                      "mandatory pre-upgrade backup, stage the new release beside the old, stop services, migrate, " +
                      "commit the switch, and restart. Re-run with --apply.");
            return PipelineResult.Success(mode, InstallerPhase.Upgrade, steps, "Dry run complete. Nothing was changed.");
        }

        var result = await _upgrade.UpgradeAsync(manifestPath, mediaDir,
            (message, percent) => steps.Add($"[{percent}%] {message}"), ct);

        if (result.Success)
        {
            return PipelineResult.Success(mode, InstallerPhase.Success, steps,
                $"Upgraded {result.PreviousVersion} to {result.NewVersion}. Pre-upgrade backup {result.PreUpgradeBackupId} retained.");
        }

        return PipelineResult.Failed(PipelineOutcome.OperationFailed, mode, InstallerPhase.Upgrade,
            result.ErrorMessage ?? "The upgrade failed.", steps);
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    private async Task<PipelineResult> RunRestoreAsync(
        PipelineRequest request, InstallerMode mode, List<string> steps, CancellationToken ct)
    {
        if (request.BackupPath is null)
        {
            return PipelineResult.Failed(PipelineOutcome.Refused, mode, InstallerPhase.Restore,
                "No backup was named. A restore needs --backup=<path>; the installer will not guess which backup " +
                "to overwrite this node's data with.", steps);
        }

        if (request.DryRun)
        {
            steps.Add($"DRY RUN — a restore from {request.BackupPath} would verify the package by hash, take a " +
                      "safety backup of the CURRENT data, stop services, restore, count, and restart. " +
                      "It would then require sync reconciliation. Re-run with --apply.");
            return PipelineResult.Success(mode, InstallerPhase.Restore, steps, "Dry run complete. Nothing was changed.");
        }

        var result = await _restore.RestoreAsync(request.BackupPath, createSafetyBackup: true,
            (message, percent) => steps.Add($"[{percent}%] {message}"), ct);

        steps.AddRange(result.Warnings);

        return result.Success
            ? PipelineResult.Success(mode, InstallerPhase.Success, steps,
                $"Restored from {result.BackupId}. Safety backup: {result.SafetyBackupId ?? "none"}. " +
                "Sync state must be reconciled before this node resumes sending.")
            : PipelineResult.Failed(PipelineOutcome.OperationFailed, mode, InstallerPhase.Restore,
                result.ErrorMessage ?? "The restore failed.", steps);
    }

    // ── Backup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Take a package, then verify it as it sits - hashes, the manifest MAC, the dump decrypts
    /// and is complete. A backup that was written and cannot be read back is reported as a
    /// failure, not a success with a caveat: the operator holding a stick must know whether it
    /// is a way back.
    /// </summary>
    private async Task<PipelineResult> RunBackupAsync(
        PipelineRequest request, InstallerMode mode, List<string> steps, CancellationToken ct)
    {
        if (request.DryRun)
        {
            steps.Add("DRY RUN — a backup would dump the database (--single-transaction), copy the generated " +
                      "configuration and key metadata, archive attachments with per-file hashes, encrypt every file " +
                      "under a fresh data key (AES-256-GCM), wrap that key to this node and to the state's recovery key " +
                      "if configured, MAC the manifest, and then VERIFY the package by reading it back. Re-run with --apply.");
            return PipelineResult.Success(mode, InstallerPhase.Load, steps, "Dry run complete. Nothing was changed.");
        }

        var manifest = await _backup.CreateBackupAsync(BackupType.Manual, (message, percent) => steps.Add($"[{percent}%] {message}"), ct);
        steps.Add($"Backup {manifest.BackupId}: {manifest.Files.Count} file(s), {manifest.KeyProtection}.");

        var location = manifest.PackagePath ?? throw new InvalidOperationException("The backup engine did not report where it wrote the package.");
        var verified = await _backup.VerifyBackupAsync(location, ct);
        if (!verified.Valid)
        {
            return PipelineResult.Failed(PipelineOutcome.OperationFailed, mode, InstallerPhase.Load,
                $"Backup {manifest.BackupId} was written but does not verify: {string.Join("; ", verified.Errors)}. " +
                "It is NOT a way back; do not rely on it.", steps);
        }

        steps.Add($"Verified: checksums {(verified.ChecksumVerified ? "ok" : "FAILED")}, manifest MAC {(verified.ManifestSignatureValid ? "ok" : "FAILED")}, dump {(verified.DumpReadable ? "decrypts and is complete" : "NOT readable")}.");
        return PipelineResult.Success(mode, InstallerPhase.Success, steps,
            $"Backup {manifest.BackupId} taken and verified at {location}. {manifest.KeyProtection}.");
    }

    // ── Repair ───────────────────────────────────────────────────────────────

    private async Task<PipelineResult> RunRepairAsync(
        PipelineRequest request, InstallerMode mode, List<string> steps, CancellationToken ct)
    {
        var opts = _options.Value;
        var mediaDir = request.MediaDirectory
            ?? Path.GetDirectoryName(Path.GetFullPath(opts.ManifestPath))
            ?? Directory.GetCurrentDirectory();

        var result = await _repair.RepairAsync(new RepairRequest
        {
            MediaDirectory = mediaDir,
            SiteConfig = request.SiteConfig,
            RegenerateConfiguration = request.RegenerateConfiguration,
            ReplaceBinaries = request.ReplaceBinaries,
            DryRun = request.DryRun
        }, ct);

        steps.AddRange(result.Findings.Select(f => $"found: [{f.Severity}] {f.Area} — {f.Message}"));
        steps.AddRange(result.Repaired.Select(r => $"did: {r}"));

        return result.Success
            ? PipelineResult.Success(mode, InstallerPhase.Success, steps, result.Message)
            : PipelineResult.Failed(PipelineOutcome.OperationFailed, mode, InstallerPhase.Repair,
                result.Message ?? "The repair failed.", steps);
    }

    // ── Uninstall ────────────────────────────────────────────────────────────

    private async Task<PipelineResult> RunUninstallAsync(
        PipelineRequest request, InstallerMode mode, List<string> steps, CancellationToken ct)
    {
        var opts = _options.Value;
        var serviceMapPath = Path.IsPathRooted(opts.ServiceMapPath)
            ? opts.ServiceMapPath
            : Path.Combine(opts.BinaryRoot, "current", opts.ServiceMapPath);

        if (!File.Exists(serviceMapPath))
        {
            return PipelineResult.Failed(PipelineOutcome.OperationFailed, mode, InstallerPhase.Uninstall,
                $"Cannot uninstall: the installed service map was not found at {serviceMapPath}. " +
                "Stopping rather than guessing which services belong to ePACS — removing the wrong service is not reversible.", steps);
        }

        var services = await _serviceMapLoader.LoadAsync(serviceMapPath, cancellationToken: ct);
        steps.Add($"Topology: {services.Count} service(s) to remove.");

        // For an uninstall the "target" is what is on the machine now. If the installed version
        // cannot be read, say so in the checkpoint rather than inventing one - a recovery run
        // that reads a fabricated version would resume against the wrong release directory.
        var installed = _modeDetector.GetInstalledVersion() ?? "unknown";
        _stateMachine = _stateMachineFactory.Create(mode, installed, installed);

        if (request.DryRun)
        {
            steps.Add("DRY RUN — stopping before any service is stopped or removed.");
            steps.Add(request.PurgeData
                ? "Data purge WAS requested: a real run would require a valid override token and the typed confirmation."
                : $"Business data at {opts.DataRoot} would be PRESERVED.");
            return PipelineResult.Success(mode, InstallerPhase.Uninstall, steps, "Dry run complete. Nothing was changed.");
        }

        await _stateMachine.TransitionAsync(InstallerPhase.Uninstall, cancellationToken: ct);
        await _uninstall.ExecuteAsync(services, request.PurgeData, request.OverrideToken, request.TypedConfirmation, ct);

        steps.Add(request.PurgeData
            ? "Business data PURGED under a validated override token."
            : $"Business data preserved at {opts.DataRoot}.");

        await _stateMachine.CompleteAsync(ct);
        return PipelineResult.Success(mode, InstallerPhase.Success, steps, "Uninstall complete.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineResult NotImplemented(InstallerMode mode, List<string> steps, string message)
        => PipelineResult.Failed(PipelineOutcome.NotImplemented, mode, InstallerPhase.Load, message, steps);

    /// <summary>
    /// Records the failure in the checkpoint without letting a failure-to-record mask the real
    /// failure. If the state file itself cannot be written — a full disk is the usual cause, and
    /// a full disk is also why the install failed — the operator needs the original error, not
    /// an IOException from the error handler.
    /// </summary>
    private async Task SafeFailAsync(string code, string message, CancellationToken ct)
    {
        if (_stateMachine is null)
        {
            // Failed before the manifest was verified, so no checkpoint exists and none is
            // needed: nothing on this machine has changed and there is nothing to resume.
            return;
        }

        try
        {
            await _stateMachine.FailAsync(code, message, ct);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEvents.CheckpointWriteFailed(_logger, ex, code);
        }
    }
}
