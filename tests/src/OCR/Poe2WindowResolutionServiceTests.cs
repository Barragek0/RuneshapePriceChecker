using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class Poe2WindowResolutionServiceTests
{
    [Fact]
    public void WindowCaptureContext_Properties_ReturnConstructorValues()
    {
        var handle = new IntPtr(12345);
        var ctx = new WindowCaptureContext(handle, 10, 20, 1920, 1080);

        Assert.Equal(handle, ctx.WindowHandle);
        Assert.Equal(10, ctx.ClientX);
        Assert.Equal(20, ctx.ClientY);
        Assert.Equal(1920, ctx.ClientWidth);
        Assert.Equal(1080, ctx.ClientHeight);
    }

    [Fact]
    public void WindowCaptureContext_Equality_SameValuesAreEqual()
    {
        var a = new WindowCaptureContext(new IntPtr(1), 0, 0, 800, 600);
        var b = new WindowCaptureContext(new IntPtr(1), 0, 0, 800, 600);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void WindowCaptureContext_Equality_DifferentValuesAreNotEqual()
    {
        var a = new WindowCaptureContext(new IntPtr(1), 0, 0, 800, 600);
        var b = new WindowCaptureContext(new IntPtr(1), 10, 0, 800, 600);

        Assert.NotEqual(a, b);
        Assert.False(a == b);
    }

    [Fact]
    public void WindowCaptureContext_ZeroSize_Allowed()
    {
        var ctx = new WindowCaptureContext(IntPtr.Zero, 0, 0, 0, 0);

        Assert.Equal(0, ctx.ClientWidth);
        Assert.Equal(0, ctx.ClientHeight);
    }

    [Fact]
    public void OcrResolutionProfiles_TryGet_KnownResolution_ReturnsProfile()
    {
        Assert.True(OcrResolutionProfiles.TryGet("1920x1080", out var profile));
        Assert.Equal(52, profile.CaptureOffsetX);
        Assert.Equal(154, profile.CaptureOffsetY);
        Assert.Equal(497, profile.CaptureWidth);
        Assert.Equal(536, profile.CaptureHeight);
    }

    [Fact]
    public void OcrResolutionProfiles_TryGet_UnknownResolution_ReturnsFalse()
    {
        Assert.False(OcrResolutionProfiles.TryGet("999x999", out _));
    }

    [Fact]
    public void OcrResolutionProfiles_Interpolate_KnownExact_ReturnsSameProfile()
    {
        var interp = OcrResolutionProfiles.Interpolate(1920, 1080);
        Assert.NotNull(interp);
        Assert.Equal(52, interp!.CaptureOffsetX);
    }

    [Fact]
    public void OcrResolutionProfiles_Interpolate_BetweenResolutions_ReturnsInterpolated()
    {
        var interp = OcrResolutionProfiles.Interpolate(1760, 990);

        Assert.NotNull(interp);
        Assert.True(interp!.CaptureWidth > 0);
        Assert.True(interp.CaptureHeight > 0);
    }

    [Fact]
    public void OcrResolutionProfiles_Interpolate_BelowSmallest_ReturnsSmallest()
    {
        var interp = OcrResolutionProfiles.Interpolate(800, 600);

        Assert.NotNull(interp);
        Assert.Equal(43, interp!.CaptureOffsetX);
    }

    [Fact]
    public void OcrResolutionProfiles_Interpolate_AboveLargest_ReturnsLargest()
    {
        var interp = OcrResolutionProfiles.Interpolate(7680, 4320);

        Assert.NotNull(interp);
        Assert.Equal(104, interp!.CaptureOffsetX);
    }

    [Fact]
    public void OcrResolutionProfiles_ValidateAll_ReturnsNoWarningsForBuiltInProfiles()
    {
        var warnings = OcrResolutionProfiles.ValidateAll();

        Assert.NotNull(warnings);
        Assert.Empty(warnings);
    }
}
