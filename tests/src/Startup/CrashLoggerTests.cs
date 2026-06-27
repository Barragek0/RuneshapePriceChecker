using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public sealed class CrashLoggerTests
{
    [Fact]
    public void WriteCrash_CreatesCrashLogFile()
    {
        // Arrange
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logsDir))
            Directory.Delete(logsDir, recursive: true);

        // Clear any prior crash state via reflection
        typeof(CrashLogger).GetField("_hasCrashed", BindingFlags.Static | BindingFlags.NonPublic)
        ?.SetValue(null, false);

        // Act
        var ex = new InvalidOperationException("Test crash message");
        CrashLogger.WriteCrash("Test crash title", ex);

        // Assert
        Assert.True(Directory.Exists(logsDir), "logs/ directory should exist");
        var files = Directory.GetFiles(logsDir, "*-crash.txt");
        Assert.NotEmpty(files);

        var crashFile = files[0];
        var content = File.ReadAllText(crashFile);

        Assert.Contains("RuneshapePriceChecker Crash Report", content);
        Assert.Contains("Test crash title", content);
        Assert.Contains("Test crash message", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("--- STACK TRACE ---", content);
        Assert.Contains("--- EXCEPTION ---", content);
        Assert.Contains("--- SYSTEM INFO ---", content);
        Assert.Matches(@"\d{8}-\d{6}\.\d{3}-crash\.txt", Path.GetFileName(crashFile));

        // Cleanup
        try { Directory.Delete(logsDir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteCrash_OnlyWritesOnce()
    {
        // Arrange
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logsDir))
            Directory.Delete(logsDir, recursive: true);

        typeof(CrashLogger).GetField("_hasCrashed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, false);

        // Act
        CrashLogger.WriteCrash("First crash", new Exception("First"));
        CrashLogger.WriteCrash("Second crash", new Exception("Second"));
        CrashLogger.WriteCrash("Third crash", new Exception("Third"));

        // Assert - only one crash file should exist
        var files = Directory.GetFiles(logsDir, "*-crash.txt");
        Assert.Single(files);

        var content = File.ReadAllText(files[0]);
        Assert.Contains("First crash", content);
        Assert.DoesNotContain("Second crash", content);
        Assert.DoesNotContain("Third crash", content);

        // Cleanup
        try { Directory.Delete(logsDir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteCrash_NullException_StillWritesLog()
    {
        // Arrange
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logsDir))
            Directory.Delete(logsDir, recursive: true);

        typeof(CrashLogger).GetField("_hasCrashed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, false);

        // Act
        CrashLogger.WriteCrash("Null exception test", null);

        // Assert
        Assert.True(Directory.Exists(logsDir));
        var files = Directory.GetFiles(logsDir, "*-crash.txt");
        Assert.NotEmpty(files);

        var content = File.ReadAllText(files[0]);
        Assert.Contains("Null exception test", content);

        // Cleanup
        try { Directory.Delete(logsDir, recursive: true); } catch { }
    }

    [Fact]
    public void GenerateCrashLogPath_ReturnsCorrectPattern()
    {
        var path = CrashLogger.GenerateCrashLogPath();
        Assert.EndsWith("-crash.txt", path);
        Assert.Contains("logs", path);
        Assert.Matches(@"\d{8}-\d{6}\.\d{3}-crash\.txt", Path.GetFileName(path));

        // Ensure directory was created
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void WriteCrash_IncludesInnerException()
    {
        // Arrange
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logsDir))
            Directory.Delete(logsDir, recursive: true);

        typeof(CrashLogger).GetField("_hasCrashed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, false);

        var inner = new ArgumentNullException("testParam", "Inner exception message");
        var outer = new InvalidOperationException("Outer exception", inner);

        // Act
        CrashLogger.WriteCrash("Inner exception test", outer);

        // Assert
        var files = Directory.GetFiles(logsDir, "*-crash.txt");
        var content = File.ReadAllText(files[0]);

        Assert.Contains("--- INNER EXCEPTION ---", content);
        Assert.Contains("Inner exception message", content);

        // Cleanup
        try { Directory.Delete(logsDir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteCrash_AggregateException_IncludesAllInner()
    {
        // Arrange
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logsDir))
            Directory.Delete(logsDir, recursive: true);

        typeof(CrashLogger).GetField("_hasCrashed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(null, false);

        var ae = new AggregateException(
            new InvalidOperationException("Inner A"),
            new ArgumentException("Inner B"),
            new NullReferenceException("Inner C"));

        // Act
        CrashLogger.WriteCrash("Aggregate test", ae);

        // Assert
        var files = Directory.GetFiles(logsDir, "*-crash.txt");
        var content = File.ReadAllText(files[0]);

        Assert.Contains("Inner Exception [0]", content);
        Assert.Contains("Inner Exception [1]", content);
        Assert.Contains("Inner Exception [2]", content);
        Assert.Contains("Inner A", content);
        Assert.Contains("Inner B", content);
        Assert.Contains("Inner C", content);

        // Cleanup
        try { Directory.Delete(logsDir, recursive: true); } catch { }
    }
}
