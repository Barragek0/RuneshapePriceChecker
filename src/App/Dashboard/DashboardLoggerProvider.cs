using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DashboardLoggerProvider(DashboardLogSink sink, string configPath) : ILoggerProvider
{
    private readonly DashboardLogSink _sink = sink;
    private readonly string _configPath = configPath;

    public ILogger CreateLogger(string categoryName)
        => new DashboardLogger(_sink, _configPath);

    public void Dispose() { }
}

internal sealed class DashboardLogger(DashboardLogSink sink, string configPath) : ILogger
{
    private readonly DashboardLogSink _sink = sink;
    private readonly string _configPath = configPath;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var color = logLevel switch
        {
            LogLevel.Warning or LogLevel.Error or LogLevel.Critical => "red",
            LogLevel.Debug => "yellow",
            LogLevel.Trace => "white",
            _ => "green"
        };

        _sink.Emit(message, color, logLevel);
    }

    private LogLevel ReadLogLevel()
    {
        try
        {
            if (!File.Exists(_configPath)) return LogLevel.Information;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            var levelStr = root?["App"]?.Str("LogLevel");
            if (levelStr is not null && Enum.TryParse<LogLevel>(levelStr, ignoreCase: true, out var level))
                return level;
            return LogLevel.Information;
        }
        catch { return LogLevel.Information; }
    }
}
