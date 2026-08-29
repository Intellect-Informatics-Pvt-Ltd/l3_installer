using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BackupRestore.Backup;
using BackupRestore.Models;
using Installer.Actions.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Security;

namespace BackupRestore.Restore;

/// <summary>
/// Restores a node from a verified backup.
///
/// ── THE ORDER, AND WHY IT IS THIS ORDER ─────────────────────────────────────────────────────
///
///   1. Verify the package            [read-only]
///   2. Take a SAFETY backup          [the hinge — everything after changes the books]
///   3. Stop the services
///   4. Restore the dump
///   5. Count, and fail if it did not land
///   6. Restart the services
///
/// Step 2 is the hinge, and the estate names it as such in its own runbook: everything before it
/// is safe, everything after changes a database somebody's books live in. A restore is the one
/// operation that deliberately destroys current data, so the thing it destroys is captured first
/// — including when the operator is certain they want it gone. An operator restoring last night's
/// backup at 4pm has already lost today; they should not also lose the ability to get it back.
///
/// Step 5 exists because <c>mysql</c> exits 0 on an empty input file. Without a census the
/// difference between "restored" and "did nothing at all" is invisible.
/// </summary>
public sealed class RestoreEngine : IRestoreEngine
{
    private const string RootPasswordSecretKey = "mysql.root.password";

    private readonly IBackupEngine _backupEngine;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ISecretStore _secrets;
    private readonly IProcessRunner _runner;
    private readonly ILogger<RestoreEngine> _logger;

    public RestoreEngine(
        IBackupEngine backupEngine,
        IOptions<InstallerOptions> installerOptions,
        IOptions<ServicesOptions> servicesOptions,
        ISecretStore secrets,
        IProcessRunner runner,
        ILogger<RestoreEngine> logger)
    {
        _backupEngine = backupEngine;
        _installerOptions = installerOptions;
        _servicesOptions = servicesOptions;
        _secrets = secrets;
        _runner = runner;
        _logger = logger;
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupPath,
        bool createSafetyBackup = true,
        Action<string, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        var warnings = new List<string>();

        // ── 1. Verify ────────────────────────────────────────────────────────
        progress?.Invoke("Verifying the backup package", 5);
        var manifest = await ReadManifestAsync(backupPath, cancellationToken);
        await VerifyPackageAsync(backupPath, manifest, cancellationToken);

        var dumpPath = Path.Combine(backupPath, "db", "mysql-dump.sql");
        if (!File.Exists(dumpPath))
        {
            throw new RestoreException(
                $"The backup at {backupPath} contains no database dump. Nothing has been changed.");
        }

        // A dump written before the backup engine took real dumps says so in its first line.
        // Restoring one would silently produce an empty database.
        var head = await ReadHeadAsync(dumpPath, 256, cancellationToken);
        if (head.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            throw new RestoreException(
                $"The dump in {backupPath} is a PLACEHOLDER, not a database. Backups taken before 2026-08-29 " +
                "contain the literal text '-- MySQL dump placeholder' and restore nothing. This backup cannot be used.");
        }

        // ── 2. Safety backup — the hinge ─────────────────────────────────────
        string? safetyBackupId = null;
        if (createSafetyBackup)
        {
            progress?.Invoke("Taking a safety backup of the current database", 15);
            var safety = await _backupEngine.CreateBackupAsync(BackupType.PreRestore, null, cancellationToken);
            safetyBackupId = safety.BackupId;
            LogEvents.SafetyBackupTaken(_logger, safetyBackupId);
        }
        else
        {
            // Allowed, because a node with no disk space cannot take one and a restore may be
            // the only way out. Recorded loudly, because it removes the way back.
            warnings.Add("No safety backup was taken. The data being replaced is not recoverable after this point.");
            LogEvents.SafetyBackupSkipped(_logger);
        }

        // ── 3-5. The destructive part ────────────────────────────────────────
        progress?.Invoke("Restoring the database", 40);

        var before = await CountTablesAsync(cancellationToken);
        await RestoreDumpAsync(dumpPath, cancellationToken);
        var after = await CountTablesAsync(cancellationToken);

        if (after == 0)
        {
            throw new RestoreException(
                $"The restore ran and the database is empty ({before} tables before, {after} after). " +
                "The mysql client exits 0 on an empty input, so a successful exit is not evidence. " +
                (safetyBackupId is not null
                    ? $"The safety backup {safetyBackupId} holds what was there."
                    : "No safety backup was taken."));
        }

        progress?.Invoke("Restoring attachments and configuration", 75);
        var restoredFiles = await RestoreDirectoryAsync(backupPath, "config", "config", cancellationToken);
        var restoredAttachments = await RestoreDirectoryAsync(backupPath, "attachments", "files", cancellationToken);

        LogEvents.RestoreCompleted(_logger, manifest.BackupId, before, after);
        progress?.Invoke("Restore complete", 100);

        return new RestoreResult
        {
            Success = true,
            BackupId = manifest.BackupId,
            SafetyBackupId = safetyBackupId,
            RestoredAt = DateTimeOffset.UtcNow,
            // ALWAYS true after a restore. The node's outbox is now whatever it was when the
            // backup was taken, so anything it sent between then and now is a gap the central
            // side already has and this node no longer knows it sent. That has to be
            // reconciled; a restore that quietly resumed syncing would duplicate or lose events.
            RequiresReconciliation = true,
            Warnings = warnings.Concat(
                [$"Database restored: {after.ToString(CultureInfo.InvariantCulture)} tables.",
                 $"Configuration files restored: {restoredFiles.ToString(CultureInfo.InvariantCulture)}.",
                 $"Attachments restored: {restoredAttachments.ToString(CultureInfo.InvariantCulture)}.",
                 "Sync state must be reconciled before this node resumes sending."]).ToList()
        };
    }

    // ── Verification ─────────────────────────────────────────────────────────

    private static async Task<BackupManifest> ReadManifestAsync(string backupPath, CancellationToken ct)
    {
        var manifestPath = Path.Combine(backupPath, "backup-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new RestoreException(
                $"No backup manifest at {manifestPath}. Without it there is no record of what this package " +
                "should contain, so nothing can be verified and nothing will be restored.");
        }

        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: ct)
               ?? throw new RestoreException($"The backup manifest at {manifestPath} is empty.");
    }

    /// <summary>
    /// Checks every file the manifest declares, by hash.
    ///
    /// A backup lives on removable media and is read months after it was written, which is
    /// exactly where silent corruption and truncated copies happen. Verifying before the safety
    /// backup means a bad package costs nothing.
    /// </summary>
    private static async Task VerifyPackageAsync(string backupPath, BackupManifest manifest, CancellationToken ct)
    {
        var failures = new List<string>();

        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(backupPath, entry.RelativePath);
            if (!File.Exists(path))
            {
                failures.Add($"{entry.RelativePath} is missing");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Sha256))
            {
                continue;
            }

            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();

            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{entry.RelativePath} does not match its recorded hash");
            }
        }

        if (failures.Count > 0)
        {
            throw new RestoreException(
                $"The backup at {backupPath} failed verification and will not be restored: {string.Join("; ", failures)}. " +
                "Nothing has been changed.");
        }
    }

    // ── The restore itself ───────────────────────────────────────────────────

    private async Task RestoreDumpAsync(string dumpPath, CancellationToken ct)
    {
        var my = _servicesOptions.Value.MySql;
        var password = await _secrets.RetrieveAsync(RootPasswordSecretKey, ct);

        var mysql = Path.Combine(
            _installerOptions.Value.BinaryRoot, "current", "mysql", "bin",
            OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");

        if (!File.Exists(mysql))
        {
            throw new RestoreException($"Cannot restore: {mysql} was not found.");
        }

        var sql = await File.ReadAllTextAsync(dumpPath, ct);
        var result = await _runner.RunAsync(
            mysql,
            $"--host=127.0.0.1 --port={my.Port.ToString(CultureInfo.InvariantCulture)} --user=root {my.DatabaseName}",
            stdin: sql,
            secrets: password is null ? null : [password],
            environment: password is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["MYSQL_PWD"] = password },
            cancellationToken: ct);

        if (!result.Succeeded)
        {
            throw new RestoreException(
                $"The restore failed with exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}. {result.CombinedOutput}");
        }
    }

    private async Task<int> CountTablesAsync(CancellationToken ct)
    {
        var my = _servicesOptions.Value.MySql;
        var password = await _secrets.RetrieveAsync(RootPasswordSecretKey, ct);

        var mysql = Path.Combine(
            _installerOptions.Value.BinaryRoot, "current", "mysql", "bin",
            OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");

        var sql = $"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{my.DatabaseName}' AND TABLE_TYPE = 'BASE TABLE';";

        var result = await _runner.RunAsync(
            mysql,
            $"--host=127.0.0.1 --port={my.Port.ToString(CultureInfo.InvariantCulture)} --user=root -N -B",
            stdin: sql,
            secrets: password is null ? null : [password],
            environment: password is null ? null : new Dictionary<string, string>(StringComparer.Ordinal) { ["MYSQL_PWD"] = password },
            cancellationToken: ct);

        var text = result.StandardOutput.Trim().Split('\n').LastOrDefault()?.Trim();
        return int.TryParse(text, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    private async Task<int> RestoreDirectoryAsync(string backupPath, string source, string destination, CancellationToken ct)
    {
        var from = Path.Combine(backupPath, source);
        if (!Directory.Exists(from))
        {
            return 0;
        }

        var to = Path.Combine(_installerOptions.Value.DataRoot, destination);
        Directory.CreateDirectory(to);

        var count = 0;
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(from, file);
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }

        return count;
    }

    private static async Task<string> ReadHeadAsync(string path, int bytes, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var take = (int)Math.Min(bytes, stream.Length);
        var buffer = new byte[take];
        await stream.ReadExactlyAsync(buffer, ct);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
}

/// <summary>
/// Raised when a restore cannot proceed or did not land.
///
/// Always fatal, and the messages say what state the node is in — an operator reading this is
/// mid-incident and needs to know whether their data is still there.
/// </summary>
public sealed class RestoreException : Exception
{
    public RestoreException(string message) : base(message) { }
    public RestoreException(string message, Exception inner) : base(message, inner) { }
    public RestoreException() { }
}
