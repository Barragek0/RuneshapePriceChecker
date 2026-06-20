using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class PriceColorCalculatorRangeTests
{
    private static readonly PricingCacheOptions Options = new()
    {
        RedThreshold = 0.5m,
        OrangeThreshold = 1.0m,
        GreenThreshold = 5.0m
    };

    [Fact]
    public void GetPriceColor_BoundaryValues_AreDistinct()
    {
        var redColor = PriceColorCalculator.GetPriceColor(0.4m, Options);
        var orangeColor = PriceColorCalculator.GetPriceColor(0.9m, Options);
        var greenColor = PriceColorCalculator.GetPriceColor(6.0m, Options);

        Assert.NotEqual(redColor, orangeColor);
        Assert.NotEqual(orangeColor, greenColor);
        Assert.NotEqual(redColor, greenColor);
    }

    [Fact]
    public void GetDivineGlowStrength_EmptyString_ReturnsZero()
    {
        Assert.Equal(0f, PriceColorCalculator.GetDivineGlowStrength(""));
    }

    [Fact]
    public void GetDivineGlowStrength_Whitespace_ReturnsZero()
    {
        Assert.Equal(0f, PriceColorCalculator.GetDivineGlowStrength("   "));
    }

    [Fact]
    public void GetDivineGlowStrength_OneDivine_ReturnsBaseStrength()
    {
        var strength = PriceColorCalculator.GetDivineGlowStrength("1d");
        Assert.True(strength > 0.6f);
        Assert.True(strength < 0.7f);
    }

    [Fact]
    public void GetDivineGlowStrength_ZeroDivine_ReturnsZero()
    {
        Assert.Equal(0f, PriceColorCalculator.GetDivineGlowStrength("0d"));
    }

    [Fact]
    public void GetDivineGlowStrength_HundredDivine_ReturnsMaxStrength()
    {
        var strength = PriceColorCalculator.GetDivineGlowStrength("100d");
        Assert.True(strength >= 0.96f);
        Assert.True(strength <= 1.0f);
    }

    [Fact]
    public void GetDivineGlowStrength_OverHundred_ClampedToMax()
    {
        var strength = PriceColorCalculator.GetDivineGlowStrength("101d");
        // Clamped to 100, so same as 100d
        Assert.True(strength >= 0.96f);
        Assert.True(strength <= 1.0f);
    }

    [Fact]
    public void GetDivineGlowStrength_NonDivineSuffix_ReturnsZero()
    {
        Assert.Equal(0f, PriceColorCalculator.GetDivineGlowStrength("5.0c"));
    }

    [Fact]
    public void GetDivineGlowStrength_NonNumericAfterD_ReturnsZero()
    {
        Assert.Equal(0f, PriceColorCalculator.GetDivineGlowStrength("abcd"));
        Assert.Equal(0f, PriceColorCalculator.GetDivineGlowStrength("ab d"));
    }

    [Fact]
    public void GetDivineGlowStrength_HalfDivine_ReturnsBetweenBaseAndMax()
    {
        // 1d = 0.62, 100d = 0.97; midpoint should be somewhere in between
        var strength = PriceColorCalculator.GetDivineGlowStrength("50d");
        Assert.True(strength > 0.7f);
        Assert.True(strength < 0.95f);
    }
}
