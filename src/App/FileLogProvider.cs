using System.Text;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App;

public sealed class FileLogProvider : ILoggerProvider, IDisposable
{
    private readonly string _path;
    private FileLogger? _logger;

    // Shared across all FileLogger instances so concurrent writes from different
    // categories don't collide on the same file (FileShare.Read would otherwise
    // fail the second concurrent AppendAllText, silently losing log entries).
    private static readonly SemaphoreSlim _globalLock = new(1, 1);

    public FileLogProvider()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        _ = Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss.fff}-log.txt");
    }

    public ILogger CreateLogger(string categoryName)
    {
        var logger = new FileLogger(categoryName, _path, _globalLock);
        _logger = logger;
        return logger;
    }

    public void Dispose() => _logger?.Dispose();

    private sealed class FileLogger(string category, string path, SemaphoreSlim globalLock) : ILogger, IDisposable
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {category}: {message}";
            if (exception is not null) line += $"\n{exception}";
            line += '\n';

            globalLock.Wait();
            try
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { }
            finally { globalLock.Release(); }
        }

        public void Dispose() { }
    }
}
