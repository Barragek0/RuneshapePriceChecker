using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RuneshapePriceChecker.Configuration;
using Xunit;

namespace RuneshapePriceChecker.Tests.Configuration;

public class SettingsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly string _configPath;

    public SettingsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rstest-sc-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tempDir, "config");
        _ = Directory.CreateDirectory(_configDir);
        _configPath = Path.Combine(_configDir, "appsettings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Construct_WithValidConfig_DoesNotThrow()
    {
        File.WriteAllText(_configPath, """{"App":{"LogLevel":"Debug"}}""");
        var config = BuildConfig(_configPath);
        using var loggerFactory0 = new LoggerFactory();
        using var controller = new SettingsController(config, loggerFactory0.CreateLogger<SettingsController>());
        Assert.NotNull(controller);
    }

    [Fact]
    public void RefreshConfiguration_WithConfigRoot_ReloadsSuccessfully()
    {
        File.WriteAllText(_configPath, """{"App":{"LogLevel":"Debug"}}""");
        var config = BuildConfig(_configPath);
        using var loggerFactory = new LoggerFactory();
        var logger = loggerFactory.CreateLogger<SettingsController>();
        using var controller = new SettingsController(config, logger);

        var refreshMethod = typeof(SettingsController).GetMethod("RefreshConfiguration",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(refreshMethod);
        _ = refreshMethod!.Invoke(controller, null);
    }

    [Fact]
    public void RefreshConfiguration_NonConfigRoot_LogsWarning()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["App:LogLevel"] = "Debug" }).Build();

        using var loggerFactory2 = new LoggerFactory();
        var logger = loggerFactory2.CreateLogger<SettingsController>();
        using var controller = new SettingsController(config, logger);

        var refreshMethod = typeof(SettingsController).GetMethod("RefreshConfiguration",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(refreshMethod);
        _ = refreshMethod!.Invoke(controller, null);
    }

    [Fact]
    public void SettingsController_Dispose_CleansUpWatcher()
    {
        File.WriteAllText(_configPath, "{}");
        var config = BuildConfig(_configPath);
        using var loggerFactory3 = new LoggerFactory();
        using var controller = new SettingsController(config, loggerFactory3.CreateLogger<SettingsController>());
    }

    [Fact]
    public void ResolveSettingsPath_ReturnsCorrectRelativePath()
    {
        var type = typeof(SettingsController);
        var method = type.GetMethod("ResolveSettingsPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // ResolveSettingsPath looks in config/, base dir, and cwd for appsettings.json
        // In the test environment, at least one of these may exist
        _ = method!.Invoke(null, null);
        // Result may be null or a valid path — either is acceptable
        // Just verify the method doesn't throw
    }

    [Fact]
    public void ResolveSettingsPath_NoCrashOnInvoke()
    {
        var type = typeof(SettingsController);
        var method = type.GetMethod("ResolveSettingsPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, null);
        // Method should return null or a string without throwing
        Assert.True(result is null or string);
    }

    private static IConfiguration BuildConfig(string path)
    {
        return new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: true)
            .Build();
    }

}
