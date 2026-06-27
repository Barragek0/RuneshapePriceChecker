using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DashboardLoggerProvider(DashboardLogSink sink) : ILoggerProvider
{
    private readonly DashboardLogSink _sink = sink;

    public ILogger CreateLogger(string categoryName) => new DashboardLogger(_sink);

    public void Dispose() => _sink.Dispose();
}

internal sealed class DashboardLogger(DashboardLogSink sink) : ILogger
{
    private readonly DashboardLogSink _sink = sink;

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
            LogLevel.Information => "green",
            LogLevel.None => "white",
            _ => "green"
        };

        _sink.Emit(message, color, logLevel);
    }
}
