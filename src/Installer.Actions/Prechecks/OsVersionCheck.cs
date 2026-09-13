using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Prechecks;

/// <summary>
/// The operating system is one the installer has engines for (ADR-0010).
///
/// WHAT IT WAS. Until 2026-09-13 this compared <c>Environment.OSVersion.Version.Build</c> with a
/// Windows build number (17763) on every platform. On Linux that value is the kernel's patch
/// level - 0 on a 6.1.0 kernel - so the check BLOCKED every Debian install with "Windows 10
/// version 1809 or later is required". Nothing in CI ran the pipeline for real on Linux, so
/// nothing said so.
///
/// WHAT IT IS. On Windows: 64-bit and the configured minimum build. On Linux: 64-bit, a
/// systemd-managed Debian or Ubuntu per <c>/etc/os-release</c> (the target ADR-0010 settled),
/// and a kernel of at least the configured major. Anything else is a block that names ADR-0010
/// rather than a Windows version string.
/// </summary>
public sealed class OsVersionCheck : IPrecheck
{
    private readonly IOptions<PrecheckOptions> _options;
    private readonly ILogger<OsVersionCheck> _logger;
    private readonly Func<string?> _osRelease;

    public OsVersionCheck(IOptions<PrecheckOptions> options, ILogger<OsVersionCheck> logger)
        : this(options, logger, () => File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : null)
    {
    }

    /// <summary>Test seam: the contents of /etc/os-release.</summary>
    internal OsVersionCheck(IOptions<PrecheckOptions> options, ILogger<OsVersionCheck> logger, Func<string?> osRelease)
    {
        _options = options;
        _logger = logger;
        _osRelease = osRelease;
    }

    public string CheckId => "OS_VERSION";
    public string Name => "Operating System";
    public int Order => 10;

    public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default) => Task.FromResult(Evaluate());

    internal PrecheckResult Evaluate()
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return Block("ERP-INST-PRE-0002", "A 64-bit operating system is required.", $"Detected: {RuntimeInformation.OSArchitecture}");
        }

        if (OperatingSystem.IsWindows())
        {
            var build = Environment.OSVersion.Version.Build;
            var required = _options.Value.MinOsBuild;
            if (build < required)
            {
                return Block("ERP-INST-PRE-0001", "Windows 10 version 1809 / Server 2019 or later is required.", $"Detected build: {build}. Required: {required}.");
            }

            LogEvents.OsVersionPassed(_logger, build);
            return Pass($"Windows build {build} (x64) — OK.", $"OS: {Environment.OSVersion.VersionString}");
        }

        if (OperatingSystem.IsLinux())
        {
            var release = _osRelease() ?? "";
            var id = Field(release, "ID");
            var idLike = Field(release, "ID_LIKE");
            var pretty = Field(release, "PRETTY_NAME") ?? RuntimeInformation.OSDescription;
            var debianFamily = string.Equals(id, "debian", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(id, "ubuntu", StringComparison.OrdinalIgnoreCase)
                               || (idLike ?? "").Contains("debian", StringComparison.OrdinalIgnoreCase);
            if (!debianFamily)
            {
                return Block("ERP-INST-PRE-0001",
                    "Debian or Ubuntu is required on Linux (ADR-0010): the installer's systemd, nftables and account engines are built for the Debian family.",
                    $"Detected: {pretty} (ID={id ?? "?"}, ID_LIKE={idLike ?? "?"}).");
            }

            var kernel = Environment.OSVersion.Version;
            var minKernel = _options.Value.MinLinuxKernelMajor;
            if (kernel.Major < minKernel)
            {
                return Block("ERP-INST-PRE-0001", $"A Linux kernel of at least {minKernel}.x is required.", $"Detected kernel: {kernel}.");
            }

            if (!Directory.Exists("/run/systemd/system"))
            {
                return Block("ERP-INST-PRE-0001", "systemd is required (ADR-0010): services are registered as systemd units.", $"{pretty} is not running systemd.");
            }

            LogEvents.OsVersionPassed(_logger, kernel.Major);
            return Pass($"{pretty}, kernel {kernel} (x64) — OK.", $"ID={id}, kernel {kernel}, systemd present.");
        }

        return Block("ERP-INST-PRE-0001",
            "ePACS installs on Windows and Debian/Ubuntu only (ADR-0010). Everything up to service registration works here for a dry run; --apply does not.",
            $"Detected: {RuntimeInformation.OSDescription}.");
    }

    private static string? Field(string osRelease, string key)
    {
        foreach (var line in osRelease.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return trimmed[(key.Length + 1)..].Trim('"');
            }
        }

        return null;
    }

    private PrecheckResult Block(string code, string message, string detail) => new()
    {
        CheckId = CheckId, Name = Name, Severity = PrecheckSeverity.Block, Message = message, TechnicalDetail = detail, ErrorCode = code
    };

    private PrecheckResult Pass(string message, string detail) => new()
    {
        CheckId = CheckId, Name = Name, Severity = PrecheckSeverity.Pass, Message = message, TechnicalDetail = detail
    };
}
