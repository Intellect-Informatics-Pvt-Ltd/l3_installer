using System.Runtime.InteropServices;

namespace Installer.Actions.Database;

/// <summary>
/// Decides whether this machine can host the estate's database at all, before anything is
/// initialised.
///
/// ── WHY THIS CLASS EXISTS, AND WHY IT IS THE FIRST THING F3 DOES ────────────────────────────
///
/// L2-R2 runs MySQL with <c>lower_case_table_names=0</c>. That is not a preference: it is a
/// deliberate discipline, recorded in <c>ops/compose/docker-compose.localdb.yml</c> and enforced
/// again in <c>ops/ansible/roles/mysqlsvc</c>, because 18 of 20 state captures spell at least
/// one table in a different case from the baseline. On a case-sensitive server those are two
/// tables and the mismatch is loud. On a case-insensitive one they silently collapse into one,
/// and a migration that "worked" is a migration that quietly merged two tables.
///
/// <b>MySQL cannot be initialised with lower_case_table_names=0 on a case-insensitive file
/// system.</b> Since 8.0 the server refuses at initialisation:
/// <c>"The server option 'lower_case_table_names' is configured to use case sensitive table
/// names but the data directory is on a case-insensitive file system which is an unsupported
/// combination."</c> And the setting cannot be changed afterwards — it is fixed at initdb time.
///
/// NTFS is case-insensitive by default. So is APFS on a default macOS install.
///
/// ── WHAT THAT MEANS FOR AN OFFLINE WINDOWS PACS NODE ────────────────────────────────────────
///
/// A Windows node cannot reproduce the production database's case behaviour. It would run with
/// <c>lower_case_table_names=1</c>, folding every table name to lower case. The consequences
/// are not theoretical:
///
///   * The baseline applies "successfully" on the node while collapsing case-differing tables
///     that stay distinct in the state's central Linux database.
///   * A query written and tested at the site works there and fails centrally, or the reverse.
///   * Data synced from the node carries table references that the central schema does not
///     resolve the same way.
///
/// This guard therefore <b>refuses to initialise</b> rather than producing a node whose
/// database is subtly different from every other node in the estate. The override exists so a
/// demo or lab machine is not blocked, and it is deliberately awkward to reach.
///
/// This is the sharpest place the runtime-target decision bites. It is not something the
/// installer can engineer around: it is a property of MySQL on a case-insensitive file system.
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
                    "lower_case_table_names=0 on a case-insensitive file system, and the setting cannot be changed after " +
                    "initialisation. This node's database would fold table names to lower case while every other database " +
                    "in the estate keeps them distinct — the baseline would appear to apply while silently collapsing " +
                    "case-differing tables, and 18 of 20 state captures contain at least one. " +
                    "Refusing to initialise. Use a case-sensitive volume for the data root, or override only for a lab " +
                    "or demo machine whose database will never exchange data with a state."
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
