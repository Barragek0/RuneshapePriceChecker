using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.OCR;

internal static class OcrCaptureRegionResolver
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public static OcrCaptureRegion Resolve(int topLeftX, int topLeftY, int windowWidth, int windowHeight, OcrResolutionProfile profile)
    {
        if (!TryCreateWindowRelativeRegion(topLeftX, topLeftY, windowWidth, windowHeight, profile, out var region, out var error))
        {
            throw new InvalidOperationException($"OCR profile is invalid: {error}");
        }

        return region;
    }

    private static bool TryCreateWindowRelativeRegion(
        int topLeftX,
        int topLeftY,
        int windowWidth,
        int windowHeight,
        OcrResolutionProfile profile,
        out OcrCaptureRegion region,
        out string validationError)
    {
        region = default!;

        if (profile.CaptureWidth <= 0 || profile.CaptureHeight <= 0)
        {
            validationError = "capture size must be positive";
            return false;
        }

        if (profile.CaptureOffsetX < 0 || profile.CaptureOffsetY < 0)
        {
            validationError = "offsets must be non-negative";
            return false;
        }

        if (profile.CaptureOffsetX + profile.CaptureWidth > windowWidth ||
            profile.CaptureOffsetY + profile.CaptureHeight > windowHeight)
        {
            validationError = "offset + size extends outside the PoE2 client area";
            return false;
        }

        region = new OcrCaptureRegion(
            topLeftX + profile.CaptureOffsetX,
            topLeftY + profile.CaptureOffsetY,
            profile.CaptureWidth,
            profile.CaptureHeight);

        validationError = string.Empty;
        return true;
    }
}
