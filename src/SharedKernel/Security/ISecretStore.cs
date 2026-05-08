namespace SharedKernel.Security;

/// <summary>
/// Manages secrets (passwords, keys, tokens) for ePACS services.
/// Secrets are encrypted at rest and never stored in plaintext.
/// Supports generation, rotation, and retrieval.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Generates a cryptographically secure random password.
    /// </summary>
    /// <param name="length">Password length (default: 32).</param>
    /// <param name="includeSpecialChars">Whether to include special characters.</param>
    /// <returns>Generated password.</returns>
    string GeneratePassword(int length = 32, bool includeSpecialChars = true);

    /// <summary>
    /// Stores a secret encrypted at rest.
    /// </summary>
    /// <param name="key">Secret identifier (e.g., "mysql_root_password").</param>
    /// <param name="value">Secret value to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a stored secret (decrypted).
    /// </summary>
    /// <param name="key">Secret identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Decrypted secret value, or null if not found.</returns>
    Task<string?> RetrieveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates a secret (generates new value, stores it, returns new value).
    /// </summary>
    /// <param name="key">Secret identifier to rotate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New secret value.</returns>
    Task<string> RotateAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that no plaintext secrets exist in a file or string.
    /// Used by support bundle collector and config drift detector.
    /// </summary>
    /// <param name="content">Content to scan.</param>
    /// <returns>True if no secrets detected; false if potential plaintext secrets found.</returns>
    bool ScanForSecrets(string content);

    /// <summary>
    /// Lists all stored secret keys (not values).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of secret key identifiers.</returns>
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default);
}
