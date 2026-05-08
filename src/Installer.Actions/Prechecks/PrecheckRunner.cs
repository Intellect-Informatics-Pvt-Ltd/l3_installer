using Microsoft.Extensions.Logging;

namespace Installer.Actions.Prechecks;

/// <summary>
/// Orchestrates execution of all registered prechecks in order.
/// Collects results and determines if installation can proceed.
/// </summary>
public sealed class PrecheckRunner
{
    private readonly IEnumerable<IPrecheck> _prechecks;
    private readonly ILogger<PrecheckRunner> _logger;

    public PrecheckRunner(IEnumerable<IPrecheck> prechecks, ILogger<PrecheckRunner> logger)
    {
        _prechecks = prechecks ?? throw new ArgumentNullException(nameof(prechecks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes all prechecks in order and returns the aggregated result.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregated precheck results.</returns>
    public async Task<PrecheckSuiteResult> RunAllAsync(CancellationToken cancellationToken = default)
    {
        var orderedChecks = _prechecks.OrderBy(c => c.Order).ToList();
        var results = new List<PrecheckResult>(orderedChecks.Count);

        _logger.LogInformation("Starting precheck suite with {CheckCount} checks.", orderedChecks.Count);

        foreach (var check in orderedChecks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation("Running precheck: {CheckName} ({CheckId}).", check.Name, check.CheckId);
                var result = await check.ExecuteAsync(cancellationToken);
                results.Add(result);

                var logLevel = result.Severity switch
                {
                    PrecheckSeverity.Pass => LogLevel.Information,
                    PrecheckSeverity.Warning => LogLevel.Warning,
                    PrecheckSeverity.Block => LogLevel.Error,
                    _ => LogLevel.Information
                };

                _logger.Log(logLevel, "Precheck {CheckId}: {Severity} — {Message}",
                    check.CheckId, result.Severity, result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Precheck {CheckId} threw an exception.", check.CheckId);
                results.Add(new PrecheckResult
                {
                    CheckId = check.CheckId,
                    Name = check.Name,
                    Severity = PrecheckSeverity.Block,
                    Message = $"Precheck failed with error: {ex.Message}",
                    TechnicalDetail = ex.ToString(),
                    ErrorCode = "ERP-CORE-SYS-0001"
                });
            }
        }

        var suiteResult = new PrecheckSuiteResult { Results = results };

        _logger.LogInformation(
            "Precheck suite complete. Passed: {Passed}, Warnings: {Warnings}, Blocking: {Blocking}. Can proceed: {CanProceed}.",
            suiteResult.PassedCount, suiteResult.WarningCount, suiteResult.BlockingCount, suiteResult.CanProceed);

        return suiteResult;
    }
}
