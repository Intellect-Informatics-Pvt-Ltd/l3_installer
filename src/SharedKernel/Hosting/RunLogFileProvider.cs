using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SharedKernel.Hosting;

/// <summary>
/// One log file per run, appended line by line and flushed on every write — so a power cut
/// mid-install leaves a log that ends at the last thing that happened, not an empty buffer.
///
/// WHY NOT SERILOG. The installer runs before any runtime but its own exists on the node, and
/// it stays self-contained on purpose (ADR-0009). A hundred lines here cost less than a package
/// whose file sink would need configuring from a file the installer is about to generate. The
/// support bundle collects this directory (12.4).
/// </summary>
public sealed class RunLogFileProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly LogLevel _minimum;

    public string Path { get; }

    public RunLogFileProvider(string directory, string runName, LogLevel minimum)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{runName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        _minimum = minimum;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly RunLogFileProvider _provider;
        private readonly string _category;

        public FileLogger(RunLogFileProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(' ').Append(Level(logLevel))
              .Append(' ').Append(eventId.Id.ToString(CultureInfo.InvariantCulture).PadLeft(4))
              .Append(' ').Append(_category)
              .Append(" - ").Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.Append(Environment.NewLine).Append(exception);
            }

            _provider.Write(sb.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
