using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.App;

public sealed class CompactConsoleFormatter(IOptionsMonitor<SimpleConsoleFormatterOptions> options) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "runeshapepricechecker-compact";

    private readonly IOptionsMonitor<SimpleConsoleFormatterOptions> _options = options;

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var formatter = logEntry.Formatter;
        if (formatter is null)
        {
            return;
        }

        var message = formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var options = _options.CurrentValue;
        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(options.TimestampFormat))
        {
            _ = builder.Append(DateTimeOffset.Now.ToString(options.TimestampFormat, CultureInfo.InvariantCulture));
        }

        _ = builder.Append(GetLogLevelText(logEntry.LogLevel));
        _ = builder.Append(" | ");
        _ = builder.AppendLine(message);

        if (logEntry.Exception is not null)
        {
            _ = builder.AppendLine(logEntry.Exception.ToString());
        }

        var originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = GetLogLevelColor(logEntry.LogLevel);
        }
        catch
        {
            // Console may be unavailable in some contexts
        }

        textWriter.Write(builder.ToString());

        try
        {
            Console.ForegroundColor = originalColor;
        }
        catch
        {
        }
    }

    // IDE0051: Used via reflection by tests
#pragma warning disable IDE0051
    private static string ShortenCategory(string category)
    {
        if (category.StartsWith("RuneshapePriceChecker.", StringComparison.Ordinal))
        {
            return category["RuneshapePriceChecker.".Length..];
        }

        if (category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal))
        {
            return "HttpClient." + category["System.Net.Http.HttpClient.".Length..];
        }

        if (category.StartsWith("System.Net.Http.", StringComparison.Ordinal))
        {
            return category["System.Net.Http.".Length..];
        }

        return category;
    }
#pragma warning restore IDE0051

    private static string GetLogLevelText(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            _ => "none"
        };
    }

    private static ConsoleColor GetLogLevelColor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Critical => ConsoleColor.Red,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Information => ConsoleColor.Gray,
            LogLevel.Debug => ConsoleColor.DarkGray,
            LogLevel.Trace => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray
        };
    }
}