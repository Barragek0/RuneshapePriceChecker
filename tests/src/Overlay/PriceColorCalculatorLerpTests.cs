using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class PriceColorCalculatorLerpTests
{
    private static readonly PricingCacheOptions Options = new()
    {
        RedThreshold = 0.5m,
        OrangeThreshold = 1.0m,
        GreenThreshold = 5.0m
    };

    [Fact]
    public void GetPriceColor_ExactRedThreshold_ReturnsRed()
    {
        var color = PriceColorCalculator.GetPriceColor(0.5m, Options);
        // 0.5 is at red threshold, should be red
        Assert.Equal(255, color.R);
        Assert.Equal(72, color.G);
        Assert.Equal(72, color.B);
    }

    [Fact]
    public void GetPriceColor_ExactOrangeThreshold_ReturnsOrange()
    {
        var color = PriceColorCalculator.GetPriceColor(1.0m, Options);
        Assert.Equal(255, color.R);
        Assert.Equal(196, color.G);
        Assert.Equal(54, color.B);
    }

    [Fact]
    public void GetPriceColor_ExactGreenThreshold_ReturnsGreen()
    {
        var color = PriceColorCalculator.GetPriceColor(5.0m, Options);
        Assert.Equal(88, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(122, color.B);
    }

    [Fact]
    public void GetPriceColor_AboveGreenThreshold_ReturnsGreen()
    {
        var color1 = PriceColorCalculator.GetPriceColor(5.0m, Options);
        var color2 = PriceColorCalculator.GetPriceColor(100.0m, Options);
        Assert.Equal(color1, color2); // Both green
    }

    [Fact]
    public void GetPriceColor_MidpointRedToOrange_ReturnsLerpedColor()
    {
        var color = PriceColorCalculator.GetPriceColor(0.75m, Options);
        // Should be between red and orange, not equal to either
        var red = PriceColorCalculator.GetPriceColor(0.5m, Options);
        var orange = PriceColorCalculator.GetPriceColor(1.0m, Options);
        Assert.NotEqual(red, color);
        Assert.NotEqual(orange, color);
    }

    [Fact]
    public void GetPriceColor_MidpointOrangeToGreen_ReturnsLerpedColor()
    {
        var color = PriceColorCalculator.GetPriceColor(3.0m, Options);
        var orange = PriceColorCalculator.GetPriceColor(1.0m, Options);
        var green = PriceColorCalculator.GetPriceColor(5.0m, Options);
        Assert.NotEqual(orange, color);
        Assert.NotEqual(green, color);
    }

    [Fact]
    public void GetPriceColor_ZeroValue_ReturnsRedOrLerped()
    {
        var color = PriceColorCalculator.GetPriceColor(0m, Options);
        Assert.NotNull(color);
        Assert.Equal(255, color.R); // At red threshold or below
    }

    [Fact]
    public void GetPriceColor_VeryLargeValue_StillGreen()
    {
        var color = PriceColorCalculator.GetPriceColor(999999m, Options);
        var green = PriceColorCalculator.GetPriceColor(5.0m, Options);
        Assert.Equal(green, color);
    }
}
