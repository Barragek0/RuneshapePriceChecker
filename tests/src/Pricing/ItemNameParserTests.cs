using Xunit;
using RuneshapePriceChecker.Pricing;

namespace RuneshapePriceChecker.Tests.Pricing;

public class ItemNameParserTests
{
    [Theory]
    [InlineData("1x Chaos Orb", 1, "Chaos Orb")]
    [InlineData("2x Divine Orb", 2, "Divine Orb")]
    [InlineData("10x Vaal Orb", 10, "Vaal Orb")]
    [InlineData("I Chaos Orb", 1, "Chaos Orb")]
    [InlineData("l Chaos Orb", 1, "Chaos Orb")]
    [InlineData("| Chaos Orb", 1, "Chaos Orb")]
    [InlineData("5x Exalted Orb", 5, "Exalted Orb")]
    [InlineData("3 x Divine Orb", 3, "Divine Orb")]
    [InlineData("Uncut Support Gem", 1, "Uncut Support Gem")]
    [InlineData("Uncut Skill Gem", 1, "Uncut Skill Gem")]
    [InlineData("Uncut Spirit Gem", 1, "Uncut Spirit Gem")]
    public void ParseDetectedItem_ReturnsExpectedQuantityAndName(string raw, int expectedQty, string expectedName)
    {
        var result = ItemNameParser.ParseDetectedItem(raw);

        Assert.Equal(expectedQty, result.Quantity);
        Assert.Equal(expectedName, result.Name);
    }

    [Fact]
    public void ParseDetectedItem_EmptyString_ReturnsEmptyName()
    {
        var result = ItemNameParser.ParseDetectedItem("");

        Assert.Equal(string.Empty, result.Name);
    }

    [Theory]
    [InlineData("1x Chaos Orb", "Chaos Orb")]
    [InlineData("Uncut Support Gem", "Uncut Support Gem")]
    [InlineData("Gemcutters Prism", "Gemcutters Prism")]
    public void ParseDetectedItem_ReturnsExpectedName(string raw, string expectedName)
    {
        var result = ItemNameParser.ParseDetectedItem(raw);

        Assert.Equal(expectedName, result.Name);
    }

    [Theory]
    [InlineData("1x Chaos Orb", 1)]
    [InlineData("2x Divine Orb", 2)]
    [InlineData("10x Vaal Orb", 10)]
    public void ParseDetectedItem_ReturnsExpectedQuantity(string raw, int expectedQty)
    {
        var result = ItemNameParser.ParseDetectedItem(raw);

        Assert.Equal(expectedQty, result.Quantity);
    }

    [Fact]
    public void ParseDetectedItem_UniqueRing_ReturnsCorrectName()
    {
        var result = ItemNameParser.ParseDetectedItem("Unique Ring");
        Assert.Equal("Unique Ring", result.Name);
    }

    [Fact]
    public void ParseDetectedItem_QuantityPrefixVariants_AllDetected()
    {
        var results = new[]
        {
            ItemNameParser.ParseDetectedItem("1x Chaos Orb"),
            ItemNameParser.ParseDetectedItem("I Chaos Orb"),
            ItemNameParser.ParseDetectedItem("l Chaos Orb"),
            ItemNameParser.ParseDetectedItem("| Chaos Orb"),
        };

        Assert.All(results, r => Assert.Equal("Chaos Orb", r.Name));
        Assert.All(results, r => Assert.True(r.Quantity > 0));
    }
}
