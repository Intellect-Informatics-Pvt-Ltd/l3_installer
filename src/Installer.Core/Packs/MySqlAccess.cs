using System.Globalization;
using Installer.Actions.Database;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SharedKernel.Configuration;
using SharedKernel.Security;

namespace Installer.Core.Packs;

/// <summary>
/// The two ways the pack engines reach MySQL, in one place: MySqlConnector for counts,
/// the GTID position and the fingerprint; the <c>mysqldump</c> / <c>mysql</c> clients (through
/// <see cref="IProcessRunner"/>, password through the environment, never argv) for the rows.
/// </summary>
public interface IMySqlAccess
{
    string DatabaseName { get; }
    Task<string> ConnectionStringAsync(CancellationToken ct);
    Task<long> CountAsync(string table, string? column, string? value, CancellationToken ct);
    Task<string> WatermarkAsync(CancellationToken ct);
    Task DumpRowsAsync(string table, string? column, string? value, string outputFile, CancellationToken ct);
    Task ApplySqlAsync(string sqlFile, CancellationToken ct);
}

public sealed class MySqlAccess : IMySqlAccess
{
    private const string RootPasswordSecretKey = "mysql.root.password";

    private readonly IOptions<InstallerOptions> _installer;
    private readonly IOptions<ServicesOptions> _services;
    private readonly ISecretStore _secrets;
    private readonly IProcessRunner _runner;

    public MySqlAccess(IOptions<InstallerOptions> installer, IOptions<ServicesOptions> services, ISecretStore secrets, IProcessRunner runner)
    {
        _installer = installer;
        _services = services;
        _secrets = secrets;
        _runner = runner;
    }

    public string DatabaseName => _services.Value.MySql.DatabaseName;

    private string Bin(string tool) =>
        Path.Combine(_installer.Value.BinaryRoot, "current", "mysql", "bin", OperatingSystem.IsWindows() ? tool + ".exe" : tool);

    public async Task<string> ConnectionStringAsync(CancellationToken ct)
    {
        var password = await _secrets.RetrieveAsync(RootPasswordSecretKey, ct)
                       ?? throw new InvalidOperationException("The secret store holds no MySQL root password; the database was never bootstrapped on this node.");
        var b = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = (uint)_services.Value.MySql.Port,
            UserID = "root",
            Password = password,
            Database = DatabaseName,
            AllowUserVariables = true
        };
        return b.ConnectionString;
    }

    public async Task<long> CountAsync(string table, string? column, string? value, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = column is null
            ? $"SELECT COUNT(*) FROM `{Escape(table)}`"
            : $"SELECT COUNT(*) FROM `{Escape(table)}` WHERE `{Escape(column)}` = @v";
        if (column is not null)
        {
            cmd.Parameters.AddWithValue("@v", value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The server's GTID set, or the binlog file:position when GTIDs are off, or "none" when binary logging is off.</summary>
    public async Task<string> WatermarkAsync(CancellationToken ct)
    {
        await using var conn = new MySqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using (var gtid = conn.CreateCommand())
        {
            gtid.CommandText = "SELECT @@global.gtid_executed";
            var value = (await gtid.ExecuteScalarAsync(ct))?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return "gtid:" + value;
            }
        }

        try
        {
            await using var status = conn.CreateCommand();
            status.CommandText = "SHOW BINARY LOG STATUS";
            await using var reader = await status.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return $"binlog:{reader.GetString(0)}:{reader.GetValue(1)}";
            }
        }
        catch (MySqlException)
        {
            // Binary logging off, or an older server without the statement. Stated, not invented.
        }

        return "none";
    }

    /// <summary>One table's rows for one society, as idempotent REPLACE statements, to <paramref name="outputFile"/>.</summary>
    public async Task DumpRowsAsync(string table, string? column, string? value, string outputFile, CancellationToken ct)
    {
        var password = await _secrets.RetrieveAsync(RootPasswordSecretKey, ct);
        var where = column is null ? "" : $" --where=\"`{Escape(column)}`='{value?.Replace("'", "''", StringComparison.Ordinal)}'\"";
        var args =
            $"--host=127.0.0.1 --port={_services.Value.MySql.Port.ToString(CultureInfo.InvariantCulture)} --user=root " +
            "--no-create-info --replace --skip-triggers --single-transaction --hex-blob --set-gtid-purged=OFF --skip-comments " +
            $"--result-file=\"{outputFile}\"{where} {DatabaseName} `{Escape(table)}`";
        var env = password is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["MYSQL_PWD"] = password };
        var result = await _runner.RunAsync(Bin("mysqldump"), args, secrets: password is null ? null : [password], environment: env, cancellationToken: ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"mysqldump of {table} failed (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
        }
    }

    /// <summary>Feeds one SQL file to the mysql client, on stdin.</summary>
    public async Task ApplySqlAsync(string sqlFile, CancellationToken ct)
    {
        var password = await _secrets.RetrieveAsync(RootPasswordSecretKey, ct);
        var sql = await File.ReadAllTextAsync(sqlFile, ct);
        var env = password is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["MYSQL_PWD"] = password };
        var result = await _runner.RunAsync(
            Bin("mysql"),
            $"--host=127.0.0.1 --port={_services.Value.MySql.Port.ToString(CultureInfo.InvariantCulture)} --user=root {DatabaseName}",
            stdin: sql, secrets: password is null ? null : [password], environment: env, cancellationToken: ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Applying {Path.GetFileName(sqlFile)} failed (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
        }
    }

    private static string Escape(string identifier) => identifier.Replace("`", "``", StringComparison.Ordinal);
}
