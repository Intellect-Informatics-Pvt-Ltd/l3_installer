using System.Diagnostics;
using System.Text;

namespace Installer.Actions.Database;

/// <inheritdoc cref="IProcessRunner"/>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        string? workingDirectory = null,
        string? stdin = null,
        IReadOnlyCollection<string>? secrets = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = Redact(stdout.ToString(), secrets),
            StandardError = Redact(stderr.ToString(), secrets)
        };
    }

    /// <summary>
    /// Removes known secrets from captured output before it can reach a log or a support bundle.
    ///
    /// MySQL is unusually good at echoing a password back at you in an error message, and this
    /// output is exactly what an operator pastes into an email when an install fails.
    /// </summary>
    private static string Redact(string text, IReadOnlyCollection<string>? secrets)
    {
        if (secrets is null || string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
        {
            text = text.Replace(secret, "***REDACTED***", StringComparison.Ordinal);
        }

        return text;
    }
}
