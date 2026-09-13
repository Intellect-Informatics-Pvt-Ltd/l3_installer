using System.Globalization;
using Installer.Actions.Install;
using Installer.Core.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Packs;
using SharedKernel.Security;

namespace Installer.Core.Packs;

public interface IPolicyPackApplier
{
    /// <summary>Applies one pack directory of the given type. Returns the manifest; throws <see cref="PackException"/> with the refusal named.</summary>
    Task<PackApplyResult> ApplyAsync(string packDirectory, PackType expectedType, SiteConfigPack site, CancellationToken cancellationToken = default);

    /// <summary>Walks the inbound directory in sequence order, applying what can be applied, acknowledging replays, leaving gaps, rejecting the rest with a reason file.</summary>
    Task<InboundSweep> ApplyInboundAsync(SiteConfigPack site, CancellationToken cancellationToken = default);
}

public sealed record PackApplyResult(PackManifest Manifest, IReadOnlyDictionary<string, long> CountsAfter);

public sealed record InboundSweep
{
    public List<string> Applied { get; init; } = [];
    public List<string> Acknowledged { get; init; } = [];
    public List<string> Waiting { get; init; } = [];
    public List<(string Pack, PackRefusal Refusal, string Reason)> Rejected { get; init; } = [];
}

/// <summary>
/// Sync v0, inbound (ADR-0011). Signature before content; sequence and chain against the
/// node's ledger; scope and schema; every data file by hash; then apply, then COUNT — the
/// manifest's row count per table against the database — and only then advance the ledger.
/// A replay is acknowledged and moved aside without touching the database. A gap waits. A
/// refusal moves the pack to <c>rejected/</c> with the reason beside it, so the next person
/// to look at the stick knows what happened without the log.
/// </summary>
public sealed class PolicyPackApplier : IPolicyPackApplier
{
    private readonly IMySqlAccess _mysql;
    private readonly ISchemaFingerprinter _fingerprinter;
    private readonly ICodeSigner _verifier;
    private readonly IOptions<PacksOptions> _packs;
    private readonly IOptions<InstallerOptions> _installer;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ILogger<PolicyPackApplier> _logger;

    public PolicyPackApplier(
        IMySqlAccess mysql,
        ISchemaFingerprinter fingerprinter,
        ICodeSigner verifier,
        IOptions<PacksOptions> packs,
        IOptions<InstallerOptions> installer,
        IOptions<ServicesOptions> services,
        ILogger<PolicyPackApplier> logger)
    {
        _mysql = mysql;
        _fingerprinter = fingerprinter;
        _verifier = verifier;
        _packs = packs;
        _installer = installer;
        _services = services;
        _logger = logger;
    }

    public async Task<PackApplyResult> ApplyAsync(string packDirectory, PackType expectedType, SiteConfigPack site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var o = _packs.Value;
        var tokens = InstallerTokenMap.Merge(InstallerTokenMap.BuildInfrastructure(_installer.Value, _services.Value), InstallerTokenMap.BuildSite(site));
        string Resolve(string s) => InstallerTokenMap.Resolve(s, tokens, "Packs option").Replace('\\', '/');

        var classification = await TableClassification.LoadAsync(Resolve(o.ClassificationPath), cancellationToken);
        var ledger = new PackLedger(Resolve(o.LedgerPath));
        var state = await ledger.LoadAsync(cancellationToken);
        var cursor = PackLedger.NextApplied(state, expectedType);

        // What schema the pack must match. A site data pack is cut by the workspace tooling
        // against the baseline FILE, so it is compared with the hash of the baseline this node
        // imposed; packs between two installers carry the live fingerprint.
        string expectedSchema;
        if (expectedType == PackType.SiteData)
        {
            var baselineRecord = Path.Combine(_installer.Value.DataRoot, "installer", "baseline.sha256");
            if (!File.Exists(baselineRecord))
            {
                throw new PackException(PackRefusal.Schema, $"{packDirectory}: this node has no record of the baseline it imposed ({baselineRecord}); the site data pack cannot be matched to a schema.");
            }

            expectedSchema = (await File.ReadAllTextAsync(baselineRecord, cancellationToken)).Trim();
        }
        else
        {
            var fingerprint = await _fingerprinter.CaptureAsync(await _mysql.ConnectionStringAsync(cancellationToken), _mysql.DatabaseName, cancellationToken);
            expectedSchema = fingerprint.FingerprintHash;
        }

        var manifest = await PackEnvelope.ReadAndVerifyAsync(
            packDirectory,
            _verifier,
            o.InboundSignerThumbprint ?? _installer.Value.ExpectedSigningThumbprint,
            o.RequireSignedInbound,
            site.PacsId,
            cursor.LastSeq + 1,
            cursor.LastHash,
            expectedSchema,
            cancellationToken);

        if (manifest.PackType != expectedType)
        {
            throw new PackException(PackRefusal.Scope, $"{packDirectory}: expected a {expectedType} pack, found {manifest.PackType}.");
        }

        var staging = Path.Combine(_installer.Value.ResolvedTempRoot, "packs", Path.GetFileName(packDirectory.TrimEnd('/', '\\')));
        var privateKey = string.IsNullOrWhiteSpace(o.RecipientPrivateKeyPath) || !File.Exists(o.RecipientPrivateKeyPath)
            ? null
            : await File.ReadAllTextAsync(o.RecipientPrivateKeyPath, cancellationToken);
        var dataDir = await PackEnvelope.OpenDataAsync(packDirectory, manifest, staging, privateKey, cancellationToken);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var table in manifest.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _mysql.ApplySqlAsync(Path.Combine(dataDir, table.Table + ".sql"), cancellationToken);
            }

            // Counted, never assumed: for a society table, this society's rows; for a master,
            // the whole table. A snapshot pack must leave at least what it carried.
            foreach (var table in manifest.Tables)
            {
                var column = classification.Society.GetValueOrDefault(table.Table);
                var after = await _mysql.CountAsync(table.Table, column, column is null ? null : site.PacsId, cancellationToken);
                counts[table.Table] = after;
                if (after < table.Rows)
                {
                    throw new PackException(PackRefusal.Hash,
                        $"{packDirectory}: after applying {table.Table} the database holds {after} row(s) in scope but the pack carried {table.Rows}. " +
                        "The pack was applied and is NOT recorded as applied; the next sweep will apply it again (it is idempotent) and count again.");
                }
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        var hash = await PackEnvelope.ManifestHashAsync(packDirectory, cancellationToken);
        PackLedger.RecordApplied(state, expectedType, manifest.PackSeq, hash, Path.GetFileName(packDirectory));
        await ledger.SaveAsync(state, cancellationToken);
        var packName = Path.GetFileName(packDirectory);
        var typeName = manifest.PackType.ToString();
        var rowsCarried = manifest.Tables.Sum(t => t.Rows);
        LogEvents.PackApplied(_logger, packName, typeName, manifest.PackSeq, manifest.Tables.Count, rowsCarried);
        return new PackApplyResult(manifest, counts);
    }

    public async Task<InboundSweep> ApplyInboundAsync(SiteConfigPack site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var o = _packs.Value;
        var tokens = InstallerTokenMap.Merge(InstallerTokenMap.BuildInfrastructure(_installer.Value, _services.Value), InstallerTokenMap.BuildSite(site));
        var root = InstallerTokenMap.Resolve(o.Root, tokens, "Packs:Root").Replace('\\', '/');
        var inbound = Path.Combine(root, "inbound");
        var applied = Path.Combine(root, "applied");
        var rejected = Path.Combine(root, "rejected");
        Directory.CreateDirectory(inbound);
        Directory.CreateDirectory(applied);
        Directory.CreateDirectory(rejected);

        var sweep = new InboundSweep();

        // In sequence order, by the manifest's own number: the file name is not trusted for it.
        var candidates = new List<(long Seq, string Dir)>();
        foreach (var dir in Directory.GetDirectories(inbound))
        {
            var manifestPath = Path.Combine(dir, PackEnvelope.ManifestFile);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var peek = System.Text.Json.JsonSerializer.Deserialize<PackManifest>(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
                candidates.Add((peek?.PackSeq ?? long.MaxValue, dir));
            }
            catch (System.Text.Json.JsonException)
            {
                candidates.Add((long.MaxValue, dir));
            }
        }

        foreach (var (_, dir) in candidates.OrderBy(c => c.Seq))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            try
            {
                await ApplyAsync(dir, PackType.Policy, site, cancellationToken);
                Move(dir, Path.Combine(applied, name));
                sweep.Applied.Add(name);
            }
            catch (PackException ex) when (ex.Refusal == PackRefusal.Replay)
            {
                Move(dir, Path.Combine(applied, name + ".replay"));
                sweep.Acknowledged.Add(name);
                LogEvents.PackReplayAcknowledged(_logger, name);
            }
            catch (PackException ex) when (ex.Refusal == PackRefusal.Sequence)
            {
                // A gap: the earlier pack has not arrived. Leave it; say so; try again next sweep.
                sweep.Waiting.Add(name);
                LogEvents.PackWaiting(_logger, name, ex.Message);
            }
            catch (PackException ex)
            {
                var target = Path.Combine(rejected, name);
                Move(dir, target);
                await File.WriteAllTextAsync(Path.Combine(target, "REJECTED.txt"), $"{ex.Refusal}: {ex.Message}\n", cancellationToken);
                sweep.Rejected.Add((name, ex.Refusal, ex.Message));
                var refusal = ex.Refusal.ToString();
                LogEvents.PackRejected(_logger, name, refusal, ex.Message);
            }
        }

        return sweep;
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(to))
        {
            Directory.Delete(to, recursive: true);
        }

        Directory.Move(from, to);
    }
}
