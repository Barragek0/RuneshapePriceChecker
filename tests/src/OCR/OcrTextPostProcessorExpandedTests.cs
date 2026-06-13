using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class OcrTextPostProcessorExpandedTests
{
    [Fact]
    public void ExtractLikelyItemNames_UnicodeChars_Normalized()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x It`em\u2019Nam'e!");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void ExtractLikelyItemNames_VeryLongName_Handled()
    {
        var longName = new string('A', 500);
        var result = OcrTextPostProcessor.ExtractLikelyItemNames($"1x {longName}");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void ExtractLikelyItemNames_EmptyLinesBetween_Filtered()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x Item A\n\n\n1x Item B");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExtractLikelyItemNames_DoubleSpecialChar_NormalizedToSingleSpace()
    {
        // Multi-whitespace collapse: "!!" becomes spaces then collapsed
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x Item!!Name");
        Assert.NotEmpty(result);
        Assert.All(result, line => Assert.DoesNotContain("!!", line));
    }

    [Fact]
    public void ExtractLikelyItemNames_LeadingTrailingDashes_Trimmed()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x ---Item Name---");
        Assert.NotEmpty(result);
        // The '1x ' prefix is added, and trailing dashes are trimmed
        Assert.All(result, line => Assert.False(line.EndsWith("---", StringComparison.Ordinal)));
    }

    [Fact]
    public void ExtractLikelyItemNames_BackticksNormalized_BecomesApostrophe()
    {
        var result = OcrTextPostProcessor.ExtractLikelyItemNames("1x Item`Name");
        Assert.NotEmpty(result);
        Assert.Contains("'", result[0]);
    }

    [Fact]
    public void ExtractLikelyItemNames_ManyLines_AllPreserved()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"1x Item {i}"));
        var result = OcrTextPostProcessor.ExtractLikelyItemNames(lines);
        Assert.Equal(20, result.Count);
    }
}