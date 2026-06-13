using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class OcrCaptureRegionResolverTests
{
    private static readonly OcrResolutionProfile ValidProfile = new(52, 154, 497, 536);

    [Fact]
    public void Resolve_ValidProfile_ReturnsCorrectRegion()
    {
        var region = OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, ValidProfile);

        Assert.Equal(52, region.X);
        Assert.Equal(154, region.Y);
        Assert.Equal(497, region.Width);
        Assert.Equal(536, region.Height);
    }

    [Fact]
    public void Resolve_NonZeroOrigin_OffsetsCorrectly()
    {
        var region = OcrCaptureRegionResolver.Resolve(100, 200, 1920, 1080, ValidProfile);

        Assert.Equal(152, region.X);
        Assert.Equal(354, region.Y);
    }

    [Fact]
    public void Resolve_OffsetAtZero_ReturnsOrigin()
    {
        var profile = new OcrResolutionProfile(0, 0, 100, 100);

        var region = OcrCaptureRegionResolver.Resolve(50, 60, 200, 200, profile);

        Assert.Equal(50, region.X);
        Assert.Equal(60, region.Y);
    }

    [Fact]
    public void Resolve_ProfileExactlyMatchesWindow_DoesNotThrow()
    {
        var profile = new OcrResolutionProfile(0, 0, 1920, 1080);

        var region = OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile);

        Assert.Equal(1920, region.Width);
        Assert.Equal(1080, region.Height);
    }

    [Fact]
    public void Resolve_NegativeCaptureWidth_ThrowsInvalidOperation()
    {
        var profile = new OcrResolutionProfile(0, 0, -1, 100);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile));
        Assert.Contains("capture size must be positive", ex.Message);
    }

    [Fact]
    public void Resolve_ZeroCaptureHeight_ThrowsInvalidOperation()
    {
        var profile = new OcrResolutionProfile(0, 0, 100, 0);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile));
        Assert.Contains("capture size must be positive", ex.Message);
    }

    [Fact]
    public void Resolve_NegativeOffsetX_ThrowsInvalidOperation()
    {
        var profile = new OcrResolutionProfile(-5, 0, 100, 100);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile));
        Assert.Contains("offsets must be non-negative", ex.Message);
    }

    [Fact]
    public void Resolve_NegativeOffsetY_ThrowsInvalidOperation()
    {
        var profile = new OcrResolutionProfile(0, -1, 100, 100);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile));
        Assert.Contains("offsets must be non-negative", ex.Message);
    }

    [Fact]
    public void Resolve_ExtendsBeyondWindowWidth_ThrowsInvalidOperation()
    {
        var profile = new OcrResolutionProfile(1800, 0, 200, 100);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile));
        Assert.Contains("extends outside the PoE2 client area", ex.Message);
    }

    [Fact]
    public void Resolve_ExtendsBeyondWindowHeight_ThrowsInvalidOperation()
    {
        var profile = new OcrResolutionProfile(0, 1000, 100, 200);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile));
        Assert.Contains("extends outside the PoE2 client area", ex.Message);
    }

    [Fact]
    public void Resolve_ExtendsBeyondBothDimensions_ThrowsInvalidOperation()
    {
        var profile = new OcrResolutionProfile(1900, 1070, 100, 100);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            OcrCaptureRegionResolver.Resolve(0, 0, 1920, 1080, profile));
        Assert.Contains("extends outside the PoE2 client area", ex.Message);
    }
}

