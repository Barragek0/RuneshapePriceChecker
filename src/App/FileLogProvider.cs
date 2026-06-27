using System.Text;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App;

public sealed class FileLogProvider : ILoggerProvider, IDisposable
{
    private readonly string? _path;
    private readonly StringBuilder? _buffer;
    private readonly object? _flushLock;
    private readonly System.Threading.Timer? _flushTimer;
    private volatile bool _disposed;

    private const int FlushIntervalMs = 500;
    private const int MaxBufferSize = 32768;

    public FileLogProvider(LogLevel minLevel)
    {
        // Only write log files when level is Debug or Trace — Information and above
        // produce enough messages to sustain ~1 MB/s of disk writes, which is
        // unnecessary when the dashboard in-memory log is sufficient for normal use.
        if (minLevel > LogLevel.Debug)
            return;

        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        _ = Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss.fff}-log.txt");
        _buffer = new StringBuilder(4096);
        _flushLock = new();
        _flushTimer = new System.Threading.Timer(FlushCallback, null, FlushIntervalMs, FlushIntervalMs);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, this, _path is not null);
    }

    internal void Write(string entry)
    {
        if (_disposed || _buffer is null) return;

        lock (_flushLock!)
        {
            _ = _buffer.Append(entry).AppendLine();
            if (_buffer.Length >= MaxBufferSize)
                FlushToDisk();
        }
    }

    private void FlushCallback(object? _)
    {
        FlushToDisk();
    }

    private void FlushToDisk()
    {
        string? content;
        lock (_flushLock!)
        {
            if (_buffer!.Length == 0) return;
            content = _buffer.ToString();
            _ = _buffer.Clear();
        }

        try
        {
            File.AppendAllText(_path!, content, Encoding.UTF8);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_flushTimer is not null)
            _ = _flushTimer.DisposeAsync();
        if (_buffer is not null)
            FlushToDisk();
    }

    private sealed class FileLogger(string category, FileLogProvider provider, bool enabled) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return enabled;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!enabled) return;
            var message = formatter(state, exception);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {category}: {message}";
            if (exception is not null) line += $"\n{exception}";
            provider.Write(line);
        }
    }
}
