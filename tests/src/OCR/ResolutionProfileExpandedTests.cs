using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class ResolutionProfileExpandedTests
{
    [Theory]
    [InlineData("1600x900")]
    [InlineData("1920x1080")]
    [InlineData("2560x1440")]
    [InlineData("3440x1440")]
    [InlineData("3840x2160")]
    public void AllBundledResolutions_ValidCaptureDimensions(string key)
    {
        Assert.True(OcrResolutionProfiles.TryGet(key, out var profile));
        Assert.True(profile.CaptureWidth > 0);
        Assert.True(profile.CaptureHeight > 0);
    }

    [Fact]
    public void ValidateAll_ReturnsNoWarnings()
    {
        var warnings = OcrResolutionProfiles.ValidateAll();
        Assert.Empty(warnings);
    }
}