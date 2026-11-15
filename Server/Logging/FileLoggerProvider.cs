using System.Collections.Concurrent;
using System.Text;

namespace Server.Logging;

/// <summary>
/// Copies the server log to one text file per day (data/logs/nox-AAAA-MM-GG.log), with the same lines
/// shown on screen. Only the levels that reach the console are written. Files older than the retention
/// period are deleted at startup and whenever a new day begins, so the disk does not fill up.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    /// <param name="retentionDays">Days of logs to keep; 0 keeps every file.</param>
    public FileLoggerProvider(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Max(0, retentionDays);
        Directory.CreateDirectory(directory);
    }

    /// <summary>Deletes nox-AAAA-MM-GG.log files older than the retention period (by the date in the name).</summary>
    private void DeleteOldFiles(DateOnly today)
    {
        if (_retentionDays == 0) return;
        DateOnly oldestKept = today.AddDays(-(_retentionDays - 1));
        foreach (string file in Directory.EnumerateFiles(_directory, "nox-*.log"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name[4..], "yyyy-MM-dd", out DateOnly day) && day < oldestKept)
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    internal void Write(LogLevel level, string message, Exception? exception)
    {
        string prefix = level switch
        {
            LogLevel.Warning => ServerText.L("[Warning] ", "[Avviso] "),
            LogLevel.Error or LogLevel.Critical => ServerText.L("[Error] ", "[Errore] "),
            _ => string.Empty
        };
        string detail = exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}";
        DateTimeOffset now = DateTimeOffset.Now;
        string line = $"{now:yyyy-MM-dd HH:mm:ss} {prefix}{CompactConsoleFormatter.SingleLine(message + detail)}";

        lock (_gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || today != _day)
                {
                    _writer?.Dispose();
                    _day = today;
                    DeleteOldFiles(today);
                    var stream = new FileStream(Path.Combine(_directory, $"nox-{today:yyyy-MM-dd}.log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // A full or read-only disk must not stop the server: the console still has the line.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Same rule as the console: everything from the server, only warnings and errors from the framework.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None &&
            logLevel >= (category.StartsWith("Microsoft", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;
            provider.Write(logLevel, message, exception);
        }
    }
}
