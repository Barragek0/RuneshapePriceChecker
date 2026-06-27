using Microsoft.Extensions.Logging;
using RuneshapePriceChecker.App;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class FileLogProviderTests : IDisposable
{
    public FileLogProviderTests()
    {
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CreateLogger_ReturnsNonNullLogger()
    {
        using var provider = new FileLogProvider(LogLevel.Trace);
        var logger = provider.CreateLogger("TestCategory");
        Assert.NotNull(logger);
    }

    [Fact]
    public void CreateLogger_DifferentCategories_ReturnDifferentLoggers()
    {
        using var provider = new FileLogProvider(LogLevel.Trace);
        var logger1 = provider.CreateLogger("CatA");
        var logger2 = provider.CreateLogger("CatB");
        Assert.NotNull(logger1);
        Assert.NotNull(logger2);
        Assert.NotSame(logger1, logger2);
    }

    [Fact]
    public void Logger_IsEnabled_ReturnsTrue()
    {
        using var provider = new FileLogProvider(LogLevel.Trace);
        var logger = provider.CreateLogger("Test");
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void Logger_Log_DoesNotThrow()
    {
        using var provider = new FileLogProvider(LogLevel.Trace);
        var logger = provider.CreateLogger("Test");

        var ex = logger.BeginScope("scope");
        Assert.Null(ex); // FileLogger returns null scope

        // Log should not throw
        logger.Log(LogLevel.Information, new EventId(1), "state", null,
            (s, e) => "test message");
    }

    [Fact]
    public void Logger_LogWithException_DoesNotThrow()
    {
        using var provider = new FileLogProvider(LogLevel.Trace);
        var logger = provider.CreateLogger("Test");

        logger.Log(LogLevel.Error, new EventId(1), "state",
            new InvalidOperationException("test error"),
            (s, e) => "error occurred");
    }

    [Fact]
    public void Logger_BeginScope_ReturnsNull()
    {
        using var provider = new FileLogProvider(LogLevel.Trace);
        var logger = provider.CreateLogger("Test");
        Assert.Null(logger.BeginScope("any state"));
    }

    [Fact]
    public void Provider_Dispose_DoesNotThrow()
    {
        var provider = new FileLogProvider(LogLevel.Trace);
        provider.Dispose();
        // Double dispose should be safe
        provider.Dispose();
    }
}
