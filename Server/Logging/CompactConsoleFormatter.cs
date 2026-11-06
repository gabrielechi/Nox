using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Server.Logging;

public sealed class CompactConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "nox";

    public CompactConsoleFormatter() : base(FormatterName) { }

    public override void Write<TState>(in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
            return;

        string level = logEntry.LogLevel switch
        {
            LogLevel.Warning => "[Avviso] ",
            LogLevel.Error or LogLevel.Critical => "[Errore] ",
            _ => string.Empty
        };
        string detail = logEntry.Exception is null
            ? string.Empty
            : $" | {logEntry.Exception.GetType().Name}: {logEntry.Exception.Message}";

        textWriter.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {level}{SingleLine(message + detail)}");
    }

    internal static string SingleLine(string value) =>
        new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
}
