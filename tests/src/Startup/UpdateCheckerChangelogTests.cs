using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerChangelogTests : IDisposable
{
    private readonly string _configPath;

    public UpdateCheckerChangelogTests()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        var configDir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(configDir) && Directory.Exists(configDir))
        {
            if (File.Exists(_configPath))
                File.Delete(_configPath);
        }
    }

    public void Dispose()
    {
        try { if (File.Exists(_configPath)) File.Delete(_configPath); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteChangelog_CreatesConfigDirAndSetsPending()
    {
        var checker = CreateChecker();
        SetPrivateFields(checker, "1.0.0");

        InvokeWriteChangelog(checker);

        Assert.True(File.Exists(_configPath));
        var json = File.ReadAllText(_configPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Changelog", out var changelog));
        Assert.Equal("1.0.0", changelog.GetProperty("Version").GetString());
        Assert.False(changelog.GetProperty("Shown").GetBoolean());
    }

    [Fact]
    public void WriteChangelog_OverwritesExistingChangelog()
    {
        var configDir = Path.GetDirectoryName(_configPath)!;
        _ = Directory.CreateDirectory(configDir);
        var existingJson = """
        {
            "Changelog": {
                "Shown": true,
                "Version": "0.9.0"
            },
            "OtherSetting": "value"
        }
        """;
        File.WriteAllText(_configPath, existingJson);

        var checker = CreateChecker();
        SetPrivateFields(checker, "2.0.0");

        InvokeWriteChangelog(checker);

        var json = File.ReadAllText(_configPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Changelog", out var changelog));
        Assert.Equal("2.0.0", changelog.GetProperty("Version").GetString());
        Assert.False(changelog.GetProperty("Shown").GetBoolean());
        Assert.True(root.TryGetProperty("OtherSetting", out var other));
        Assert.Equal("value", other.GetString());
    }

    [Fact]
    public void WriteChangelog_DoesNotCorruptExistingSettings()
    {
        var configDir = Path.GetDirectoryName(_configPath)!;
        _ = Directory.CreateDirectory(configDir);
        var existingJson = """
        {
            "App": {
                "LogLevel": "Debug"
            },
            "PricingCache": {
                "DisplayCurrency": "divine"
            }
        }
        """;
        File.WriteAllText(_configPath, existingJson);

        var checker = CreateChecker();
        SetPrivateFields(checker, "3.0.0");

        InvokeWriteChangelog(checker);

        var json = File.ReadAllText(_configPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("App", out var app));
        Assert.Equal("Debug", app.GetProperty("LogLevel").GetString());
        Assert.True(root.TryGetProperty("PricingCache", out var pricing));
        Assert.Equal("divine", pricing.GetProperty("DisplayCurrency").GetString());
        Assert.True(root.TryGetProperty("Changelog", out _));
    }

#pragma warning disable CA2000 // Ownership transferred to UpdateChecker via factory/sink/dashboard
    private static UpdateChecker CreateChecker()
    {
        var updateOptions = Options.Create(new UpdateOptions());
        var appOptions = new StaticOptionsMonitor<AppOptions>(new AppOptions());
        var logger = new LoggerFactory().CreateLogger<UpdateChecker>();
        var lifetime = new NullApplicationLifetime();
        var sink = new DashboardLogSink();
        var dashboard = new DashboardService(sink);
        var factory = new SingleClientHttpClientFactory(new HttpClient(), "GitHub");

        return new UpdateChecker(updateOptions, appOptions, lifetime, logger, dashboard, factory);
    }
#pragma warning restore CA2000

    private static void SetPrivateFields(UpdateChecker checker, string changelogVersion)
    {
        var type = typeof(UpdateChecker);
        type.GetField("_changelogVersion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(checker, changelogVersion);
    }

    private static void InvokeWriteChangelog(UpdateChecker checker)
    {
        var method = typeof(UpdateChecker).GetMethod("WriteChangelogToSettings",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        _ = method!.Invoke(checker, null);
    }
}
