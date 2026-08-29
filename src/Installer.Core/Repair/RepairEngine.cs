using Installer.Actions.Install;
using Installer.Actions.Topology;
using ManifestVerifier;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Core.Repair;

/// <summary>
/// Puts an installation back the way the release says it should be, WITHOUT touching data.
///
/// ── WHAT REPAIR IS FOR, AND WHAT IT IS NOT ──────────────────────────────────────────────────
///
/// A node has been running for months. Somebody edited a config file to chase a problem, an
/// antivirus quarantined a DLL, a service was deleted and never re-registered, an upgrade was
/// interrupted. The binaries and configuration have drifted from what the release declares.
///
/// Repair restores <b>what the release owns</b>: payloads, generated configuration, service
/// registrations, the <c>current</c> link. It never touches <b>what the site owns</b>: the
/// database, attachments, logs, backups. That line is the whole safety argument — a repair is
/// something an operator should be able to run without a backup and without a decision, and it
/// stays that way only for as long as it cannot destroy anything.
///
/// So if the problem is in the DATA, repair is the wrong tool and says so: that is restore. If
/// it is in the SCHEMA, that is upgrade, or a DBA. Repair is for the parts the medium can simply
/// re-lay.
///
/// Everything is diagnosed BEFORE anything is changed, so a dry run is a complete answer rather
/// than a prefix of one.
/// </summary>
public sealed class RepairEngine : IRepairEngine
{
    private readonly IManifestVerificationService _manifestVerifier;
    private readonly IServiceMapLoader _serviceMapLoader;
    private readonly IPayloadExtractor _payloads;
    private readonly IBinaryDeployer _binaries;
    private readonly IConfigGenerator _configGenerator;
    private readonly IServiceOrchestrator _services;
    private readonly IOptions<InstallerOptions> _options;
    private readonly IOptions<ComponentsOptions> _components;
    private readonly ILogger<RepairEngine> _logger;

    public RepairEngine(
        IManifestVerificationService manifestVerifier,
        IServiceMapLoader serviceMapLoader,
        IPayloadExtractor payloads,
        IBinaryDeployer binaries,
        IConfigGenerator configGenerator,
        IServiceOrchestrator services,
        IOptions<InstallerOptions> options,
        IOptions<ComponentsOptions> components,
        ILogger<RepairEngine> logger)
    {
        _manifestVerifier = manifestVerifier;
        _serviceMapLoader = serviceMapLoader;
        _payloads = payloads;
        _binaries = binaries;
        _configGenerator = configGenerator;
        _services = services;
        _options = options;
        _components = components;
        _logger = logger;
    }

    public async Task<RepairResult> RepairAsync(RepairRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var opts = _options.Value;
        var findings = new List<RepairFinding>();
        var mediaDir = request.MediaDirectory;
        var manifestPath = Path.Combine(mediaDir, Path.GetFileName(opts.ManifestPath));
        var signature = manifestPath + ".sig";

        // The same media gate install and upgrade use. Re-laying binaries from an unverified
        // medium turns a drifted node into a compromised one.
        var verification = await _manifestVerifier.VerifyAsync(
            manifestPath, mediaDir,
            File.Exists(signature) ? signature : null,
            opts.ExpectedSigningThumbprint,
            cancellationToken);

        if (!verification.Valid || verification.Manifest is null)
        {
            throw new RepairException(
                $"The media did not verify: {string.Join("; ", verification.Errors)}. Nothing has been changed.");
        }

        var manifest = verification.Manifest;
        var version = manifest.Manifest.StackVersion;

        var installedTarget = _binaries.ResolveCurrent();
        var installedVersion = installedTarget is null ? null : Path.GetFileName(installedTarget);
        var releasePath = Path.Combine(opts.ReleasesPath, version);

        if (installedVersion is null)
        {
            findings.Add(new RepairFinding(RepairArea.CurrentLink, RepairSeverity.Broken,
                "'current' points at nothing, so no service can start."));
        }
        else if (!string.Equals(installedVersion, version, StringComparison.Ordinal))
        {
            // Repair re-lays the release that is already installed. Moving between versions is
            // an upgrade — it takes a backup and runs migrations — and doing it under the name
            // "repair" would skip both.
            throw new RepairException(
                $"This medium carries {version} but {installedVersion} is installed. Repair re-lays the release " +
                "that is already there; changing version is an upgrade, which takes a backup and runs migrations. " +
                "Nothing has been changed.");
        }

        if (!Directory.Exists(releasePath))
        {
            findings.Add(new RepairFinding(RepairArea.Binaries, RepairSeverity.Broken,
                $"The release directory {releasePath} is missing."));
        }

        var serviceMapPath = Path.Combine(mediaDir, opts.ServiceMapPath);
        var services = await _serviceMapLoader.LoadAsync(
            serviceMapPath, _components.Value.EnabledGroups(), cancellationToken);

        var configDir = Path.Combine(opts.DataRoot, "config");
        var configMissing = !Directory.Exists(configDir)
                            || Directory.GetFiles(configDir, "*", SearchOption.AllDirectories).Length == 0;

        if (configMissing)
        {
            findings.Add(new RepairFinding(RepairArea.Configuration, RepairSeverity.Broken,
                "No generated configuration is present."));
        }
        else if (request.RegenerateConfiguration)
        {
            findings.Add(new RepairFinding(RepairArea.Configuration, RepairSeverity.Requested,
                "Configuration will be regenerated from templates, discarding any hand edits."));
        }

        findings.Add(new RepairFinding(RepairArea.Services, RepairSeverity.Requested,
            $"{services.Count} service(s) will be re-registered from the service map."));

        if (request.DryRun)
        {
            return new RepairResult
            {
                Success = true,
                Version = version,
                Findings = findings,
                Repaired = [],
                Message = "Dry run. Nothing was changed."
            };
        }

        var repaired = new List<string>();

        await _services.StopAllAsync(services, cancellationToken);
        repaired.Add($"Stopped {services.Count} service(s).");

        var binariesNeedRelaying =
            request.ReplaceBinaries ||
            findings.Exists(f => f.Area is RepairArea.Binaries or RepairArea.CurrentLink);

        if (binariesNeedRelaying)
        {
            var staging = Path.Combine(opts.ResolvedTempRoot, "repair", version);
            await _payloads.ExtractAllAsync(manifest, mediaDir, staging, cancellationToken: cancellationToken);
            await _binaries.StageAsync(staging, version, cancellationToken);
            await _binaries.SwitchCurrentAsync(version);
            repaired.Add($"Re-laid the binaries for {version} and repointed 'current'.");
        }

        if (request.RegenerateConfiguration || configMissing)
        {
            if (request.SiteConfig is null)
            {
                // Refused, not skipped. A repair that leaves broken configuration in place and
                // reports success is worse than one that did not run.
                throw new RepairException(
                    "Configuration needs regenerating but no site configuration pack was supplied. Pass " +
                    "--config=<path-to-.epcfg>. The services are stopped; re-run to complete the repair.");
            }

            var generated = await _configGenerator.GenerateAllAsync(
                request.SiteConfig,
                Path.Combine(mediaDir, "config-templates"),
                configDir,
                services,
                cancellationToken);

            repaired.Add($"Regenerated {generated.GeneratedFiles.Count} configuration file(s).");
        }

        // Unconditional, because it is idempotent and cheap, and because a service whose binary
        // path or environment has drifted is invisible until it fails to start — which is
        // exactly the situation somebody runs repair to get out of.
        await _services.RegisterAllAsync(services, cancellationToken);
        repaired.Add($"Re-registered {services.Count} service(s).");

        await _services.StartAllAsync(services, cancellationToken);
        repaired.Add($"Started {services.Count} service(s).");

        LogEvents.RepairCompleted(_logger, version, repaired.Count);

        return new RepairResult
        {
            Success = true,
            Version = version,
            Findings = findings,
            Repaired = repaired,
            Message = $"Repaired {version}. The database, attachments and logs were not touched."
        };
    }
}
