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
            var sql = await File.ReadAllTextAsync(file);
            // Split on delimiter-style separators for multi-statement files
            foreach (var statement in SplitStatements(sql))
            {
                if (string.IsNullOrWhiteSpace(statement)) continue;
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = statement;
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        // Simple split on ';' at end of line.  Fine for DDL without procedures.
        var parts = sql.Split(';', StringSplitOptions.RemoveEmptyEntries);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0);
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
