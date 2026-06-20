using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class OcrTextPostProcessorTests
{
    [Fact]
    public void ExtractLikelyItemNames_EmptyText_ReturnsEmpty()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractLikelyItemNames_SingleLine_ReturnsNormalizedItem()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x Chaos Orb");
        _ = Assert.Single(result);
        Assert.Contains("Chaos Orb", result[0]);
    }

    [Fact]
    public void ExtractLikelyItemNames_MultipleLines_ReturnsEach()
    {
        var raw = "1x Divine Orb\n1x Exalted Orb\n1x Chaos Orb";
        var result = OcrTextPostProcessor.ExtractLikelyItemNames(raw);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ExtractLikelyItemNames_ShortLines_Filtered()
    {
        // Lines below 3 chars after normalization are filtered out.
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("a\nb\nabc");
        _ = Assert.Single(result);
        Assert.Equal("abc", result[0]);
    }

    [Fact]
    public void ExtractLikelyItemNames_NumbersOnly_Filtered()
    {
        // Lines with no letters are filtered out.
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("12\n34");
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractLikelyItemNames_CarriageReturns_Handled()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x Item One\r\n1x Item Two");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExtractLikelyItemNames_SpecialChars_Normalized()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x It`em�Nam'e!");
        _ = Assert.Single(result);
        Assert.Contains("It'em'Nam", result[0]);
    }
}
