using Xunit;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Overlay;

namespace RuneshapePriceChecker.Tests.Overlay;

public class PriceColorCalculatorTests
{
    private static readonly PricingCacheOptions DefaultOptions = new()
    {
        RedThreshold = 0.5m,
        OrangeThreshold = 1.0m,
        GreenThreshold = 5.0m
    };

    [Fact]
    public void GetPriceColor_RedThreshold_ReturnsNonWhiteColor()
    {
        var color = PriceColorCalculator.GetPriceColor(0.4m, DefaultOptions);

        Assert.NotEqual(System.Drawing.Color.White, color);
        Assert.True(color.R > color.G + 50 || color.R > color.B + 50); // reddish
    }

    [Fact]
    public void GetPriceColor_OrangeThreshold_ReturnsDistinctColor()
    {
        var red = PriceColorCalculator.GetPriceColor(0.4m, DefaultOptions);
        var orange = PriceColorCalculator.GetPriceColor(0.7m, DefaultOptions);

        Assert.NotEqual(red, orange);
    }

    [Fact]
    public void GetPriceColor_GreenThreshold_ReturnsDistinctColor()
    {
        var green = PriceColorCalculator.GetPriceColor(6.0m, DefaultOptions);

        Assert.True(green.G > green.R); // greenish
    }

    [Fact]
    public void GetPriceColor_Negative_ReturnsReddish()
    {
        var color = PriceColorCalculator.GetPriceColor(-1m, DefaultOptions);

        Assert.True(color.R > 200);
    }

    [Fact]
    public void GetDivineGlowStrength_NonDivine_ReturnsZero()
    {
        var strength = PriceColorCalculator.GetDivineGlowStrength("0.5c");

        Assert.Equal(0f, strength);
    }

    [Fact]
    public void GetDivineGlowStrength_DivineDenominated_ReturnsPositive()
    {
        var strength = PriceColorCalculator.GetDivineGlowStrength("1.5d");

        Assert.True(strength > 0f);
    }

    [Fact]
    public void TryParseDisplayedChaosEquivalent_ChaosString_ParsesCorrectly()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("0.5c", DefaultOptions, out var value);

        Assert.True(result);
        Assert.Equal(0.5m, value);
    }
}
