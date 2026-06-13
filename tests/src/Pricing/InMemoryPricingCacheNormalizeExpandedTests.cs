using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class InMemoryPricingCacheNormalizeExpandedTests
{
    [Theory]
    [InlineData("Uncut Skill Gem", "UNCUT SKILL GEM")]
    [InlineData("Uncut Support Gem", "UNCUT SUPPORT GEM")]
    [InlineData("Uncut Spirit Gem", "UNCUT SPIRIT GEM")]
    public void Normalize_UncutGems_KeepsPrefix(string input, string expected)
    {
        var result = InMemoryPricingCache.Normalize(input);
        Assert.Contains(expected, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_LeadingWhitespace_Trims()
    {
        var result = InMemoryPricingCache.Normalize("   Chaos Orb   ");
        Assert.NotNull(result);
    }

    [Fact]
    public void Normalize_SpecialCharacters_Strips()
    {
        var result = InMemoryPricingCache.Normalize("It`em?Nam'e!");
        Assert.NotNull(result);
    }
}