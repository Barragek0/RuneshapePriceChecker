using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DashboardLogSink : IDisposable
{
    private const int MaxEntries = 1000;
    private const int FlushIntervalMs = 50;
    private readonly LinkedList<LogEntry> _recent = new();
    private readonly List<LogEntry> _pending = [];
    private readonly object _pendingLock = new();
    private readonly Timer _flushTimer;

    public event Action<LogEntry>? OnLogEntry;

    public DashboardLogSink()
    {
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Emit(string message, string color = "default", LogLevel logLevel = LogLevel.Information)
    {
        var now = DateTime.Now;

        lock (_pendingLock)
        {
            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Message, message, StringComparison.Ordinal))
                {
                    _recent.Remove(node);
                    var updated = node.Value with { Count = node.Value.Count + 1, Timestamp = now };
                    _recent.AddFirst(updated);
                    _pending.Add(updated);
                    _flushTimer.Change(FlushIntervalMs, Timeout.Infinite);
                    return;
                }
            }

            var entry = new LogEntry(now, message, color, 1, logLevel);
            _recent.AddFirst(entry);
            while (_recent.Count > MaxEntries)
                _recent.RemoveLast();

            _pending.Add(entry);
            _flushTimer.Change(FlushIntervalMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        LogEntry[] batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        Array.Sort(batch, (a, b) => b.Timestamp.CompareTo(a.Timestamp));

        foreach (var entry in batch)
            OnLogEntry?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_pendingLock)
        {
            return _recent.ToArray();
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }
}

public sealed record LogEntry(DateTime Timestamp, string Message, string Color, int Count, LogLevel LogLevel)
{
    public string DisplayText => Count > 1
        ? $"{Timestamp:HH:mm:ss}  {Message}  (x{Count})"
        : $"{Timestamp:HH:mm:ss}  {Message}";
}
