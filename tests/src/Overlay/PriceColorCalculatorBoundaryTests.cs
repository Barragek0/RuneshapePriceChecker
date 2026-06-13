using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class PriceColorCalculatorBoundaryTests
{
    private static readonly PricingCacheOptions Options = new()
    {
        RedThreshold = 0.5m,
        OrangeThreshold = 1.0m,
        GreenThreshold = 5.0m
    };

    [Theory]
    [InlineData(0.49, "red")]
    [InlineData(0.50, "orange")]
    [InlineData(0.99, "orange")]
    [InlineData(1.00, "green")]
    [InlineData(5.00, "green")]
    public void GetPriceColor_AtBoundaries_CorrectColor(decimal price, string expected)
    {
        var color = PriceColorCalculator.GetPriceColor(price, Options);
        Assert.NotNull(color);
    }

    [Fact]
    public void GetPriceColor_ZeroPrice_DoesNotThrow()
    {
        var color = PriceColorCalculator.GetPriceColor(0m, Options);
        Assert.NotNull(color);
    }

    [Fact]
    public void GetPriceColor_NegativePrice_DoesNotThrow()
    {
        var color = PriceColorCalculator.GetPriceColor(-1m, Options);
        Assert.NotNull(color);
    }

    // ── TryParseDisplayedChaosEquivalent ──

    [Fact]
    public void TryParseChaosEquivalent_EmptyString_ReturnsFalse()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("", Options, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryParseChaosEquivalent_Whitespace_ReturnsFalse()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("   ", Options, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryParseChaosEquivalent_LessThanPrefix_ParsesCorrectly()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("<0.5c", Options, out var value);
        Assert.True(result);
        Assert.Equal(0.5m, value);
    }

    [Fact]
    public void TryParseChaosEquivalent_LessThanExalt_ParsesCorrectly()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("<1.0ex", Options, out var value);
        Assert.True(result);
        Assert.Equal(1.0m, value);
    }

    [Fact]
    public void TryParseChaosEquivalent_ChaosDenomination_ReturnsCorrect()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("3.5c", Options, out var value);
        Assert.True(result);
        Assert.Equal(3.5m, value);
    }

    [Fact]
    public void TryParseChaosEquivalent_ExaltDenomination_ReturnsCorrect()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("2.5ex", Options, out var value);
        Assert.True(result);
        Assert.Equal(2.5m, value);
    }

    [Fact]
    public void TryParseChaosEquivalent_DivineDenomination_ReturnsGreenThresholdOrMore()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("1.0d", Options, out var value);
        Assert.True(result);
        Assert.True(value >= Options.GreenThreshold);
    }

    [Fact]
    public void TryParseChaosEquivalent_UnknownSuffix_ReturnsFalse()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("5.0xyz", Options, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryParseChaosEquivalent_NegativeValue_ReturnsZero()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("-1.0c", Options, out var value);
        Assert.True(result);
        Assert.Equal(0m, value);
    }

    [Fact]
    public void TryParseChaosEquivalent_NonNumericExalt_ReturnsFalse()
    {
        var result = PriceColorCalculator.TryParseDisplayedChaosEquivalent("abc ex", Options, out _);
        Assert.False(result);
    }
}