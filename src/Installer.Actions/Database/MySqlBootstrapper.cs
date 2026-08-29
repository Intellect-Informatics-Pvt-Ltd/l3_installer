using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Security;

namespace Installer.Actions.Database;

/// <summary>
/// Brings the bundled MySQL up from nothing. See <see cref="IDatabaseBootstrapper"/> for why
/// this is the centre of the product rather than a detail of it.
///
/// ── THE ORDER, AND WHY IT IS THIS ORDER ─────────────────────────────────────────────────────
///
///   0. Refuse if the file system cannot host lower_case_table_names=0   [before anything]
///   1. Write my.ini                                                     [reversible]
///   2. Initialise the data directory                                    [IRREVERSIBLE]
///   3. Start the server
///   4. Set the root password, from a generated secret
///   5. Create the application and health-check accounts
///   6. Impose db/stable_baseline_ddl.sql
///   7. Count, and fail if the count did not move as declared
///
/// Step 0 is the hinge. lower_case_table_names is fixed at initialisation and can never be
/// changed afterwards, so a node initialised wrongly has to be flattened and redone — and the
/// symptom will not appear for months, when a state's data will not reconcile against it.
///
/// Step 7 is the estate's own rule, borrowed deliberately: <c>ops/README.md</c> states it as
/// "counted, never assumed — this estate has twice had a seed return rc=0 while landing zero
/// rows". A MySQL client that exits 0 having applied nothing is exactly that failure.
/// </summary>
public sealed class MySqlBootstrapper : IDatabaseBootstrapper
{
    private const string RootPasswordSecretKey = "mysql.root.password";
    private const string AppPasswordSecretKey = "mysql.app.password";

    private readonly IOptions<InstallerOptions> _installer;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ISecretStore _secrets;
    private readonly IProcessRunner _runner;
    private readonly ILogger<MySqlBootstrapper> _logger;

    public MySqlBootstrapper(
        IOptions<InstallerOptions> installer,
        IOptions<ServicesOptions> services,
        ISecretStore secrets,
        IProcessRunner runner,
        ILogger<MySqlBootstrapper> logger)
    {
        _installer = installer;
        _services = services;
        _secrets = secrets;
        _runner = runner;
        _logger = logger;
    }

    private MySqlServiceOptions My => _services.Value.MySql;
    private string DataRoot => _installer.Value.DataRoot;
    private string BinRoot => Path.Combine(_installer.Value.BinaryRoot, "current", "mysql", "bin");
    private string DataDirectory => Expand(My.DataDir);
    private string ConfigFilePath => Path.Combine(DataRoot, "mysql", "my.ini");
    private string MysqldPath => Path.Combine(BinRoot, OperatingSystem.IsWindows() ? "mysqld.exe" : "mysqld");
    private string MysqlClientPath => Path.Combine(BinRoot, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");

    /// <summary>
    /// Expands <c>${DataRoot}</c> and normalises separators to the running platform's.
    ///
    /// The defaults in <c>ServicesOptions</c> are written Windows-style
    /// (<c>${DataRoot}\mysql\data</c>) because that is the target. On Linux and macOS a
    /// backslash is an ordinary filename character, so without this the whole path collapses
    /// into one literal directory called <c>\mysql\data</c> — meaning CI would exercise a
    /// different path shape from the machine the product actually installs on, which is the
    /// one thing a cross-platform build must not do.
    /// </summary>
    private string Expand(string value)
    {
        var expanded = value.Replace("${DataRoot}", DataRoot.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        return expanded
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    // ── Plan ─────────────────────────────────────────────────────────────────

    public Task<DatabaseBootstrapPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        var dataDir = DataDirectory;
        var verdict = TableNameCaseGuard.Inspect(dataDir);
        var alreadyInitialised = IsInitialised(dataDir);

        var steps = new List<string>
        {
            $"Check the data volume can host lower_case_table_names=0 — {(verdict.CanHostEstateSetting ? "yes" : "NO")}",
            $"Write {ConfigFilePath}",
            alreadyInitialised
                ? $"SKIP initialise — {dataDir} already holds a MySQL data directory"
                : $"Initialise the data directory at {dataDir} (IRREVERSIBLE)",
            "Start the server on 127.0.0.1 only",
            "Set the root password from a generated secret",
            $"Create '{My.ApplicationUser}' (least privilege on {My.DatabaseName}) and '{My.HealthCheckUser}' (USAGE only)",
            "Impose db/stable_baseline_ddl.sql",
            "Count tables before and after, and fail if the count did not move"
        };

        string? blocker = null;

        if (!verdict.CanHostEstateSetting)
        {
            blocker = verdict.Explanation;
        }
        else if (alreadyInitialised)
        {
            // Not an error, but not a fresh install either. Re-initialising over a populated
            // data directory would destroy a society's books, so the bootstrapper never does it
            // and says so instead of silently skipping.
            blocker = $"{dataDir} already contains a MySQL data directory. This bootstrapper only initialises an empty one — " +
                      "re-initialising would destroy the data it holds. Use repair or restore, or move the existing directory aside deliberately.";
        }

        return Task.FromResult(new DatabaseBootstrapPlan
        {
            CanProceed = blocker is null,
            Blocker = blocker,
            CaseSensitivity = verdict,
            DataDirectory = dataDir,
            ConfigFilePath = ConfigFilePath,
            DataDirectoryAlreadyInitialised = alreadyInitialised,
            Steps = steps
        });
    }

    /// <summary>
    /// A data directory is "initialised" if MySQL's system tablespace is there. Checking for
    /// <c>ibdata1</c> rather than for a non-empty directory: the installer creates the directory
    /// itself in an earlier phase, so "exists and has files" would be true on a fresh node.
    /// </summary>
    private static bool IsInitialised(string dataDir) =>
        Directory.Exists(dataDir) &&
        (File.Exists(Path.Combine(dataDir, "ibdata1")) || Directory.Exists(Path.Combine(dataDir, "mysql")));

    // ── Execute ──────────────────────────────────────────────────────────────

    public async Task<DatabaseBootstrapResult> ExecuteAsync(string baselineDdlPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineDdlPath);

        var plan = await PlanAsync(cancellationToken);
        if (!plan.CanProceed)
        {
            throw new DatabaseBootstrapException(plan.Blocker ?? "Database bootstrap cannot proceed.");
        }

        if (!File.Exists(baselineDdlPath))
        {
            throw new DatabaseBootstrapException(
                $"The baseline schema was not found at {baselineDdlPath}. This file (db/stable_baseline_ddl.sql) is the " +
                "estate's single schema authority; the installer will not invent a schema from module DDL in its absence.");
        }

        var steps = new List<string>();
        var rootPassword = _secrets.GeneratePassword();
        var appPassword = _secrets.GeneratePassword();
        var secrets = new[] { rootPassword, appPassword };

        // 1. Configuration
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
        Directory.CreateDirectory(Expand(My.LogDir));
        await File.WriteAllTextAsync(ConfigFilePath, MyIniWriter.Render(My, DataRoot, plan.CaseSensitivity), cancellationToken);
        steps.Add($"Wrote {ConfigFilePath} (lower_case_table_names={plan.CaseSensitivity.RequiredLowerCaseTableNames}).");
        LogEvents.MySqlConfigWritten(_logger, ConfigFilePath, plan.CaseSensitivity.RequiredLowerCaseTableNames);

        // 2. Initialise — the first irreversible step
        Directory.CreateDirectory(DataDirectory);
        await RunOrThrowAsync(MysqldPath,
            $"--defaults-file=\"{ConfigFilePath}\" --initialize-insecure --console",
            secrets, "MySQL data directory initialisation failed", null, null, cancellationToken);
        steps.Add($"Initialised the data directory at {DataDirectory}.");

        // 3. Start
        await RunOrThrowAsync(MysqldPath,
            $"--defaults-file=\"{ConfigFilePath}\" --daemonize",
            secrets, "MySQL failed to start after initialisation", null, null, cancellationToken);
        steps.Add("Started the server, bound to 127.0.0.1.");

        // 4-5. Credentials and accounts, in one transactional-ish batch through stdin.
        //      Never on the command line: any user on the box can read the process table.
        await ExecuteSqlAsync(BuildAccountsSql(rootPassword, appPassword), rootPassword: null, secrets, cancellationToken);
        await _secrets.StoreAsync(RootPasswordSecretKey, rootPassword, cancellationToken);
        await _secrets.StoreAsync(AppPasswordSecretKey, appPassword, cancellationToken);
        steps.Add($"Set the root password and created '{My.ApplicationUser}' and '{My.HealthCheckUser}'. Credentials are in the secret store, not in any file the operator can read.");

        // 6-7. Impose the baseline, counting before and after.
        var before = await CountTablesAsync(rootPassword, cancellationToken);
        steps.Add($"Census before: {before} table(s) in {My.DatabaseName}.");

        var ddl = await File.ReadAllTextAsync(baselineDdlPath, cancellationToken);
        await ExecuteSqlAsync($"USE `{My.DatabaseName}`;\n{ddl}", rootPassword, secrets, cancellationToken);

        var after = await CountTablesAsync(rootPassword, cancellationToken);
        steps.Add($"Census after: {after} table(s).");

        // THE RULE, from ops/README.md: rc=0 is never the verdict. A MySQL client can exit 0
        // having applied nothing at all - the estate has been bitten by exactly this twice.
        if (after <= before)
        {
            throw new DatabaseBootstrapException(
                $"The baseline was applied and the table count did not move ({before} → {after}). " +
                "The client exited successfully, which is not evidence that anything landed. Nothing further will run " +
                $"against this database. Check the MySQL error log at {Path.Combine(Expand(My.LogDir), "mysql-error.log")}.");
        }

        LogEvents.BaselineImposed(_logger, My.DatabaseName, before, after);
        return new DatabaseBootstrapResult
        {
            Succeeded = true,
            TablesBefore = before,
            TablesAfter = after,
            Steps = steps,
            Message = $"Database ready: {after} tables in {My.DatabaseName}."
        };
    }

    /// <summary>
    /// The accounts, as one script.
    ///
    /// Least privilege throughout. The health-check account gets USAGE and nothing else — it
    /// exists so <c>mysqladmin ping</c> can authenticate, and its credentials sit in a service
    /// definition on disk, so it must not be able to read a member's account balance. The
    /// application account is scoped to the one database and denied DDL: schema changes arrive
    /// through the installer's upgrade path, not from a running service.
    /// </summary>
    private string BuildAccountsSql(string rootPassword, string appPassword)
    {
        var c = CultureInfo.InvariantCulture;
        return string.Create(c, $"""
            ALTER USER 'root'@'localhost' IDENTIFIED BY '{Escape(rootPassword)}';

            CREATE DATABASE IF NOT EXISTS `{My.DatabaseName}`
              CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

            CREATE USER IF NOT EXISTS '{My.ApplicationUser}'@'localhost' IDENTIFIED BY '{Escape(appPassword)}';
            GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, SHOW VIEW
              ON `{My.DatabaseName}`.* TO '{My.ApplicationUser}'@'localhost';

            CREATE USER IF NOT EXISTS '{My.HealthCheckUser}'@'localhost' IDENTIFIED BY '';
            -- USAGE only: enough to connect and answer a ping, and nothing more. This account's
            -- credentials live in service-map.yaml, which is readable on the box.

            DELETE FROM mysql.user WHERE User = '';
            -- Anonymous accounts. MySQL creates none with --initialize-insecure, but a node that
            -- has been touched by hand may have one, and an anonymous account on a books-of-
            -- account database is not a risk anyone should have to remember to check for.

            FLUSH PRIVILEGES;
            """);
    }

    /// <summary>
    /// Escapes a value for a single-quoted MySQL string literal.
    ///
    /// These are generated passwords from <see cref="ISecretStore"/>, not user input, so this is
    /// belt-and-braces rather than the primary defence — but a generated password containing a
    /// quote would otherwise produce a syntax error at the least convenient moment, halfway
    /// through initialising a database.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private async Task<int> CountTablesAsync(string rootPassword, CancellationToken ct)
    {
        var sql = $"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{My.DatabaseName}' AND TABLE_TYPE = 'BASE TABLE';";
        var result = await ExecuteSqlAsync(sql, rootPassword, [rootPassword], ct, batchMode: true);

        var text = result.StandardOutput.Trim().Split('\n').LastOrDefault()?.Trim();
        return int.TryParse(text, CultureInfo.InvariantCulture, out var count)
            ? count
            : throw new DatabaseBootstrapException(
                $"Could not read the table count from MySQL. Output was: '{result.StandardOutput.Trim()}'. " +
                "The count is what proves the schema landed, so the bootstrap stops rather than assuming it did.");
    }

    /// <summary>
    /// Runs SQL through the mysql client.
    ///
    /// The SQL goes in on stdin and the password goes in through MYSQL_PWD, so neither appears
    /// on the command line where the process table would expose it to every user on the box.
    /// </summary>
    private async Task<ProcessResult> ExecuteSqlAsync(
        string sql, string? rootPassword, IReadOnlyCollection<string> secrets, CancellationToken ct, bool batchMode = false)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rootPassword is not null)
        {
            env["MYSQL_PWD"] = rootPassword;
        }

        var args = $"--defaults-file=\"{ConfigFilePath}\" --user=root --protocol=TCP" + (batchMode ? " -N -B" : "");
        return await RunOrThrowAsync(MysqlClientPath, args, secrets, "SQL execution failed", sql, env, ct);
    }

    private async Task<ProcessResult> RunOrThrowAsync(
        string exe, string args, IReadOnlyCollection<string> secrets,
        string failure, string? stdin, IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        if (!File.Exists(exe))
        {
            throw new DatabaseBootstrapException(
                $"{failure}: {exe} was not found. The MySQL payload must be extracted and deployed before the database is bootstrapped.");
        }

        var result = await _runner.RunAsync(exe, args, stdin: stdin, secrets: secrets, environment: environment, cancellationToken: ct);

        return result.Succeeded
            ? result
            : throw new DatabaseBootstrapException($"{failure} (exit {result.ExitCode}). {result.CombinedOutput}");
    }
}
