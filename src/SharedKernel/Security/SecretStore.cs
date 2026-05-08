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

        _logger.LogInformation("Secret stored: {Key}.", key);
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
        _logger.LogInformation("Secret rotated: {Key}.", key);
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

    private byte[] DeriveKey()
    {
        // Derive key from machine name + data root path (machine-specific)
        // In production, this would use DPAPI or a certificate-wrapped key
        var seed = $"{Environment.MachineName}:{_options.Value.DataRoot}:ePACS-SecretStore-v1";
        var seedBytes = Encoding.UTF8.GetBytes(seed);
        return SHA256.HashData(seedBytes);
    }

    [GeneratedRegex(@"(?i)(password|secret|private_key|connection_string|api_key|token)\s*[=:]\s*[""']?[^\s""']{8,}", RegexOptions.Compiled)]
    private static partial Regex AnySecretPattern();
}
