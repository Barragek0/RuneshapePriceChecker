using RuneshapePriceChecker.Configuration;
using Xunit;

namespace RuneshapePriceChecker.Tests.Configuration;

public class PricingCacheOptionsTests
{
    [Fact]
    public void Default_League_IsRunesOfAldur()
    {
        var options = new PricingCacheOptions();
        Assert.Equal("Runes of Aldur", options.League);
    }

    [Fact]
    public void Default_PricingSource_IsPoe2Scout()
    {
        var options = new PricingCacheOptions();
        Assert.Equal("poe2scout", options.PricingSource);
    }

    [Fact]
    public void Default_DisplayCurrency_IsExalt()
    {
        var options = new PricingCacheOptions();
        Assert.Equal("exalt", options.DisplayCurrency);
    }

    [Fact]
    public void Thresholds_HaveSensibleDefaults()
    {
        var options = new PricingCacheOptions();
        Assert.True(options.RedThreshold < options.OrangeThreshold);
        Assert.True(options.OrangeThreshold < options.GreenThreshold);
    }
}
