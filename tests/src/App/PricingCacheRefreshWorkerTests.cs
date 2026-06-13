using RuneshapePriceChecker.App;
using RuneshapePriceChecker.Configuration;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class PricingCacheRefreshWorkerTests
{
    [Fact]
    public void PricingCacheRefreshWorker_IsBackgroundService()
    {
        var type = typeof(PricingCacheRefreshWorker);
        Assert.NotNull(type);
        Assert.True(typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(type));
    }

    [Fact]
    public void RefreshInterval_Default_Is15Minutes()
    {
        var options = new PricingCacheOptions();
        Assert.Equal(15, options.RefreshInterval.TotalMinutes);
    }

    [Fact]
    public void RefreshInterval_Custom_Respected()
    {
        var options = new PricingCacheOptions { RefreshInterval = TimeSpan.FromMinutes(5) };
        Assert.Equal(5, options.RefreshInterval.TotalMinutes);
    }

    [Fact]
    public void PricingCacheOptions_ZeroInterval_NotNegative()
    {
        var options = new PricingCacheOptions();
        Assert.True(options.RefreshInterval >= TimeSpan.Zero);
    }
}
