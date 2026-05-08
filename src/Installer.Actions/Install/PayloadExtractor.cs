using System.IO.Compression;
using Microsoft.Extensions.Logging;
using SharedKernel.Contracts;

namespace Installer.Actions.Install;

/// <summary>
/// Extracts payload archives (ZIP/TGZ) to the staging directory.
/// Tracks extraction progress for resumability after power-cut.
/// </summary>
public sealed class PayloadExtractor : IPayloadExtractor
{
    private readonly ILogger<PayloadExtractor> _logger;

    public PayloadExtractor(ILogger<PayloadExtractor> logger)
    {
        _logger = logger;
    }

    public async Task ExtractAllAsync(
        ReleaseManifest manifest,
        string sourceDirectory,
        string stagingDirectory,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var payloads = manifest.Payloads.OrderBy(p => p.InstallOrder).ToList();
        var total = payloads.Count;

        _logger.LogInformation("Extracting {Count} payloads to {StagingDir}.", total, stagingDirectory);

        if (!Directory.Exists(stagingDirectory))
        {
            Directory.CreateDirectory(stagingDirectory);
        }

        // Track progress in a manifest file for resumability
        var progressFile = Path.Combine(stagingDirectory, ".extraction-progress.json");
        var completedPayloads = await LoadProgressAsync(progressFile, cancellationToken);

        for (var i = 0; i < payloads.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = payloads[i];

            // Skip already-extracted payloads (resume support)
            if (completedPayloads.Contains(payload.Name))
            {
                _logger.LogInformation("Payload {Name} already extracted. Skipping.", payload.Name);
                progress?.Invoke(i + 1, total, payload.Name);
                continue;
            }

            var sourcePath = Path.Combine(sourceDirectory, payload.File);
            var targetDir = Path.Combine(stagingDirectory, payload.Name);

            _logger.LogInformation("Extracting payload {Index}/{Total}: {Name} ({File}).",
                i + 1, total, payload.Name, payload.File);

            await ExtractPayloadAsync(sourcePath, targetDir, cancellationToken);

            // Record completion for resumability
            completedPayloads.Add(payload.Name);
            await SaveProgressAsync(progressFile, completedPayloads, cancellationToken);

            progress?.Invoke(i + 1, total, payload.Name);
        }

        _logger.LogInformation("All {Count} payloads extracted successfully.", total);
    }

    private static async Task ExtractPayloadAsync(string sourcePath, string targetDir, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Payload file not found: {sourcePath}", sourcePath);
        }

        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.CreateDirectory(targetDir);

        if (sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(sourcePath, targetDir), ct);
        }
        else if (sourcePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                 sourcePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            // For .tgz files, use GZip + Tar extraction
            await ExtractTarGzAsync(sourcePath, targetDir, ct);
        }
        else
        {
            // For .exe or other single files, just copy
            var destFile = Path.Combine(targetDir, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destFile, overwrite: true);
        }
    }

    private static async Task ExtractTarGzAsync(string sourcePath, string targetDir, CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(sourcePath);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gzipStream, targetDir, overwriteFiles: true, ct);
    }

    private static async Task<HashSet<string>> LoadProgressAsync(string progressFile, CancellationToken ct)
    {
        if (!File.Exists(progressFile))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(progressFile, ct);
            var items = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
            return items is not null ? [.. items] : [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task SaveProgressAsync(string progressFile, HashSet<string> completed, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(completed.ToArray());
        var tempPath = progressFile + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, progressFile, overwrite: true);
    }
}
