using MySqlConnector;

namespace Harness.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the SQL migration files against the test MySQL containers.
/// Files are read from the db/mysql/{pacs,nldr}/ directories relative to the
/// solution root.  In CI these files are embedded as Content items.
/// </summary>
public static class MigratorHelper
{
    // Resolve the db/ folder relative to the test assembly location
    private static string DbRoot => Path.Combine(
        GetSolutionRoot(), "db", "mysql");

    public static Task ApplyPacsMigrationsAsync(string connStr) =>
        ApplyMigrationsAsync(connStr, Path.Combine(DbRoot, "pacs"));

    public static Task ApplyNldrMigrationsAsync(string connStr) =>
        ApplyMigrationsAsync(connStr, Path.Combine(DbRoot, "nldr"));

    private static async Task ApplyMigrationsAsync(string connStr, string folder)
    {
        var files = Directory.GetFiles(folder, "V*.sql")
            .OrderBy(f => f)
            .ToArray();

        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync();

        foreach (var file in files)
        {
            // The WHOLE file, as one multi-statement command - the way the installer applies the same
            // files (Installer.Core/Packs/MySqlAccess.cs feeds each one to the mysql client on stdin), so
            // the server's own parser decides where statements end.
            //
            // This used to split the text on every ';'. V001__core_business.sql and
            // V003__orchestration_compat.sql carry semicolons inside '--' comments ("...canonical
            // OutboxMessages shape; so any platform tooling..."), so the split cut a comment in two and
            // sent its second half to MySQL as a statement: "You have an error in your SQL syntax ...
            // near '' at line 10". The test never got past provisioning - and since the run-settings
            // file was missing too, nobody had seen it fail. (Estate lesson: a quote or a ';' inside a
            // comment is still a character to anything that splits text by hand.)
            var sql = await File.ReadAllTextAsync(file);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (MySqlException ex)
            {
                throw new InvalidOperationException($"Migration {Path.GetFileName(file)} failed: {ex.Message}", ex);
            }
        }
    }

    private static string GetSolutionRoot()
    {
        // Walk up from the test assembly directory to find the harness/ root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ePACS.SyncHarness.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate ePACS.SyncHarness.sln — " +
            "ensure tests are run from within the harness/ directory tree.");
    }
}
