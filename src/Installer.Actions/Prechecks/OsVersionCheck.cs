using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Prechecks;

/// <summary>
/// Validates that the OS version meets minimum requirements.
/// Checks: Windows 10/11/Server 2019+ (build >= configurable threshold), x64 architecture.
/// </summary>
public sealed class OsVersionCheck : IPrecheck
{
    private readonly IOptions<PrecheckOptions> _options;
    private readonly ILogger<OsVersionCheck> _logger;

    public OsVersionCheck(IOptions<PrecheckOptions> options, ILogger<OsVersionCheck> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string CheckId => "OS_VERSION";
    public string Name => "Operating System Version";
    public int Order => 10;

    public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var osVersion = Environment.OSVersion;
        var currentBuild = osVersion.Version.Build;
        var requiredBuild = _options.Value.MinOsBuild;
        var is64Bit = Environment.Is64BitOperatingSystem;

        if (!is64Bit)
        {
            _logger.LogError("Architecture check failed: 32-bit OS detected.");
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Block,
                Message = "64-bit Windows is required.",
                TechnicalDetail = $"Detected: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}",
                ErrorCode = "ERP-INST-PRE-0002"
            });
        }

        if (currentBuild < requiredBuild)
        {
            _logger.LogError("OS version check failed. Build {CurrentBuild} < required {RequiredBuild}.",
                currentBuild, requiredBuild);
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Block,
                Message = "Windows 10 version 1809 or later is required.",
                TechnicalDetail = $"Detected build: {currentBuild}. Required: {requiredBuild}.",
                ErrorCode = "ERP-INST-PRE-0001"
            });
        }

        _logger.LogInformation("OS version check passed. Build: {Build}, Architecture: x64.", currentBuild);
        return Task.FromResult(new PrecheckResult
        {
            CheckId = CheckId,
            Name = Name,
            Severity = PrecheckSeverity.Pass,
            Message = $"Windows build {currentBuild} (x64) — OK.",
            TechnicalDetail = $"OS: {osVersion.VersionString}"
        });
    }
}
