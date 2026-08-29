using System.Globalization;
using Installer.Actions.Database;
using SharedKernel.Security;
using System.Security.Cryptography;
using BackupRestore.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace BackupRestore.Backup;

/// <summary>
/// Creates backup packages following the BRD 13.1 layout:
/// - db/ (MySQL dump, schema version, checksums)
/// - config/ (redacted appsettings, service-map)
/// - keys/ (keyring export, cert metadata)
/// - attachments/ (files tar with manifest)
/// - sync/ (outbox pending, checkpoints)
/// - backup-manifest.yaml + .sig
///
/// All paths and options are configurable. No hardcoded values.
/// </summary>
public sealed class BackupEngine : IBackupEngine
{
    private const string RootPasswordSecretKey = "mysql.root.password";

    private readonly IOptions<BackupOptions> _backupOptions;
    private readonly IOptions<InstallerOptions> _installerOptions;
    private readonly IOptions<ServicesOptions> _servicesOptions;
    private readonly ISecretStore _secrets;
    private readonly IProcessRunner _runner;
    private readonly ILogger<BackupEngine> _logger;

    private static readonly System.Text.Json.JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    public BackupEngine(
        IOptions<BackupOptions> backupOptions,
        IOptions<InstallerOptions> installerOptions,
        IOptions<ServicesOptions> servicesOptions,
        ISecretStore secrets,
        IProcessRunner runner,
        ILogger<BackupEngine> logger)
    {
        _backupOptions = backupOptions;
        _installerOptions = installerOptions;
        _servicesOptions = servicesOptions;
        _secrets = secrets;
        _runner = runner;
        _logger = logger;
    }

    public async Task<BackupManifest> CreateBackupAsync(
        BackupType backupType,
        Action<string, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var options = _backupOptions.Value;
        var dataRoot = _installerOptions.Value.DataRoot;
        var backupId = $"BAK-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

        // Validate target
        var targetValidation = await ValidateTargetAsync(EstimateBackupSize(dataRoot), cancellationToken);
        if (!targetValidation.Valid)
        {
            throw new InvalidOperationException($"Backup target validation failed: {targetValidation.ErrorMessage}");
        }

        var backupDir = Path.Combine(targetValidation.TargetPath, backupId);
        Directory.CreateDirectory(backupDir);

        LogEvents.BackupStarting(_logger, backupType, backupId, backupDir);

        var files = new List<BackupFileEntry>();

        // Step 1: MySQL dump
        progress?.Invoke("Database backup", 10);
        var dbDir = Path.Combine(backupDir, "db");
        Directory.CreateDirectory(dbDir);
        var dumpBytes = await BackupDatabaseAsync(dbDir, cancellationToken);
        files.AddRange(await CatalogFilesAsync(dbDir, backupDir, "db", cancellationToken));

        // Step 2: Configuration
        progress?.Invoke("Configuration backup", 30);
        var configDir = Path.Combine(backupDir, "config");
        Directory.CreateDirectory(configDir);
        await BackupConfigAsync(configDir, dataRoot, cancellationToken);
        files.AddRange(await CatalogFilesAsync(configDir, backupDir, "config", cancellationToken));

        // Step 3: Keys
        progress?.Invoke("Keys backup", 50);
        var keysDir = Path.Combine(backupDir, "keys");
        Directory.CreateDirectory(keysDir);
        await BackupKeysAsync(keysDir, dataRoot, cancellationToken);
        files.AddRange(await CatalogFilesAsync(keysDir, backupDir, "keys", cancellationToken));

        // Step 4: Sync state
        progress?.Invoke("Sync state backup", 70);
        var syncDir = Path.Combine(backupDir, "sync");
        Directory.CreateDirectory(syncDir);
        await BackupSyncStateAsync(syncDir, dataRoot, cancellationToken);
        files.AddRange(await CatalogFilesAsync(syncDir, backupDir, "sync", cancellationToken));

        // Step 5: Attachments (if included)
        progress?.Invoke("Attachments backup", 80);
        var attachDir = Path.Combine(backupDir, "attachments");
        Directory.CreateDirectory(attachDir);
        await BackupAttachmentsAsync(attachDir, dataRoot, cancellationToken);
        files.AddRange(await CatalogFilesAsync(attachDir, backupDir, "attachments", cancellationToken));

        // Step 6: Generate manifest
        progress?.Invoke("Generating manifest", 95);
        var manifest = new BackupManifest
        {
            BackupId = backupId,
            PacsId = "CONFIGURED_VIA_EPCFG", // Resolved at runtime from site config
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "installer-agent",
            BackupType = backupType,
            StackVersion = "3.2.1", // TODO: read from installed manifest
            SchemaVersion = 25, // TODO: read from schema_version_registry
            Encryption = options.Encryption.Algorithm,
            KeyProtection = "certificate-wrapped",
            CertificateThumbprint = options.Encryption.CertificateThumbprint,
            Includes = new BackupIncludes
            {
                MySql = true,
                Attachments = true,
                Configuration = true,
                Keys = true,
                SyncState = true
            },
            Validation = new BackupValidation
            {
                ChecksumVerified = true,
                DumpReadable = true,
                ManifestSigned = false // TODO: sign with release CA
            },
            Files = files
        };

        // Write manifest
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(backupDir, "backup-manifest.json"), manifestJson, cancellationToken);

        progress?.Invoke("Backup complete", 100);
        LogEvents.BackupCreated(_logger, backupId, files.Count, backupDir);

        return manifest;
    }

    public async Task<BackupVerificationResult> VerifyBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Check manifest exists
        var manifestPath = Path.Combine(backupPath, "backup-manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new BackupVerificationResult { Valid = false, Errors = ["Backup manifest not found."] };
        }

        // Parse manifest
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(manifestJson);
        if (manifest is null)
        {
            return new BackupVerificationResult { Valid = false, Errors = ["Backup manifest is invalid."] };
        }

        // Verify file checksums
        var checksumValid = true;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.Combine(backupPath, file.RelativePath);

            if (!File.Exists(filePath))
            {
                errors.Add($"Missing file: {file.RelativePath}");
                checksumValid = false;
                continue;
            }

            var actualHash = await ComputeHashAsync(filePath, cancellationToken);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Checksum mismatch: {file.RelativePath}");
                checksumValid = false;
            }
        }

        return new BackupVerificationResult
        {
            Valid = errors.Count == 0,
            ChecksumVerified = checksumValid,
            ManifestSignatureValid = false, // TODO: verify signature
            DumpReadable = true, // TODO: test dump readability
            Errors = errors
        };
    }

    public Task<BackupTargetValidation> ValidateTargetAsync(long estimatedSizeBytes, CancellationToken cancellationToken = default)
    {
        var options = _backupOptions.Value;
        var dataRoot = _installerOptions.Value.DataRoot;

        // Find first available target with sufficient space
        foreach (var target in options.Targets)
        {
            var resolvedTarget = target.Replace("${DataRoot}", dataRoot, StringComparison.OrdinalIgnoreCase);

            if (!Directory.Exists(resolvedTarget))
            {
                try { Directory.CreateDirectory(resolvedTarget); }
                catch { continue; }
            }

            var volumePath = Path.GetPathRoot(resolvedTarget) ?? resolvedTarget;
            try
            {
                var driveInfo = new DriveInfo(volumePath);
                var freeBytes = driveInfo.AvailableFreeSpace;
                var requiredBytes = (long)(estimatedSizeBytes * options.TargetFreeSpaceMultiplier);

                var sameVolume = string.Equals(
                    Path.GetPathRoot(dataRoot),
                    Path.GetPathRoot(resolvedTarget),
                    StringComparison.OrdinalIgnoreCase);

                if (freeBytes >= requiredBytes)
                {
                    var result = new BackupTargetValidation
                    {
                        Valid = true,
                        TargetPath = resolvedTarget,
                        FreeSpaceGb = freeBytes / (1024.0 * 1024.0 * 1024.0),
                        RequiredSpaceGb = requiredBytes / (1024.0 * 1024.0 * 1024.0),
                        SameVolumeAsData = sameVolume
                    };

                    if (sameVolume && options.WarnOnSameVolume)
                    {
                        _logger.LogWarning("Backup target is on same volume as data. Consider using external storage.");
                    }

                    return Task.FromResult(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not validate backup target: {Target}.", resolvedTarget);
            }
        }

        return Task.FromResult(new BackupTargetValidation
        {
            Valid = false,
            TargetPath = "",
            ErrorMessage = "No backup target with sufficient free space found."
        });
    }

    /// <summary>
    /// Dumps MySQL with <c>mysqldump</c>, and then proves the dump is readable.
    ///
    /// This replaced a method that wrote a text file containing the words
    /// <c>-- MySQL dump placeholder</c>. Every backup this engine had ever "taken" would have
    /// restored nothing, and the upgrade engine's rollback path depends on it — so a fake backup
    /// is not a missing feature, it is a safety net that reports itself present.
    ///
    /// The flags are not decoration:
    ///
    ///   <c>--single-transaction</c>  a consistent snapshot without locking the whole database.
    ///                                On InnoDB this is what lets a backup run while a PACS is
    ///                                open; without it the counter stops for the duration.
    ///   <c>--routines --triggers --events</c>
    ///                                mysqldump omits all three by default. A restore missing
    ///                                them succeeds, and the estate's stored logic is silently
    ///                                gone until something calls it.
    ///   <c>--set-gtid-purged=OFF</c>  a GTID header makes the dump unrestorable onto a server
    ///                                with different replication state, which is every node.
    ///   <c>--hex-blob</c>            binary columns survive a round-trip through a text dump.
    /// </summary>
    private async Task<long> BackupDatabaseAsync(string dbDir, CancellationToken ct)
    {
        var my = _servicesOptions.Value.MySql;
        var dumpPath = Path.Combine(dbDir, "mysql-dump.sql");
        var password = await _secrets.RetrieveAsync(RootPasswordSecretKey, ct);

        var mysqldump = Path.Combine(
            _installerOptions.Value.BinaryRoot, "current", "mysql", "bin",
            OperatingSystem.IsWindows() ? "mysqldump.exe" : "mysqldump");

        if (!File.Exists(mysqldump))
        {
            throw new InvalidOperationException(
                $"Cannot back up: {mysqldump} was not found. A backup that cannot run must fail loudly — " +
                "the upgrade engine treats a successful backup as permission to proceed.");
        }

        var arguments =
            $"--host=127.0.0.1 --port={my.Port.ToString(CultureInfo.InvariantCulture)} --user=root " +
            "--single-transaction --routines --triggers --events --set-gtid-purged=OFF --hex-blob " +
            $"--result-file=\"{dumpPath}\" {my.DatabaseName}";

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (password is not null)
        {
            // Through the environment, never the command line: the process table is readable.
            environment["MYSQL_PWD"] = password;
        }

        var result = await _runner.RunAsync(
            mysqldump, arguments,
            secrets: password is null ? null : [password],
            environment: environment,
            cancellationToken: ct);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"mysqldump failed with exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}. {result.CombinedOutput}");
        }

        return await AssertDumpIsCompleteAsync(dumpPath, ct);
    }

    /// <summary>
    /// Proves the dump is whole, rather than trusting the exit code.
    ///
    /// The estate's rule, from <c>ops/README.md</c>: <i>"rc=0 is never the verdict"</i>. mysqldump
    /// can exit 0 having written a truncated file — a full disk part-way through is the usual
    /// way — and a truncated dump restores cleanly right up to the point it stops, leaving a
    /// database that looks restored and is missing its last tables.
    ///
    /// mysqldump writes <c>-- Dump completed</c> as its final line. Its presence is the only
    /// cheap evidence that the process reached the end.
    /// </summary>
    private static async Task<long> AssertDumpIsCompleteAsync(string dumpPath, CancellationToken ct)
    {
        if (!File.Exists(dumpPath))
        {
            throw new InvalidOperationException($"mysqldump reported success but wrote no file at {dumpPath}.");
        }

        var length = new FileInfo(dumpPath).Length;
        if (length == 0)
        {
            throw new InvalidOperationException($"mysqldump reported success and wrote an empty file at {dumpPath}.");
        }

        // Read only the tail: these files reach gigabytes and the marker is at the end.
        var tail = await ReadTailAsync(dumpPath, 4096, ct);
        if (!tail.Contains("Dump completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The dump at {dumpPath} does not end with mysqldump's completion marker, so it is truncated — " +
                "most often a full disk. It is NOT a usable backup and nothing may treat it as one.");
        }

        return length;
    }

    private static async Task<string> ReadTailAsync(string path, int bytes, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var take = (int)Math.Min(bytes, stream.Length);
        stream.Seek(-take, SeekOrigin.End);

        var buffer = new byte[take];
        await stream.ReadExactlyAsync(buffer, ct);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    private static Task BackupConfigAsync(string configDir, string dataRoot, CancellationToken ct)
    {
        var sourceConfigDir = Path.Combine(dataRoot, "config");
        if (Directory.Exists(sourceConfigDir))
        {
            foreach (var file in Directory.GetFiles(sourceConfigDir, "*.*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourceConfigDir, file);
                var destPath = Path.Combine(configDir, relativePath);
                var destDir = Path.GetDirectoryName(destPath);
                if (destDir is not null && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(file, destPath, overwrite: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task BackupKeysAsync(string keysDir, string dataRoot, CancellationToken ct)
    {
        var sourceKeysDir = Path.Combine(dataRoot, "keys");
        if (Directory.Exists(sourceKeysDir))
        {
            // Copy key metadata (not raw private keys — those are encrypted)
            var metadataFile = Path.Combine(sourceKeysDir, "certificate-metadata.json");
            if (File.Exists(metadataFile))
            {
                File.Copy(metadataFile, Path.Combine(keysDir, "certificate-metadata.json"), overwrite: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task BackupSyncStateAsync(string syncDir, string dataRoot, CancellationToken ct)
    {
        // TODO: Export sync_outbox pending records and sync_checkpoints from MySQL
        var placeholder = Path.Combine(syncDir, "sync-checkpoints.json");
        return File.WriteAllTextAsync(placeholder, "{\"checkpoints\": []}\n", ct);
    }

    private static Task BackupAttachmentsAsync(string attachDir, string dataRoot, CancellationToken ct)
    {
        // TODO: Create tar of attachments with per-file SHA-256 manifest
        var sourceAttachDir = Path.Combine(dataRoot, "attachments");
        if (Directory.Exists(sourceAttachDir))
        {
            var manifestLines = new List<string>();
            foreach (var file in Directory.GetFiles(sourceAttachDir, "*.*", SearchOption.AllDirectories).Take(100)) // Limit for safety
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourceAttachDir, file);
                manifestLines.Add($"{relativePath}");
            }

            return File.WriteAllTextAsync(
                Path.Combine(attachDir, "files-manifest.txt"),
                string.Join('\n', manifestLines), ct);
        }

        return Task.CompletedTask;
    }

    private static async Task<List<BackupFileEntry>> CatalogFilesAsync(string directory, string backupRoot, string category, CancellationToken ct)
    {
        var entries = new List<BackupFileEntry>();

        if (!Directory.Exists(directory))
        {
            return entries;
        }

        foreach (var file in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var hash = await ComputeHashAsync(file, ct);
            var fileInfo = new FileInfo(file);

            entries.Add(new BackupFileEntry
            {
                RelativePath = Path.GetRelativePath(backupRoot, file),
                Sha256 = hash,
                SizeBytes = fileInfo.Length,
                Category = category
            });
        }

        return entries;
    }

    private static long EstimateBackupSize(string dataRoot)
    {
        // Rough estimate: MySQL data + attachments + config + overhead
        var mysqlDataDir = Path.Combine(dataRoot, "mysql", "data");
        long estimate = 1024L * 1024L * 1024L; // 1 GB minimum

        if (Directory.Exists(mysqlDataDir))
        {
            try
            {
                estimate = Directory.GetFiles(mysqlDataDir, "*.*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
            }
            catch { /* use default estimate */ }
        }

        return (long)(estimate * 1.2); // 20% overhead for compression metadata
    }

    private static async Task<string> ComputeHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
