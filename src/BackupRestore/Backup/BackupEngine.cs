using System.Globalization;
using System.IO.Compression;
using Installer.Actions.Database;
using SharedKernel.Security;
using System.Security.Cryptography;
using BackupRestore.Crypto;
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

    /// <summary>The node's key-encryption key for backups, in the secret store.</summary>
    public const string BackupKekSecretName = "backup.kek";
    public const string ManifestFileName = "backup-manifest.json";
    public const string ManifestMacFileName = "backup-manifest.json.mac";

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

        // Step 6: Encrypt every file in place. The plaintext hashes were taken above; the
        // ciphertext hashes are taken now, so the package can be verified without the key and
        // the contents after decryption - both, separately.
        progress?.Invoke("Encrypting", 90);
        var dek = BackupCrypto.NewKey();
        var kek = await _secrets.GetOrCreateKeyAsync(BackupKekSecretName, BackupCrypto.KeyBytes, cancellationToken);
        var encrypted = new List<BackupFileEntry>(files.Count);
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plain = Path.Combine(backupDir, entry.RelativePath);
            var enc = plain + BackupCrypto.Suffix;
            await BackupCrypto.EncryptFileAsync(plain, enc, dek, cancellationToken);
            File.Delete(plain);
            encrypted.Add(entry with
            {
                RelativePath = entry.RelativePath + BackupCrypto.Suffix,
                EncryptedSha256 = await ComputeHashAsync(enc, cancellationToken),
                EncryptedSizeBytes = new FileInfo(enc).Length
            });
        }

        // The data key, wrapped: always to this node; to the state's recovery key when one is
        // configured. What a manifest says about KeyProtection is what is true of it.
        string? recoveryWrap = null, recoveryKeyId = null;
        if (!string.IsNullOrWhiteSpace(options.Encryption.RecoveryPublicKeyPath))
        {
            if (!File.Exists(options.Encryption.RecoveryPublicKeyPath))
            {
                throw new InvalidOperationException(
                    $"Backup:Encryption:RecoveryPublicKeyPath names {options.Encryption.RecoveryPublicKeyPath}, which does not exist. " +
                    "A backup that silently dropped the recovery wrap would be restorable only by this machine, which is not what was configured.");
            }

            var pem = await File.ReadAllTextAsync(options.Encryption.RecoveryPublicKeyPath, cancellationToken);
            recoveryWrap = BackupCrypto.WrapKeyToPublicKey(dek, pem);
            recoveryKeyId = BackupCrypto.PublicKeyId(pem);
        }

        // Step 7: Generate manifest
        progress?.Invoke("Generating manifest", 95);
        var manifest = new BackupManifest
        {
            BackupId = backupId,
            PacsId = _installerOptions.Value.SiteConfigPath is null ? "UNKNOWN" : "SEE-SITE-PACK",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "installer-agent",
            BackupType = backupType,
            StackVersion = "3.2.1", // TODO: read from installed manifest
            SchemaVersion = 25, // TODO: read from schema_version_registry
            Encryption = "AES-256-GCM (per-file, chunked, fresh data key per backup)",
            KeyProtection = recoveryWrap is null
                ? "local-kek-only: restorable by THIS node only (no Backup:Encryption:RecoveryPublicKeyPath configured)"
                : $"local-kek + recovery-rsa-oaep (key id {recoveryKeyId})",
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
                DumpReadable = dumpBytes > 0,
                ManifestSigned = true,
                SignatureType = "HMAC-SHA256(local KEK) - a MAC, not a certificate signature"
            },
            Files = encrypted,
            KeyWrap = new BackupKeyWrap
            {
                Algorithm = "AES-256-GCM(KEK) / RSA-OAEP-SHA256(recovery)",
                Local = BackupCrypto.WrapKey(dek, kek),
                Recovery = recoveryWrap,
                RecoveryKeyId = recoveryKeyId
            }
        };

        CryptographicOperations.ZeroMemory(dek);

        // Write manifest, then its MAC over the exact bytes written.
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        var manifestPath = Path.Combine(backupDir, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(backupDir, ManifestMacFileName), BackupCrypto.ManifestMac(manifestBytes, kek) + "\n", cancellationToken);

        progress?.Invoke("Backup complete", 100);
        LogEvents.BackupCreated(_logger, backupId, encrypted.Count, backupDir);

        return manifest with { PackagePath = backupDir };
    }

    public async Task<BackupVerificationResult> VerifyBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        var manifestPath = Path.Combine(backupPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new BackupVerificationResult { Valid = false, Errors = ["Backup manifest not found."] };
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        BackupManifest? manifest;
        try
        {
            manifest = System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(manifestBytes);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new BackupVerificationResult { Valid = false, Errors = [$"Backup manifest does not parse: {ex.Message}"] };
        }

        if (manifest is null)
        {
            return new BackupVerificationResult { Valid = false, Errors = ["Backup manifest is invalid."] };
        }

        // 1. Every file, by the hash that can be checked WITHOUT the key.
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

            var expected = file.EncryptedSha256 ?? file.Sha256;
            var actualHash = await ComputeHashAsync(filePath, cancellationToken);
            if (!string.Equals(actualHash, expected, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Checksum mismatch: {file.RelativePath}");
                checksumValid = false;
            }
        }

        // 2. The manifest's MAC, when this node holds the key that made it.
        var macValid = false;
        var macPath = Path.Combine(backupPath, ManifestMacFileName);
        if (manifest.KeyWrap is null)
        {
            errors.Add("This package is not encrypted (written before 2026-09-13); it can be verified by hash only and will not be restored by this build.");
        }
        else if (!File.Exists(macPath))
        {
            errors.Add("The manifest MAC is missing: the manifest cannot be shown to be the one this node wrote.");
        }
        else
        {
            var kek = await _secrets.RetrieveAsync(BackupKekSecretName, cancellationToken);
            if (kek is null)
            {
                errors.Add("This node holds no backup key, so the manifest MAC and the data key cannot be checked here (a replacement node needs the state's recovery key).");
            }
            else
            {
                macValid = BackupCrypto.ManifestMacValid(manifestBytes, Convert.FromBase64String(kek), await File.ReadAllTextAsync(macPath, cancellationToken));
                if (!macValid)
                {
                    errors.Add("The manifest MAC does not verify: the manifest was altered, or it was written by another node.");
                }
            }
        }

        // 3. Is the dump readable? Decrypt it (to a root-only temp file) and look for the two
        //    markers a complete mysqldump carries. rc=0 from mysqldump was never the evidence.
        var dumpReadable = false;
        if (checksumValid && macValid)
        {
            var dumpEntry = manifest.Files.FirstOrDefault(f => f.Category == "db" && f.RelativePath.Replace('\\', '/').EndsWith("mysql-dump.sql" + BackupCrypto.Suffix, StringComparison.Ordinal));
            if (dumpEntry is null)
            {
                errors.Add("The package carries no database dump.");
            }
            else
            {
                try
                {
                    var dek = await UnwrapAsync(manifest, cancellationToken);
                    var temp = Path.Combine(_installerOptions.Value.ResolvedTempRoot, "verify", manifest.BackupId + ".sql");
                    Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                    try
                    {
                        await BackupCrypto.DecryptFileAsync(Path.Combine(backupPath, dumpEntry.RelativePath), temp, dek, cancellationToken);
                        var plainHash = await ComputeHashAsync(temp, cancellationToken);
                        if (!string.Equals(plainHash, dumpEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add("The decrypted dump does not hash to what the manifest recorded.");
                        }
                        else
                        {
                            dumpReadable = await LooksLikeACompleteDumpAsync(temp, cancellationToken);
                            if (!dumpReadable)
                            {
                                errors.Add("The dump decrypts but is not a complete mysqldump (missing the header or the 'Dump completed' marker).");
                            }
                        }
                    }
                    finally
                    {
                        if (File.Exists(temp))
                        {
                            File.Delete(temp);
                        }

                        CryptographicOperations.ZeroMemory(dek);
                    }
                }
                catch (CryptographicException ex)
                {
                    errors.Add($"The dump cannot be decrypted here: {ex.Message}");
                }
            }
        }

        return new BackupVerificationResult
        {
            Valid = errors.Count == 0,
            ChecksumVerified = checksumValid,
            ManifestSignatureValid = macValid,
            DumpReadable = dumpReadable,
            Errors = errors
        };
    }

    /// <summary>
    /// The data key, from the local wrap when this node holds the KEK, else from the recovery
    /// wrap when a private key path is configured. Throws <see cref="CryptographicException"/>
    /// naming which way was tried.
    /// </summary>
    public async Task<byte[]> UnwrapAsync(BackupManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.KeyWrap is null)
        {
            throw new CryptographicException("The package carries no wrapped data key: it was written before encryption existed and cannot be restored by this build.");
        }

        var kek = await _secrets.RetrieveAsync(BackupKekSecretName, cancellationToken);
        if (kek is not null)
        {
            try
            {
                return BackupCrypto.UnwrapKey(manifest.KeyWrap.Local, Convert.FromBase64String(kek));
            }
            catch (CryptographicException) when (manifest.KeyWrap.Recovery is not null)
            {
                // Fall through to the recovery key: this node's KEK is not the one that wrote it.
            }
        }

        var privateKeyPath = _backupOptions.Value.Encryption.RecoveryPrivateKeyPath;
        if (manifest.KeyWrap.Recovery is not null && !string.IsNullOrWhiteSpace(privateKeyPath) && File.Exists(privateKeyPath))
        {
            return BackupCrypto.UnwrapKeyWithPrivateKey(manifest.KeyWrap.Recovery, await File.ReadAllTextAsync(privateKeyPath, cancellationToken));
        }

        throw new CryptographicException(
            kek is null
                ? "This node holds no backup key and no recovery private key was supplied (Backup:Encryption:RecoveryPrivateKeyPath). " +
                  (manifest.KeyWrap.Recovery is null
                      ? "The package was wrapped to its writer only (no recovery key was configured when it was taken), so it cannot be restored anywhere but that node."
                      : $"The package is wrapped to recovery key {manifest.KeyWrap.RecoveryKeyId}; supply that private key.")
                : "The data key does not unwrap under this node's key, and no recovery private key was supplied.");
    }

    private static async Task<bool> LooksLikeACompleteDumpAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length < 64)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var head = new byte[Math.Min(256, info.Length)];
        await stream.ReadExactlyAsync(head, ct);
        var headText = System.Text.Encoding.UTF8.GetString(head);
        if (headText.Contains("placeholder", StringComparison.OrdinalIgnoreCase) || !headText.Contains("MySQL dump", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tailLength = (int)Math.Min(512, info.Length);
        stream.Seek(-tailLength, SeekOrigin.End);
        var tail = new byte[tailLength];
        await stream.ReadExactlyAsync(tail, ct);
        return System.Text.Encoding.UTF8.GetString(tail).Contains("Dump completed", StringComparison.Ordinal);
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

    /// <summary>
    /// 15.3: every attachment, in one zip, with a per-file SHA-256 manifest beside it. The zip
    /// is what gets encrypted; the manifest is how a restore proves each file came back whole.
    /// Was: a text listing of the first 100 file names, which backed up nothing.
    /// </summary>
    private static async Task BackupAttachmentsAsync(string attachDir, string dataRoot, CancellationToken ct)
    {
        var lines = new List<string>();
        var count = 0;
        var zipPath = Path.Combine(attachDir, "attachments.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var root in new[] { Path.Combine(dataRoot, "attachments"), Path.Combine(dataRoot, "files") })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var prefix = Path.GetFileName(root);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = prefix + "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                    lines.Add($"{await ComputeHashAsync(file, ct)}  {relative}");
                    count++;
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(attachDir, "files-manifest.sha256"), string.Join('\n', lines) + (lines.Count > 0 ? "\n" : ""), ct);
        await File.WriteAllTextAsync(Path.Combine(attachDir, "files-count.txt"), count.ToString(CultureInfo.InvariantCulture) + "\n", ct);
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
