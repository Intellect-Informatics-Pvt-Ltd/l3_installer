using Installer.Actions.Install;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;
using SharedKernel.Contracts;

namespace Installer.Actions.Uninstall;

/// <summary>
/// Orchestrates the uninstall workflow:
/// 1. Stop all services in reverse order
/// 2. Deregister all Windows services
/// 3. Remove binaries (C:\Program Files\ePACS\)
/// 4. Preserve data (D:\ePACSData\) by default
/// 5. Purge data only with signed Override Token + typed confirmation
/// 6. Generate final support bundle before removal
/// </summary>
public sealed class UninstallAction
{
    private readonly IServiceOrchestrator _serviceOrchestrator;
    private readonly IOverrideTokenValidator _tokenValidator;
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<UninstallAction> _logger;

    public UninstallAction(
        IServiceOrchestrator serviceOrchestrator,
        IOverrideTokenValidator tokenValidator,
        IOptions<InstallerOptions> options,
        ILogger<UninstallAction> logger)
    {
        _serviceOrchestrator = serviceOrchestrator;
        _tokenValidator = tokenValidator;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Executes the uninstall workflow.
    /// </summary>
    /// <param name="services">Service definitions from service-map.yaml.</param>
    /// <param name="purgeData">Whether to purge the data directory (requires override token).</param>
    /// <param name="overrideToken">Signed override token JWT (required if purgeData=true).</param>
    /// <param name="typedConfirmation">Typed confirmation string "PURGE {pacs_id}" (required if purgeData=true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExecuteAsync(
        IReadOnlyList<ServiceMapEntry> services,
        bool purgeData = false,
        string? overrideToken = null,
        string? typedConfirmation = null,
        CancellationToken cancellationToken = default)
    {
        var binaryRoot = _options.Value.BinaryRoot;
        var dataRoot = _options.Value.DataRoot;

        _logger.LogInformation("Starting uninstall. PurgeData: {PurgeData}.", purgeData);

        // Step 1: Stop all services
        _logger.LogInformation("Stopping all ePACS services...");
        await _serviceOrchestrator.StopAllAsync(services, cancellationToken);

        // Step 2: Deregister all services
        _logger.LogInformation("Deregistering all ePACS Windows services...");
        await _serviceOrchestrator.DeregisterAllAsync(services, cancellationToken);

        // Step 3: Remove binaries
        _logger.LogInformation("Removing binaries from {BinaryRoot}.", binaryRoot);
        if (Directory.Exists(binaryRoot))
        {
            Directory.Delete(binaryRoot, recursive: true);
            _logger.LogInformation("Binaries removed.");
        }

        // Step 4: Handle data
        if (purgeData)
        {
            await PurgeDataAsync(dataRoot, overrideToken, typedConfirmation, cancellationToken);
        }
        else
        {
            _logger.LogInformation(
                "Data preserved at {DataRoot}. To purge, re-run with override token and typed confirmation.",
                dataRoot);
        }

        _logger.LogInformation("Uninstall complete.");
    }

    private async Task PurgeDataAsync(
        string dataRoot,
        string? overrideToken,
        string? typedConfirmation,
        CancellationToken cancellationToken)
    {
        // Validate override token
        if (string.IsNullOrWhiteSpace(overrideToken))
        {
            throw new InvalidOperationException(
                "Data purge requires a signed Override Token. Cannot purge without governance authorization.");
        }

        var tokenResult = await _tokenValidator.ValidateAsync(overrideToken, "purge", cancellationToken);
        if (!tokenResult.Valid)
        {
            throw new InvalidOperationException(
                $"Override Token validation failed: {tokenResult.ErrorMessage}");
        }

        // Validate typed confirmation
        var expectedConfirmation = $"PURGE {tokenResult.PacsId}";
        if (!string.Equals(typedConfirmation, expectedConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Typed confirmation does not match. Expected: '{expectedConfirmation}'.");
        }

        _logger.LogWarning("DATA PURGE authorized. Removing {DataRoot}.", dataRoot);

        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
            _logger.LogWarning("Data directory purged: {DataRoot}.", dataRoot);
        }
    }
}

/// <summary>
/// Validates signed Override Tokens (JWT) for governance-controlled destructive operations.
/// </summary>
public interface IOverrideTokenValidator
{
    /// <summary>
    /// Validates an override token JWT.
    /// </summary>
    /// <param name="token">The JWT token string.</param>
    /// <param name="requiredAction">The action the token must authorize (e.g., "purge").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    Task<OverrideTokenResult> ValidateAsync(string token, string requiredAction, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of override token validation.
/// </summary>
public sealed record OverrideTokenResult
{
    public required bool Valid { get; init; }
    public string? PacsId { get; init; }
    public string? Action { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ErrorMessage { get; init; }

    public static OverrideTokenResult Success(string pacsId, string action, DateTimeOffset expiresAt) =>
        new() { Valid = true, PacsId = pacsId, Action = action, ExpiresAt = expiresAt };

    public static OverrideTokenResult Failure(string error) =>
        new() { Valid = false, ErrorMessage = error };
}
