using Microsoft.Extensions.Logging;
using Spectre.Console;
using XivVault.Core.Backup;

namespace XivVault.Cli.Infrastructure;

/// <summary>Shows Core's log lines on stderr for --verbose. The same lines go to the log file either way.</summary>
internal sealed class ConsoleLoggerProvider(IAnsiConsole console) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(console, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose()
    {
    }

    private sealed class Logger(IAnsiConsole console, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var level = logLevel switch
            {
                LogLevel.Warning => "warn",
                LogLevel.Error or LogLevel.Critical => "fail",
                LogLevel.Debug or LogLevel.Trace => "dbug",
                _ => "info",
            };
            var line = $"{level} {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $" ({exception.GetType().Name}: {exception.Message})";
            }

            console.MarkupLine($"[{CliOutput.Dim}]{Markup.Escape(BackupAllowlist.ForDisplay(line))}[/]");
        }
    }
}
