using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class ItemNameParserOcrTokenEdgeTests
{
    [Theory]
    [InlineData("O", 2)]
    [InlineData("0", 2)]
    [InlineData("I", 1)]
    [InlineData("i", 1)]
    [InlineData("l", 1)]
    [InlineData("|", 1)]
    public void NormalizeQuantityToken_AllTokens_ParseCorrectly(string token, int expected)
    {
        var result = ItemNameParser.NormalizeQuantityToken(token);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseDetectedItem_QuantityWithX_ReturnsCorrectQuantity()
    {
        var result = ItemNameParser.ParseDetectedItem("5x Chaos Orb");
        Assert.Equal(5, result.Quantity);
        Assert.Contains("Chaos Orb", result.Name);
    }

    [Fact]
    public void ParseDetectedItem_GreaterOrb_ParsesNameCorrectly()
    {
        var result = ItemNameParser.ParseDetectedItem("GREATER ORB OF STORMS");
        Assert.NotNull(result);
        Assert.NotEmpty(result.Name);
    }

    [Fact]
    public void ParseDetectedItem_PerfectRune_ParsesName()
    {
        var result = ItemNameParser.ParseDetectedItem("PERFECT IRON RUNE");
        Assert.NotNull(result);
        Assert.NotEmpty(result.Name);
    }
}