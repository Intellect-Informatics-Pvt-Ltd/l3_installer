namespace SupportBundle;

/// <summary>
/// Collects diagnostic information into a support bundle ZIP.
/// All sensitive data is redacted before inclusion.
/// </summary>
public interface ISupportBundleCollector
{
    /// <summary>
    /// Generates a support bundle containing logs, service status, versions, and system info.
    /// PII and secrets are redacted. Output is a ZIP file.
    /// </summary>
    /// <param name="correlationId">Optional correlation ID to filter logs for a specific operation.</param>
    /// <param name="outputDirectory">Directory to write the bundle ZIP to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path to the generated support bundle ZIP file.</returns>
    Task<string> CollectAsync(
        string? correlationId = null,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default);
}
