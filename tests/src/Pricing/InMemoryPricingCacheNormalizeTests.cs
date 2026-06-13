using Xunit;
using RuneshapePriceChecker.Pricing;

namespace RuneshapePriceChecker.Tests.Pricing;

public class InMemoryPricingCacheNormalizeTests
{
    [Theory]
    [InlineData("Chaos Orb", "CHAOS ORB")]
    [InlineData("chaos orb", "CHAOS ORB")]
    [InlineData("  Chaos  Orb  ", "CHAOS ORB")]
    [InlineData("Gemcutter's Prism", "GEMCUTTERS PRISM")]
    [InlineData("Gemcutters Prism", "GEMCUTTERS PRISM")]
    [InlineData("Armourer's Scrap", "ARMOURERS SCRAP")]
    [InlineData("Orb of Alchemy", "ORB OF ALCHEMY")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_VariousInputs(string? input, string expected)
    {
        var result = InMemoryPricingCache.Normalize(input);
        Assert.Equal(expected, result);
    }
}
