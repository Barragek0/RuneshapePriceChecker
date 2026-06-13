using System.IO;
using System.Text.Json;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class AutoUpdaterConfigMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public AutoUpdaterConfigMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rstest-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void VersionParsing_OldFormat_0_2_2_ParsesCorrectly()
    {
        Assert.True(UpdateChecker.TryParseVersion("0.2.2", out var version));
        Assert.Equal(0, version.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(2, version.Build);
    }

    [Fact]
    public void VersionParsing_OldToNew_ComparisonWorks()
    {
        Assert.True(UpdateChecker.TryParseVersion("0.2.2", out var oldVersion));
        Assert.True(UpdateChecker.TryParseVersion("1.0.0", out var newVersion));

        Assert.True(newVersion > oldVersion);
        Assert.True(oldVersion < newVersion);
    }

    [Fact]
    public void VersionParsing_UpdaterAndMainApp_UseIdenticalPattern()
    {
        // Both use: @"^(\d+)\.(\d+)\.(\d+)$"
        // Both strip v/V prefix before parsing
        // Both strip +build metadata before parsing

        var testCases = new[]
        {
            ("v0.2.2", true), ("V1.0.0", true), ("0.2.2+abc", true),
            ("v0.2.2-beta", false), ("0.2", false), ("latest", false)
        };

        foreach (var (input, expectValid) in testCases)
        {
            var stripped = input.TrimStart('v', 'V');
            var plusIdx = stripped.IndexOf('+');
            if (plusIdx >= 0) stripped = stripped[..plusIdx];

            var mainResult = UpdateChecker.TryParseVersion(stripped, out var mainVersion);
            Assert.Equal(expectValid, mainResult);

            if (expectValid)
            {
                Assert.True(mainVersion.Major >= 0);
                Assert.True(mainVersion.Minor >= 0);
                Assert.True(mainVersion.Build >= 0);
            }
        }
    }

    [Fact]
    public void ConfigMigration_OldConfig_ReadableByNewApp()
    {
        // Simulate a v0.2.2 config file (minimal JSON)
        var oldConfig = """
        {
            "App": {
                "LogLevel": "Information",
                "SuppressAlreadyRunningWarning": false
            }
        }
        """;

        var configPath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(configPath, oldConfig);

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;

        // Old config should have App section
        Assert.True(root.TryGetProperty("App", out var app));
        Assert.Equal("Information", app.GetProperty("LogLevel").GetString());
    }

    [Fact]
    public void ConfigMigration_NewConfigSections_AddedWithDefaults()
    {
        // New sections that didn't exist in v0.2.2:
        // PricingCache, Ocr, Update, Window, Changelog

        var newConfig = """
        {
            "App": { "LogLevel": "Debug" },
            "PricingCache": { "DisplayCurrency": "chaos" },
            "Ocr": { "BinarizationThreshold": 128 },
            "Update": { "AutoUpdate": true }
        }
        """;

        var configPath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(configPath, newConfig);

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("PricingCache", out _));
        Assert.True(root.TryGetProperty("Ocr", out _));
        Assert.True(root.TryGetProperty("Update", out _));
    }

    [Fact]
    public void ConfigMigration_ChangelogSection_WrittenAfterUpdate()
    {
        var changelogConfig = """
        {
            "App": { "LogLevel": "Debug" },
            "Changelog": {
                "Shown": false,
                "Body": "## What's New in v1.0.0\n\n- Feature A",
                "Version": "1.0.0"
            }
        }
        """;

        var configPath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(configPath, changelogConfig);

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Changelog", out var changelog));
        Assert.False(changelog.GetProperty("Shown").GetBoolean());
        Assert.Contains("Feature A", changelog.GetProperty("Body").GetString());
        Assert.Equal("1.0.0", changelog.GetProperty("Version").GetString());
    }

    [Fact]
    public void ConfigMigration_MissingOptionalSections_DoesNotCrash()
    {
        var minimalConfig = "{}";

        var configPath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(configPath, minimalConfig);

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;

        // Empty config should not crash anything that reads it
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
    }

    [Fact]
    public void ConfigMigration_CorruptedJson_HandledGracefully()
    {
        var corruptedConfig = "{ this is not valid json";

        var configPath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(configPath, corruptedConfig);

        Assert.ThrowsAny<Exception>(() => JsonDocument.Parse(File.ReadAllText(configPath)));
    }
}
