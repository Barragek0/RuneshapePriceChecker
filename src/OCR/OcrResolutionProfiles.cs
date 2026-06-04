namespace RuneshapePriceChecker.OCR;

public static class OcrResolutionProfiles
{
    private static readonly Dictionary<string, OcrResolutionProfile> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1920x1080"] = new(1920, 1080, 255, 150, 288, 540) { Confirmed = true },
            ["2560x1440"] = new(2560, 1440, 310, 200, 330, 725),
            ["3440x1440"] = new(3440, 1440, 380, 200, 390, 725) { Confirmed = true },
            ["3840x2160"] = new(3840, 2160, 415, 300, 420, 1095),
        };

    public static bool TryGet(string resolutionKey, out OcrResolutionProfile profile)
    {
        return Profiles.TryGetValue(resolutionKey, out profile!);
    }

    public static IReadOnlyCollection<OcrResolutionProfile> All => Profiles.Values;
}

public sealed record OcrResolutionProfile(
    int WindowWidth,
    int WindowHeight,
    int CaptureOffsetX,
    int CaptureOffsetY,
    int CaptureWidth,
    int CaptureHeight)
{
    public string Key => $"{WindowWidth}x{WindowHeight}";
    public bool Confirmed { get; init; }
}

public sealed record OcrCaptureRegion(int X, int Y, int Width, int Height);
