using System.Text;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App;

public sealed class FileLogProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _writeLock = new();

    public FileLogProvider()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-log.txt");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Write(string entry)
    {
        lock (_writeLock)
        {
            try { File.AppendAllText(_path, entry + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(string category, FileLogProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel.ToString()[..4]}] {category}: {message}";
            if (exception is not null) line += $"\n{exception}";
            provider.Write(line);
        }
    }
}
