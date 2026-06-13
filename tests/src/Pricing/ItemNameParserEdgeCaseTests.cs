using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class ItemNameParserEdgeCaseTests
{
    [Fact]
    public void ParseDetectedItem_Empty_ReturnsEmptyName()
    {
        var result = ItemNameParser.ParseDetectedItem("");
        Assert.Equal("", result.Name);
        Assert.Equal(1, result.Quantity);
    }

    [Fact]
    public void ParseDetectedItem_Whitespace_ReturnsEmptyName()
    {
        var result = ItemNameParser.ParseDetectedItem("   ");
        Assert.Equal("", result.Name);
    }

    [Fact]
    public void ParseDetectedItem_WithQuantityX_ParsesCorrectly()
    {
        var result = ItemNameParser.ParseDetectedItem("5x Chaos Orb");
        Assert.Equal("Chaos Orb", result.Name);
        Assert.Equal(5, result.Quantity);
    }
}
