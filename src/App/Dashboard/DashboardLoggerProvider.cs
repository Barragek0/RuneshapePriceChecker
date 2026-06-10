using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DashboardLoggerProvider : ILoggerProvider
{
    private readonly DashboardLogSink _sink;
    private readonly string _configPath;

    public DashboardLoggerProvider(DashboardLogSink sink, string configPath)
    {
        _sink = sink;
        _configPath = configPath;
    }

    public ILogger CreateLogger(string categoryName)
        => new DashboardLogger(_sink, _configPath, categoryName);

    public void Dispose() { }
}

internal sealed class DashboardLogger : ILogger
{
    private readonly DashboardLogSink _sink;
    private readonly string _configPath;
    private readonly string _category;

    public DashboardLogger(DashboardLogSink sink, string configPath, string category)
    {
        _sink = sink;
        _configPath = configPath;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel >= LogLevel.Information) return true;
        if (logLevel >= LogLevel.Warning) return true;
        return ReadDebugLogging();
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var color = logLevel switch
        {
            LogLevel.Warning or LogLevel.Error or LogLevel.Critical => "red",
            LogLevel.Debug or LogLevel.Trace => "yellow",
            _ => "green"
        };

        _sink.Emit(message, color);
    }

    private bool ReadDebugLogging()
    {
        try
        {
            if (!File.Exists(_configPath)) return false;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            return root?["App"]?["DebugLogging"]?.GetValue<bool>() ?? false;
        }
        catch { return false; }
    }
}
