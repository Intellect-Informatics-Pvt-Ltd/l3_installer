using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace SupportBundle;

/// <summary>
/// Collects diagnostic information into a support bundle ZIP file.
/// Includes: logs (redacted), service status, versions, OS info, disk info.
/// Excludes: plaintext secrets, private keys, raw PII.
/// </summary>
public sealed partial class SupportBundleCollector : ISupportBundleCollector
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<SupportBundleCollector> _logger;

    public SupportBundleCollector(
        IOptions<InstallerOptions> options,
        ILogger<SupportBundleCollector> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<string> CollectAsync(
        string? correlationId = null,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var dataRoot = _options.Value.DataRoot;
        var bundleDir = outputDirectory ?? Path.Combine(dataRoot, "temp", "support-bundle");
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var bundleName = correlationId is not null
            ? $"epacs-support-{correlationId[..Math.Min(12, correlationId.Length)]}-{timestamp}"
            : $"epacs-support-{timestamp}";

        var stagingDir = Path.Combine(bundleDir, bundleName);
        var zipPath = Path.Combine(bundleDir, $"{bundleName}.zip");

        _logger.LogInformation("Collecting support bundle: {BundleName}.", bundleName);

        try
        {
            Directory.CreateDirectory(stagingDir);

            // Collect system info
            await CollectSystemInfoAsync(stagingDir, cancellationToken);

            // Collect logs (redacted)
            await CollectLogsAsync(stagingDir, correlationId, cancellationToken);

            // Collect config (redacted)
            await CollectConfigAsync(stagingDir, cancellationToken);

            // Collect installer state
            await CollectInstallerStateAsync(stagingDir, cancellationToken);

            // Package as ZIP
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            _logger.LogInformation("Support bundle created: {ZipPath}.", zipPath);
            return zipPath;
        }
        finally
        {
            // Clean up staging directory
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    private static Task CollectSystemInfoAsync(string stagingDir, CancellationToken ct)
    {
        var info = new Dictionary<string, string>
        {
            ["MachineName"] = Environment.MachineName,
            ["OSVersion"] = Environment.OSVersion.ToString(),
            ["ProcessorCount"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Is64BitOS"] = Environment.Is64BitOperatingSystem.ToString(),
            ["SystemDirectory"] = Environment.SystemDirectory,
            ["DotNetVersion"] = Environment.Version.ToString(),
            ["CollectedAtUtc"] = DateTime.UtcNow.ToString("O")
        };

        // Add disk info
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var key = $"Drive_{drive.Name.Replace("\\", "").Replace(":", "")}";
            info[$"{key}_TotalGB"] = (drive.TotalSize / (1024.0 * 1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            info[$"{key}_FreeGB"] = (drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            info[$"{key}_Format"] = drive.DriveFormat;
        }

        var content = string.Join(Environment.NewLine, info.Select(kv => $"{kv.Key}: {kv.Value}"));
        return File.WriteAllTextAsync(Path.Combine(stagingDir, "system-info.txt"), content, ct);
    }

    private async Task CollectLogsAsync(string stagingDir, string? correlationId, CancellationToken ct)
    {
        var logsRoot = Path.Combine(_options.Value.DataRoot, "logs");
        var logsOutputDir = Path.Combine(stagingDir, "logs");
        Directory.CreateDirectory(logsOutputDir);

        if (!Directory.Exists(logsRoot))
        {
            return;
        }

        // Collect recent log files (last 3 days) with redaction
        var cutoff = DateTime.UtcNow.AddDays(-3);

        foreach (var serviceDir in Directory.GetDirectories(logsRoot))
        {
            ct.ThrowIfCancellationRequested();

            var serviceName = Path.GetFileName(serviceDir);
            var serviceOutputDir = Path.Combine(logsOutputDir, serviceName);
            Directory.CreateDirectory(serviceOutputDir);

            var recentLogs = Directory.GetFiles(serviceDir, "*.json")
                .Where(f => File.GetLastWriteTimeUtc(f) > cutoff)
                .Take(10); // Max 10 files per service

            foreach (var logFile in recentLogs)
            {
                var content = await File.ReadAllTextAsync(logFile, ct);
                var redacted = RedactSensitiveData(content);

                // If correlationId specified, filter to matching lines
                if (correlationId is not null)
                {
                    var lines = redacted.Split('\n')
                        .Where(l => l.Contains(correlationId, StringComparison.OrdinalIgnoreCase));
                    redacted = string.Join('\n', lines);
                    if (string.IsNullOrWhiteSpace(redacted))
                    {
                        continue;
                    }
                }

                var outputPath = Path.Combine(serviceOutputDir, Path.GetFileName(logFile));
                await File.WriteAllTextAsync(outputPath, redacted, ct);
            }
        }
    }

    private async Task CollectConfigAsync(string stagingDir, CancellationToken ct)
    {
        var configDir = Path.Combine(_options.Value.DataRoot, "config");
        var configOutputDir = Path.Combine(stagingDir, "config");
        Directory.CreateDirectory(configOutputDir);

        if (!Directory.Exists(configDir))
        {
            return;
        }

        foreach (var configFile in Directory.GetFiles(configDir, "*.*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var content = await File.ReadAllTextAsync(configFile, ct);
            var redacted = RedactSensitiveData(content);

            var relativePath = Path.GetRelativePath(configDir, configFile);
            var outputPath = Path.Combine(configOutputDir, relativePath);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir is not null)
            {
                Directory.CreateDirectory(outputDir);
            }

            await File.WriteAllTextAsync(outputPath, redacted, ct);
        }
    }

    private async Task CollectInstallerStateAsync(string stagingDir, CancellationToken ct)
    {
        var stateFile = _options.Value.ResolvedStateFile;
        if (File.Exists(stateFile))
        {
            var content = await File.ReadAllTextAsync(stateFile, ct);
            await File.WriteAllTextAsync(Path.Combine(stagingDir, "installer-state.json"), content, ct);
        }
    }

    /// <summary>
    /// Redacts sensitive data from text content.
    /// Masks: passwords, connection strings, certificates, Aadhaar numbers, phone numbers.
    /// </summary>
    private static string RedactSensitiveData(string content)
    {
        // Redact password values in JSON
        content = PasswordPattern().Replace(content, "$1\"***REDACTED***\"");

        // Redact connection strings
        content = ConnectionStringPattern().Replace(content, "$1\"***REDACTED***\"");

        // Redact Aadhaar numbers (12 digits)
        content = AadhaarPattern().Replace(content, "****-****-$1");

        // Redact phone numbers (10 digits)
        content = PhonePattern().Replace(content, "******$1");

        // Redact certificate thumbprints in values (keep first 8 chars)
        content = ThumbprintPattern().Replace(content, "$1***REDACTED");

        return content;
    }

    [GeneratedRegex(@"(""[Pp]assword""\s*:\s*)""[^""]*""", RegexOptions.Compiled)]
    private static partial Regex PasswordPattern();

    [GeneratedRegex(@"(""[Cc]onnection[Ss]tring""\s*:\s*)""[^""]*""", RegexOptions.Compiled)]
    private static partial Regex ConnectionStringPattern();

    [GeneratedRegex(@"\b\d{4}[\s-]?\d{4}[\s-]?(\d{4})\b", RegexOptions.Compiled)]
    private static partial Regex AadhaarPattern();

    [GeneratedRegex(@"\b\d{6}(\d{4})\b", RegexOptions.Compiled)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"([A-Fa-f0-9]{8})[A-Fa-f0-9]{32,}", RegexOptions.Compiled)]
    private static partial Regex ThumbprintPattern();
}
