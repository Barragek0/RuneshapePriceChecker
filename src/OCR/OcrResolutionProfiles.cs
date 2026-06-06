namespace RuneshapePriceChecker.OCR;

public static class OcrResolutionProfiles
{
    private static readonly Dictionary<string, OcrResolutionProfile> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1600x900"] = new(213, 125, 240, 450) { Confirmed = true },
            ["1920x1080"] = new(255, 150, 288, 540) { Confirmed = true },
            ["2560x1440"] = new(308, 200, 418, 720) { Confirmed = true },
            ["3440x1440"] = new(380, 200, 390, 720) { Confirmed = true },
            ["3840x2160"] = new(407, 300, 680, 1080),
        };

    public static bool TryGet(string resolutionKey, out OcrResolutionProfile profile)
    {
        return Profiles.TryGetValue(resolutionKey, out profile!);
    }

    public static IReadOnlyCollection<string> SupportedResolutions => Profiles.Keys;
}

public sealed record OcrResolutionProfile(
    int CaptureOffsetX,
    int CaptureOffsetY,
    int CaptureWidth,
    int CaptureHeight)
{
    public bool Confirmed { get; init; }
}

public sealed record OcrCaptureRegion(int X, int Y, int Width, int Height);
