using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Prechecks;

/// <summary>
/// Validates that the system has sufficient physical RAM.
/// Thresholds are configurable: minimum (blocking) and recommended (warning).
/// </summary>
public sealed class RamCheck : IPrecheck
{
    private readonly IOptions<PrecheckOptions> _options;
    private readonly ILogger<RamCheck> _logger;

    public RamCheck(IOptions<PrecheckOptions> options, ILogger<RamCheck> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string CheckId => "RAM";
    public string Name => "Physical Memory";
    public int Order => 30;

    public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var totalRamGb = GetTotalPhysicalMemoryGb();
        var minRequired = _options.Value.MinRamGb;
        var recommended = _options.Value.RecommendedRamGb;

        if (totalRamGb < minRequired)
        {
            _logger.LogError("RAM check failed. Detected: {DetectedGb:F1} GB. Required: {RequiredGb} GB.",
                totalRamGb, minRequired);
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Block,
                Message = $"At least {minRequired} GB RAM is required.",
                TechnicalDetail = $"Detected: {totalRamGb:F1} GB. Required: {minRequired} GB.",
                ErrorCode = "ERP-INST-PRE-0005"
            });
        }

        if (totalRamGb < recommended)
        {
            _logger.LogWarning("RAM below recommended. Detected: {DetectedGb:F1} GB. Recommended: {RecommendedGb} GB.",
                totalRamGb, recommended);
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Warning,
                Message = $"{recommended} GB RAM is recommended for optimal performance.",
                TechnicalDetail = $"Detected: {totalRamGb:F1} GB. Recommended: {recommended} GB.",
                ErrorCode = "ERP-INST-PRE-0006"
            });
        }

        _logger.LogInformation("RAM check passed. Detected: {DetectedGb:F1} GB.", totalRamGb);
        return Task.FromResult(new PrecheckResult
        {
            CheckId = CheckId,
            Name = Name,
            Severity = PrecheckSeverity.Pass,
            Message = $"RAM: {totalRamGb:F0} GB — OK.",
            TechnicalDetail = $"Total physical memory: {totalRamGb:F1} GB."
        });
    }

    private static double GetTotalPhysicalMemoryGb()
    {
        // GC.GetGCMemoryInfo gives total available physical memory
        var memInfo = GC.GetGCMemoryInfo();
        return memInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
    }
}
