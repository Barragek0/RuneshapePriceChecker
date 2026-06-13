using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class InMemoryPricingCacheTests
{
    [Fact]
    public void Normalize_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", InMemoryPricingCache.Normalize(""));
    }

    [Fact]
    public void Normalize_Whitespace_ReturnsEmpty()
    {
        Assert.Equal("", InMemoryPricingCache.Normalize("   "));
    }

    [Fact]
    public void Normalize_SimpleItemName_ReturnsUpper()
    {
        var result = InMemoryPricingCache.Normalize("Chaos Orb");
        Assert.Contains("CHAOS ORB", result);
    }

    [Fact]
    public void Normalize_Null_ReturnsEmpty()
    {
        Assert.Equal("", InMemoryPricingCache.Normalize(null!));
    }

    [Fact]
    public void Normalize_LeadingTrailingWhitespace_Trims()
    {
        var result = InMemoryPricingCache.Normalize("  Divine Orb  ");
        Assert.Equal("DIVINE ORB", result);
    }

    [Fact]
    public void Normalize_MixedCase_ReturnsUpper()
    {
        var result = InMemoryPricingCache.Normalize("ExAlTeD oRb");
        Assert.Equal("EXALTED ORB", result);
    }
}
