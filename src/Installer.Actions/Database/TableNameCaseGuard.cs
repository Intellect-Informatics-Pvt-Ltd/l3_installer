using System.Runtime.InteropServices;

namespace Installer.Actions.Database;

/// <summary>
/// Decides whether this machine can host the estate's database shape, before anything is
/// initialised.
///
/// ── WHAT IS ACTUALLY AT STAKE ───────────────────────────────────────────────────────────────
///
/// <b>Corrected 2026-08-29 after measuring, because the first version of this comment overstated
/// it.</b> The claim was that a case-folding server would collapse case-differing tables while
/// applying the baseline. Measured: <c>db/stable_baseline_ddl.sql</c> declares 1,189 tables and
/// <b>none of them collide when folded to lower case</b>. Imposing the baseline on a
/// <c>lower_case_table_names=1</c> server produces the same 1,189 tables.
///
/// The estate's "18 of 20 captures" figure is about something else: one table, <c>DB_Names</c>
/// vs <c>db_names</c>, where an existing STATE CAPTURE disagreed with the baseline's spelling
/// and produced <c>ERROR 1146</c> when migrated on a Linux server. That is a migration concern,
/// it was fixed, and a fresh PACS node does not migrate a capture — it imposes the baseline.
///
/// ── SO WHY STILL REFUSE BY DEFAULT ──────────────────────────────────────────────────────────
///
/// Because what remains is a permanent, invisible divergence, and the setting cannot be changed
/// after initialisation. On <c>lower_case_table_names=1</c> MySQL <b>stores</b> identifiers
/// folded, so the node's own <c>information_schema</c> reports <c>db_names</c> where every Linux
/// node reports <c>DB_Names</c>. That has consequences the installer cannot fix later:
///
///   * <b>Schema fingerprinting</b> (tasks.md §19) compares a node against the baseline. Every
///     mixed-case identifier looks like drift that is not drift, so either the fingerprint
///     normalises case — and then stops detecting real case drift — or it reports noise.
///   * <b>Backup and restore across platforms.</b> A dump taken from this node and restored to a
///     Linux server carries folded names; the reverse carries names this node will fold on
///     import. Neither direction fails loudly.
///   * <b>Case errors in SQL are masked here.</b> A query referencing <c>voucherdetails</c> works
///     on this node and fails on every Linux one. Nothing authored at a PACS site should reach
///     a state server, but "should not" is not a control.
///
/// None of that is fatal, and none of it is discoverable a year later without effort. So the
/// installer makes it a <b>decision</b> rather than a default: refused unless explicitly
/// accepted, because an irreversible divergence chosen by accident is the failure worth
/// preventing here.
///
/// ── THE UNDERLYING CONSTRAINT, WHICH IS NOT NEGOTIABLE ──────────────────────────────────────
///
/// MySQL refuses to initialise with <c>lower_case_table_names=0</c> on a case-insensitive file
/// system, and the value is fixed at initialisation. NTFS is case-insensitive by default; so is
/// APFS on a default macOS install. This is a property of MySQL, not something the installer or
/// the packaging can work around.
/// </summary>
public static class TableNameCaseGuard
{
    /// <summary>The value the estate's production databases are initialised with.</summary>
    public const int EstateLowerCaseTableNames = 0;

    /// <summary>
    /// Determines whether the data directory can host a <c>lower_case_table_names=0</c> server.
    /// </summary>
    /// <param name="dataDirectory">
    /// The intended MySQL data directory. Tested by experiment rather than by assuming from the
    /// OS: a Windows machine may have a case-sensitive volume enabled per-directory, and a Linux
    /// one may be pointed at a case-insensitive mount. What matters is the actual behaviour of
    /// the path the data will live on.
    /// </param>
    public static CaseSensitivityVerdict Inspect(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var probeRoot = Directory.Exists(dataDirectory)
            ? dataDirectory
            : Path.GetDirectoryName(Path.GetFullPath(dataDirectory)) ?? Path.GetTempPath();

        if (!Directory.Exists(probeRoot))
        {
            Directory.CreateDirectory(probeRoot);
        }

        var caseSensitive = ProbeCaseSensitivity(probeRoot);

        return caseSensitive
            ? new CaseSensitivityVerdict
            {
                CanHostEstateSetting = true,
                FileSystemIsCaseSensitive = true,
                Explanation = $"{probeRoot} is case-sensitive; MySQL can be initialised with lower_case_table_names=0, matching every other database in the estate."
            }
            : new CaseSensitivityVerdict
            {
                CanHostEstateSetting = false,
                FileSystemIsCaseSensitive = false,
                Explanation =
                    $"{probeRoot} is CASE-INSENSITIVE ({RuntimeInformation.OSDescription}). MySQL refuses to initialise with " +
                    "lower_case_table_names=0 there, and the value is fixed at initialisation — it cannot be changed later. " +
                    "This node would therefore STORE table names folded to lower case, while every Linux node in the estate " +
                    "stores them as written. The baseline itself applies cleanly either way (its 1,189 table names do not " +
                    "collide when folded), so nothing fails today; what you get is a permanent divergence that makes schema " +
                    "fingerprinting report case as drift, makes cross-platform dump/restore lossy in both directions, and " +
                    "masks SQL case errors that would fail on a Linux server. " +
                    "Refused by default because it is irreversible — accept it deliberately with AcceptCaseFolding, or put " +
                    "the data root on a case-sensitive volume."
            };
    }

    /// <summary>
    /// Probes by experiment: write one file, try to read it back under a different case.
    ///
    /// Deliberately not inferred from <c>RuntimeInformation.IsOSPlatform</c>. Windows supports
    /// per-directory case sensitivity (<c>fsutil file setCaseSensitiveInfo</c>), Linux can mount
    /// a case-insensitive volume, and a bind mount or network share can be either. The only
    /// trustworthy answer for the path the data will actually live on is to try it.
    /// </summary>
    private static bool ProbeCaseSensitivity(string directory)
    {
        var stem = $".epacs-case-probe-{Guid.NewGuid():N}";
        var lower = Path.Combine(directory, stem + ".tmp");
        var upper = Path.Combine(directory, stem.ToUpperInvariant() + ".TMP");

        try
        {
            File.WriteAllText(lower, "probe");
            // If the upper-case name resolves to the file we just wrote, the path folds case.
            return !File.Exists(upper);
        }
#pragma warning disable CA1031 // A probe that cannot run must not be read as "case-sensitive":
        catch (Exception) // assuming the safe answer here would let the guard pass by accident.
#pragma warning restore CA1031
        {
            return false;
        }
        finally
        {
            TryDelete(lower);
            TryDelete(upper);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

/// <summary>The outcome of inspecting a prospective data directory.</summary>
public sealed record CaseSensitivityVerdict
{
    /// <summary>True when MySQL here can be initialised the way the estate initialises it.</summary>
    public required bool CanHostEstateSetting { get; init; }

    public required bool FileSystemIsCaseSensitive { get; init; }

    /// <summary>Operator-facing explanation. Always populated, including on success.</summary>
    public required string Explanation { get; init; }

    /// <summary>
    /// The value MySQL would have to be initialised with here. 0 where the estate's setting is
    /// possible; 1 where the file system forces case folding.
    /// </summary>
    public int RequiredLowerCaseTableNames => FileSystemIsCaseSensitive ? 0 : 1;
}
