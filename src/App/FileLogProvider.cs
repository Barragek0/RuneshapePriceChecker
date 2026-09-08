using System.Text;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App;

public sealed class FileLogProvider : ILoggerProvider, IDisposable
{
    private const long MaxLogBytes = 4 * 1024 * 1024;
    private string LogPath { get; }
    private string PreviousPath { get; }
    private FileLogger? _logger;

    // Shared across all FileLogger instances so concurrent writes from different
    // categories don't collide on the same file (FileShare.Read would otherwise
    // fail the second concurrent AppendAllText, silently losing log entries).
    private static readonly SemaphoreSlim _globalLock = new(1, 1);

    public FileLogProvider()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        _ = Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss.fff}-{Guid.NewGuid():N}-log.txt");
        PreviousPath = LogPath + ".previous";
        try { using var stream = new FileStream(LogPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite); } catch { }
    }

    public string CurrentLogPath => LogPath;
    public string PreviousLogPath => PreviousPath;

    public ILogger CreateLogger(string categoryName)
    {
        var logger = new FileLogger(categoryName, this);
        _logger = logger;
        return logger;
    }

    public void Dispose() => _logger?.Dispose();

    private void Write(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        _globalLock.Wait();
        try
        {
            var length = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
            if (length + bytes.LongLength > MaxLogBytes)
            {
                try { if (File.Exists(PreviousPath)) File.Delete(PreviousPath); } catch { }
                try { if (File.Exists(LogPath)) File.Move(LogPath, PreviousPath); } catch { }
                File.WriteAllText(LogPath, "--- log rotated after reaching 4 MiB ---\n", Encoding.UTF8);
            }

            using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: false);
        }
        catch { }
        finally { _globalLock.Release(); }
    }

    private sealed class FileLogger(string category, FileLogProvider provider) : ILogger, IDisposable
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

            provider.Write(line);
        }

        public void Dispose() { }
    }
}
