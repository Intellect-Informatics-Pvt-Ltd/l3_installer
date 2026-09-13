using Installer.Core.Packs;
using Installer.Core.Pipeline;
using Installer.Core.SiteConfig;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Hosting;

namespace Sync.Agent;

/// <summary>
/// The ePACS Sync Agent, v0 (ADR-0011): cut a ledger pack on the cadence, apply whatever policy
/// packs have arrived, and answer <c>/health/ready</c> only once the site is known.
///
/// <c>Packs:Mode=stream</c> — the frozen Kafka→NLDR path — is refused at start with exit 4
/// naming the ADR, never silently ignored: an operator who configured a stream must find out
/// from the exit code, not from a pack that never arrived at the state.
///
/// Business operations are never blocked by this worker. It reads the database and writes files;
/// a failed export is logged and retried on the next tick, and the ledger is only advanced
/// after a pack is whole on disk.
/// </summary>
public sealed partial class PackSyncWorker : BackgroundService
{
    public const int ExitStreamRefused = 4;

    private readonly ILedgerPackExporter _exporter;
    private readonly IPolicyPackApplier _applier;
    private readonly ISiteConfigLoader _siteLoader;
    private readonly HealthEndpoint _health;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOptions<PacksOptions> _packs;
    private readonly IOptions<InstallerOptions> _installer;
    private readonly ILogger<PackSyncWorker> _logger;

    public PackSyncWorker(
        ILedgerPackExporter exporter,
        IPolicyPackApplier applier,
        ISiteConfigLoader siteLoader,
        HealthEndpoint health,
        IHostApplicationLifetime lifetime,
        IOptions<PacksOptions> packs,
        IOptions<InstallerOptions> installer,
        ILogger<PackSyncWorker> logger)
    {
        _exporter = exporter;
        _applier = applier;
        _siteLoader = siteLoader;
        _health = health;
        _lifetime = lifetime;
        _packs = packs;
        _installer = installer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = _packs.Value;
        if (!string.Equals(o.Mode, "packs", StringComparison.OrdinalIgnoreCase))
        {
            LogStreamRefused(_logger, o.Mode);
            Environment.ExitCode = ExitStreamRefused;
            _lifetime.StopApplication();
            return;
        }

        SiteConfigPack site;
        try
        {
            site = await _siteLoader.LoadAsync(InstallerPipeline.InstalledSitePackPath(_installer.Value), allowUnsigned: false, stoppingToken);
        }
        catch (Exception ex) when (ex is SiteConfigException or IOException)
        {
            // Not ready, and never guessing: a pack for the wrong society is worse than none.
            LogNoSite(_logger, ex.Message);
            Environment.ExitCode = ExitStreamRefused;
            _lifetime.StopApplication();
            return;
        }

        _health.Ready = true;
        LogStarted(_logger, site.PacsId, o.ExportIntervalMinutes, o.ApplyIntervalMinutes);

        var lastExport = DateTimeOffset.MinValue;
        var lastApply = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastApply >= TimeSpan.FromMinutes(o.ApplyIntervalMinutes))
            {
                lastApply = now;
                try
                {
                    var sweep = await _applier.ApplyInboundAsync(site, stoppingToken);
                    if (sweep.Applied.Count + sweep.Acknowledged.Count + sweep.Waiting.Count + sweep.Rejected.Count > 0)
                    {
                        LogSweep(_logger, sweep.Applied.Count, sweep.Acknowledged.Count, sweep.Waiting.Count, sweep.Rejected.Count);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogApplyFailed(_logger, ex);
                }
            }

            if (now - lastExport >= TimeSpan.FromMinutes(o.ExportIntervalMinutes))
            {
                lastExport = now;
                try
                {
                    await _exporter.ExportAsync(site, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogExportFailed(_logger, ex);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Critical,
        Message = "Packs:Mode is '{Mode}'. The Kafka->NLDR stream was FROZEN by ADR-0011 (2026-09-13): no counterparty exists in the estate. Set Packs:Mode=packs. Exiting 4.")]
    private static partial void LogStreamRefused(ILogger logger, string mode);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Critical,
        Message = "No usable site pack on this node: {Why}. The sync agent cannot know which society it serves and will not guess. Exiting 4.")]
    private static partial void LogNoSite(ILogger logger, string why);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information,
        Message = "Sync agent up for {PacsId}: ledger packs every {ExportMinutes} min, inbound sweep every {ApplyMinutes} min (ADR-0011).")]
    private static partial void LogStarted(ILogger logger, string pacsId, int exportMinutes, int applyMinutes);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Information,
        Message = "Inbound sweep: {Applied} applied, {Acknowledged} acknowledged (replay), {Waiting} waiting (gap), {Rejected} rejected.")]
    private static partial void LogSweep(ILogger logger, int applied, int acknowledged, int waiting, int rejected);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Error, Message = "Inbound sweep failed; will retry next tick.")]
    private static partial void LogApplyFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Error, Message = "Ledger pack export failed; will retry next tick. The ledger was not advanced.")]
    private static partial void LogExportFailed(ILogger logger, Exception ex);
}
