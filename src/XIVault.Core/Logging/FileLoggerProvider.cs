using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace XIVault.Core.Logging;

/// <summary>
/// Daily log files under the XIV Vault data folder, kept for two weeks. Log messages carry paths,
/// counts and results; callers never pass configuration contents.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int DaysToKeep = 14;
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly Lock _gate = new();

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information)
    {
        _directory = directory;
        _minimum = minimum;
        PruneOldFiles();
    }

    public string CurrentFile => Path.Combine(_directory, $"xivault-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break a backup.
            }
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-DaysToKeep);
            foreach (var file in Directory.EnumerateFiles(_directory, "xivault-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 ? category : category[(dot + 1)..];
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(Level(logLevel))
                .Append(' ').Append(category)
                .Append(": ").Append(formatter(state, exception));
            if (exception is not null)
            {
                line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            }

            provider.Write(line.AppendLine().ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
