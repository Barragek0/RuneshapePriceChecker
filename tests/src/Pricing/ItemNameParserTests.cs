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

    [Theory]
    [InlineData("Uncut Skill Gem Level 19", "Uncut Skill Gem")]
    [InlineData("Uncut Skill Cem Level 19", "Uncut Skill Cem")]
    [InlineData("Uncut Support Gem Level 5", "Uncut Support Gem")]
    [InlineData("Uncut Spirit Gem Level 1", "Uncut Spirit Gem")]
    [InlineData("Gemme d'aptitude brute Niveau 19", "Gemme d'aptitude brute")]
    [InlineData("Gemme d'aptitude brute Nivieau 19", "Gemme d'aptitude brute")]
    [InlineData("Roher Fertigkeitsedelstein Stufe 19", "Roher Fertigkeitsedelstein")]
    [InlineData("Gema de Habilidad Bruta Nivel 19", "Gema de Habilidad Bruta")]
    [InlineData("1x Uncut Skill Gem Level 19", "Uncut Skill Gem")]
    [InlineData("Ix Gemme d'aptitude brute Niveau 19", "Gemme d'aptitude brute")]
    [InlineData("1x xx Rune of Consistency", "Rune of Consistency")]
    [InlineData("1x x Artificer Orb", "Artificer Orb")]
    [InlineData("1x xxx Item Name", "Item Name")]
    // Portuguese
    [InlineData("Gema de Habilidade Bruta Nível 19", "Gema de Habilidade Bruta")]
    [InlineData("Gema de Suporte Bruta Nível 5", "Gema de Suporte Bruta")]
    // Russian
    [InlineData("Неогранённый самоцвет умений Уровень 19", "Неогранённый самоцвет умений")]
    [InlineData("Неогранённый самоцвет поддержки Уровень 5", "Неогранённый самоцвет поддержки")]
    [InlineData("Неогранённый самоцвет духа Уровень 1", "Неогранённый самоцвет духа")]
    // Korean
    [InlineData("스킬 젬 레벨 19", "스킬 젬")]
    [InlineData("서포트 젬 레벨 5", "서포트 젬")]
    // Chinese Traditional
    [InlineData("技能寶石 等級 19", "技能寶石")]
    [InlineData("輔助寶石 等級 5", "輔助寶石")]
    public void ParseDetectedItem_StripsLevelSuffix(string raw, string expectedName)
    {
        var result = ItemNameParser.ParseDetectedItem(raw);
        Assert.Equal(expectedName, result.Name);
    }

    [Fact]
    public void ParseDetectedItem_NormalItem_DoesNotStripAnything()
    {
        var result = ItemNameParser.ParseDetectedItem("Chaos Orb");
        Assert.Equal("Chaos Orb", result.Name);
    }

    [Fact]
    public void ParseDetectedItem_UncutGemWithoutLevel_KeepsName()
    {
        var result = ItemNameParser.ParseDetectedItem("Uncut Skill Gem");
        Assert.Equal("Uncut Skill Gem", result.Name);
    }
}
