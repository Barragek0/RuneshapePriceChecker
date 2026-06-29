using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Configuration;

public class AppOptionsBindingTests
{
    [Fact]
    public void AppOptions_BindsLogLevel()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["App:LogLevel"] = "Debug"
        });

        var options = config.GetSection("App").Get<AppOptions>();
        Assert.NotNull(options);
        Assert.Equal(LogLevel.Debug, options!.LogLevel);
    }

    [Fact]
    public void AppOptions_BindsForceUpdateAvailable()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["App:ForceUpdateAvailable"] = "true"
        });

        var options = config.GetSection("App").Get<AppOptions>();
        Assert.NotNull(options);
        Assert.True(options!.ForceUpdateAvailable);
    }

    [Fact]
    public void AppOptions_NewInstance_HasCorrectDefaults()
    {
        var options = new AppOptions();
        Assert.Equal(LogLevel.Information, options.LogLevel);
        Assert.False(options.ForceUpdateAvailable);
        Assert.False(options.AutoApplyUpdate);
    }

    [Fact]
    public void PricingCacheOptions_BindsDisplayCurrency()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["PricingCache:DisplayCurrency"] = "divine"
        });

        var options = config.GetSection("PricingCache").Get<PricingCacheOptions>();
        Assert.NotNull(options);
        Assert.Equal("divine", options!.DisplayCurrency);
    }

    [Fact]
    public void PricingCacheOptions_BindsThresholds()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["PricingCache:RedThreshold"] = "0.5",
            ["PricingCache:OrangeThreshold"] = "2.0",
            ["PricingCache:GreenThreshold"] = "10.0"
        });

        var options = config.GetSection("PricingCache").Get<PricingCacheOptions>();
        Assert.NotNull(options);
        Assert.Equal(0.5m, options!.RedThreshold);
        Assert.Equal(2.0m, options.OrangeThreshold);
        Assert.Equal(10.0m, options.GreenThreshold);
    }

    [Fact]
    public void OcrOptions_BindsThresholdAndLanguage()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Ocr:Language"] = "eng",
            ["Ocr:BinarizationThreshold"] = "160"
        });

        var options = config.GetSection("Ocr").Get<OcrOptions>();
        Assert.NotNull(options);
        Assert.Equal("eng", options!.Language);
        Assert.Equal(160, options.BinarizationThreshold);
    }

    [Fact]
    public void WindowOptions_BindsInitialSetup()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Window:InitialSetupComplete"] = "true"
        });

        var options = config.GetSection("Window").Get<WindowOptions>();
        Assert.NotNull(options);
        Assert.True(options!.InitialSetupComplete);
    }

    [Fact]
    public void AllSections_BindWithoutError()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["App:LogLevel"] = "Warning",
            ["PricingCache:League"] = "Standard",
            ["Ocr:Language"] = "eng",
            ["Update:AutoUpdate"] = "true",
            ["Window:InitialSetupComplete"] = "false"
        });

        var app = config.GetSection("App").Get<AppOptions>();
        var pricing = config.GetSection("PricingCache").Get<PricingCacheOptions>();
        var ocr = config.GetSection("Ocr").Get<OcrOptions>();
        var update = config.GetSection("Update").Get<UpdateOptions>();
        var window = config.GetSection("Window").Get<WindowOptions>();

        Assert.NotNull(app);
        Assert.NotNull(pricing);
        Assert.NotNull(ocr);
        Assert.NotNull(update);
        Assert.NotNull(window);
        Assert.Equal(LogLevel.Warning, app!.LogLevel);
        Assert.Equal("Standard", pricing!.League);
        Assert.Equal("eng", ocr!.Language);
        Assert.True(update!.AutoUpdate);
        Assert.False(window!.InitialSetupComplete);
    }

    [Fact]
    public void InvalidThresholdValue_ThrowsInvalidOperationException()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["PricingCache:RedThreshold"] = "not-a-number"
        });

        _ = Assert.Throws<InvalidOperationException>(() =>
            config.GetSection("PricingCache").Get<PricingCacheOptions>());
    }

    [Fact]
    public void EmptyConfiguration_NewInstancesHaveCorrectDefaults()
    {
        var app = new AppOptions();
        var pricing = new PricingCacheOptions();
        var update = new UpdateOptions();

        Assert.Equal(LogLevel.Information, app.LogLevel);
        Assert.Equal("poe2scout", pricing.PricingSource);
        Assert.True(update.AutoUpdate);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
