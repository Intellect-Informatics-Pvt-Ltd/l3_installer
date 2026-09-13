using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Installer.Actions.Install;
using Installer.Core.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;
using SharedKernel.Packs;
using SharedKernel.Security;

namespace Installer.Core.Packs;

public interface ILedgerPackExporter
{
    /// <summary>Cuts the next ledger pack for this society into the outbound directory and advances the ledger. Returns its directory and manifest.</summary>
    Task<(string Directory, PackManifest Manifest)> ExportAsync(SiteConfigPack site, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sync v0, outbound (ADR-0011). A ledger pack is a whole, consistent snapshot of the society's
/// scope: every table the classification marks as the society's own, restricted to this
/// society's rows, as idempotent <c>REPLACE</c> statements — so applying a pack twice, or
/// applying pack N after pack N+1 was lost and re-cut, lands the same rows. The GTID position at
/// snapshot time is recorded as the watermark, which is what a future incremental (binlog) v1
/// would cut from; v0 does not need it to be correct, only to be honest.
///
/// Empty tables are counted and skipped (a society touches perhaps a tenth of the 806 tables),
/// so a pack is minutes, not hours. Counts are taken from the database, not from the dump, so
/// what the manifest says is what was there.
/// </summary>
public sealed class LedgerPackExporter : ILedgerPackExporter
{
    private readonly IMySqlAccess _mysql;
    private readonly ISchemaFingerprinter _fingerprinter;
    private readonly IOptions<PacksOptions> _packs;
    private readonly IOptions<InstallerOptions> _installer;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ILogger<LedgerPackExporter> _logger;

    public LedgerPackExporter(
        IMySqlAccess mysql,
        ISchemaFingerprinter fingerprinter,
        IOptions<PacksOptions> packs,
        IOptions<InstallerOptions> installer,
        IOptions<ServicesOptions> services,
        ILogger<LedgerPackExporter> logger)
    {
        _mysql = mysql;
        _fingerprinter = fingerprinter;
        _packs = packs;
        _installer = installer;
        _services = services;
        _logger = logger;
    }

    public async Task<(string Directory, PackManifest Manifest)> ExportAsync(SiteConfigPack site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var o = _packs.Value;
        var tokens = InstallerTokenMap.Merge(InstallerTokenMap.BuildInfrastructure(_installer.Value, _services.Value), InstallerTokenMap.BuildSite(site));
        string Resolve(string s) => InstallerTokenMap.Resolve(s, tokens, "Packs option").Replace('\\', '/');

        var classification = await TableClassification.LoadAsync(Resolve(o.ClassificationPath), cancellationToken);
        var ledger = new PackLedger(Resolve(o.LedgerPath));
        var state = await ledger.LoadAsync(cancellationToken);
        var cursor = PackLedger.NextProduced(state, PackType.Ledger);
        var seq = cursor.LastSeq + 1;
        var packId = $"{site.PacsId}-L-{seq.ToString("D6", CultureInfo.InvariantCulture)}";
        var directory = Path.Combine(Resolve(o.Root), "outbound", packId);
        if (Directory.Exists(directory))
        {
            // A previous attempt died between writing and recording. Cut it again from scratch:
            // the snapshot is whole, so the re-cut supersedes anything half-written.
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(Path.Combine(directory, PackEnvelope.DataDirectory));

        var fingerprint = await _fingerprinter.CaptureAsync(await _mysql.ConnectionStringAsync(cancellationToken), _mysql.DatabaseName, cancellationToken);
        var watermarkFrom = await _mysql.WatermarkAsync(cancellationToken);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long skipped = 0;
        foreach (var (table, column) in classification.Society.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fingerprint.Tables.ContainsKey(table.ToLowerInvariant()))
            {
                // Classified but not in this database: the classification is newer than the
                // node's schema. Stated in the log; the schema fingerprint in the manifest is
                // what the state will refuse on if it matters.
                LogEvents.PackTableAbsent(_logger, table);
                continue;
            }

            var rows = await _mysql.CountAsync(table, column, site.PacsId, cancellationToken);
            if (rows == 0)
            {
                skipped++;
                continue;
            }

            await _mysql.DumpRowsAsync(table, column, site.PacsId, Path.Combine(directory, PackEnvelope.DataDirectory, table + ".sql"), cancellationToken);
            counts[table] = rows;
        }

        var watermarkTo = await _mysql.WatermarkAsync(cancellationToken);

        var manifest = new PackManifest
        {
            PacsId = site.PacsId,
            State = site.StateCode,
            PackType = PackType.Ledger,
            PackSeq = seq,
            PrevPackHash = cursor.LastHash,
            WatermarkFrom = watermarkFrom,
            WatermarkTo = watermarkTo,
            SchemaFingerprint = fingerprint.FingerprintHash,
            ProducedAt = DateTimeOffset.UtcNow,
            ProducerVersion = o.ProducerVersion,
            Tables = []
        };

        using var signer = await SiteSignerAsync(o, cancellationToken);
        var recipient = string.IsNullOrWhiteSpace(o.RecipientPublicKeyPath) ? null : await File.ReadAllTextAsync(Resolve(o.RecipientPublicKeyPath), cancellationToken);
        var written = await PackEnvelope.WriteAsync(directory, manifest, counts, signer?.Signer, recipient, cancellationToken);

        var hash = await PackEnvelope.ManifestHashAsync(directory, cancellationToken);
        PackLedger.RecordProduced(state, PackType.Ledger, seq, hash, packId);
        await ledger.SaveAsync(state, cancellationToken);

        var totalRows = counts.Values.Sum();
        var signing = signer is null ? "UNSIGNED" : "signed";
        var encryptionState = recipient is null ? "clear" : "encrypted";
        LogEvents.LedgerPackCut(_logger, packId, counts.Count, totalRows, skipped, signing, encryptionState);
        return (directory, written);
    }

    /// <summary>The site's signing key, when it has one. A pack without a signature says so, loudly, and the state refuses it.</summary>
    private async Task<SiteSigner?> SiteSignerAsync(PacksOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.SigningPfxPath))
        {
            LogEvents.LedgerPackUnsigned(_logger, "Packs:SigningPfxPath is not set");
            return null;
        }

        if (!File.Exists(o.SigningPfxPath))
        {
            throw new FileNotFoundException("Packs:SigningPfxPath names a file that does not exist. A pack that silently went out unsigned would be refused by the state and nobody here would know why.", o.SigningPfxPath);
        }

        var password = Environment.GetEnvironmentVariable(o.SigningPfxPasswordEnv);
        // EphemeralKeySet is refused on macOS; the default flags keep the key in memory on Linux
        // and Windows anyway for a certificate that is never added to a store.
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var cert = X509CertificateLoader.LoadPkcs12(await File.ReadAllBytesAsync(o.SigningPfxPath, ct), password, flags);
        return new SiteSigner(cert);
    }

    private sealed class SiteSigner : IDisposable
    {
        private readonly X509Certificate2 _cert;

        public SiteSigner(X509Certificate2 cert)
        {
            _cert = cert;
            Signer = new CmsCodeSigner(() => _cert);
        }

        public ICodeSigner Signer { get; }

        public void Dispose() => _cert.Dispose();
    }
}
