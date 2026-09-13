using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace SharedKernel.Security;

/// <summary>
/// File-based secret store with AES-256 encryption at rest.
/// Secrets are stored in an encrypted JSON file under D:\ePACSData\keys\secrets.enc.
/// The encryption key is derived from a machine-specific seed + certificate (for portability).
/// </summary>
public sealed partial class SecretStore : ISecretStore
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<SecretStore> _logger;
    private readonly string _secretsFilePath;

    // Patterns that indicate plaintext secrets in content
    private static readonly string[] SecretPatterns =
    [
        "password",
        "secret",
        "private_key",
        "connection_string",
        "api_key",
        "token"
    ];

    public SecretStore(IOptions<InstallerOptions> options, ILogger<SecretStore> logger)
    {
        _options = options;
        _logger = logger;
        _secretsFilePath = Path.Combine(options.Value.DataRoot, "keys", "secrets.enc");
    }

    public string GeneratePassword(int length = 32, bool includeSpecialChars = true)
    {
        const string alphanumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        const string special = "!@#$%^&*()-_=+[]{}|;:,.<>?";
        var chars = includeSpecialChars ? alphanumeric + special : alphanumeric;

        var password = new char[length];
        var randomBytes = RandomNumberGenerator.GetBytes(length);

        for (var i = 0; i < length; i++)
        {
            password[i] = chars[randomBytes[i] % chars.Length];
        }

        return new string(password);
    }

    public async Task StoreAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var secrets = await LoadSecretsAsync(cancellationToken);
        secrets[key] = value;
        await SaveSecretsAsync(secrets, cancellationToken);

        LogSecretStored(_logger, key);
    }

    public async Task<string?> RetrieveAsync(string key, CancellationToken cancellationToken = default)
    {
        var secrets = await LoadSecretsAsync(cancellationToken);
        return secrets.GetValueOrDefault(key);
    }

    public async Task<string> RotateAsync(string key, CancellationToken cancellationToken = default)
    {
        var newValue = GeneratePassword();
        await StoreAsync(key, newValue, cancellationToken);
        LogSecretRotated(_logger, key);
        return newValue;
    }

    public bool ScanForSecrets(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return true; // No content = no secrets
        }

        // Check for patterns that look like plaintext secrets
        return !AnySecretPattern().IsMatch(content);
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        var secrets = await LoadSecretsAsync(cancellationToken);
        return secrets.Keys.ToList();
    }

    private async Task<Dictionary<string, string>> LoadSecretsAsync(CancellationToken ct)
    {
        if (!File.Exists(_secretsFilePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(_secretsFilePath, ct);
            var decryptedBytes = Decrypt(encryptedBytes);
            var json = Encoding.UTF8.GetString(decryptedBytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load secrets file. Returning empty store.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveSecretsAsync(Dictionary<string, string> secrets, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_secretsFilePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(secrets);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encryptedBytes = Encrypt(plainBytes);

        // Atomic write
        var tempPath = _secretsFilePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, encryptedBytes, ct);
        File.Move(tempPath, _secretsFilePath, overwrite: true);
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.GenerateIV();
        aes.Key = DeriveKey();

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(result, 0);
        ciphertext.CopyTo(result, aes.IV.Length);
        return result;
    }

    private byte[] Decrypt(byte[] ciphertextWithIv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;

        // Extract IV from first 16 bytes
        var iv = ciphertextWithIv[..16];
        var ciphertext = ciphertextWithIv[16..];

        aes.IV = iv;
        aes.Key = DeriveKey();

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>
    /// The store's master key: 32 random bytes in <c>keys/master.key</c>, mode 0600, created on
    /// first use.
    ///
    /// WHAT IT REPLACED (2026-09-13). Until then the key was SHA-256 of
    /// <c>"{MachineName}:{DataRoot}:ePACS-SecretStore-v1"</c> — two values readable by anyone
    /// with a shell, so <c>secrets.enc</c>, which holds the database root and application
    /// passwords, was obfuscated rather than encrypted. A random key on disk beside it is not
    /// DPAPI either, but it is the difference between "copy one file" and "copy two files from
    /// a root-only directory", and it is what makes an encrypted backup meaningful: the backup
    /// deliberately EXCLUDES this file, so a backup that carries <c>secrets.enc</c> carries
    /// nothing readable without the node or the state's recovery key.
    /// </summary>
    private byte[] DeriveKey()
    {
        var path = Path.Combine(_options.Value.DataRoot, "keys", "master.key");
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32)
            {
                return existing;
            }

            throw new InvalidOperationException($"{path} exists but is not a 32-byte key. The secret store cannot be opened; nothing has been changed.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(32);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: false);
        LogMasterKeyCreated(_logger, path);
        return key;
    }

    public async Task<byte[]> GetOrCreateKeyAsync(string name, int bytes, CancellationToken cancellationToken = default)
    {
        var existing = await RetrieveAsync(name, cancellationToken);
        if (existing is not null)
        {
            var material = Convert.FromBase64String(existing);
            if (material.Length == bytes)
            {
                return material;
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(bytes);
        await StoreAsync(name, Convert.ToBase64String(fresh), cancellationToken);
        return fresh;
    }

    [LoggerMessage(EventId = 2803, Level = LogLevel.Information, Message = "Secret store master key created at {Path} (0600). Back it up out of band: an encrypted backup deliberately excludes it.")]
    private static partial void LogMasterKeyCreated(ILogger logger, string path);

    [GeneratedRegex(@"(?i)(password|secret|private_key|connection_string|api_key|token)\s*[=:]\s*[""']?[^\s""']{8,}", RegexOptions.Compiled)]
    private static partial Regex AnySecretPattern();

    // Source-generated logging. CA1873 flags the ILogger extension overloads because the
    // params object[] and the boxing of each argument are paid whether or not the level is
    // enabled. The generator emits an IsEnabled guard around the formatting, so the cost is
    // only paid when the message is actually written.
    [LoggerMessage(EventId = 2801, Level = LogLevel.Information, Message = "Secret stored: {Key}.")]
    private static partial void LogSecretStored(ILogger logger, string key);

    [LoggerMessage(EventId = 2802, Level = LogLevel.Information, Message = "Secret rotated: {Key}.")]
    private static partial void LogSecretRotated(ILogger logger, string key);
}
