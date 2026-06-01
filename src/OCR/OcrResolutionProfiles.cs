namespace RuneshapePriceChecker.OCR;

public static class OcrResolutionProfiles
{
    private static readonly Dictionary<string, OcrResolutionProfile> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1920x1080"] = new(1920, 1080, 255, 160, 285, 537, 23, 24, 8, 2, 2)
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
    int CaptureHeight,
    int RowTextHeight,
    int RowGapHeight,
    int RowLateOffsetStartRow,
    int RowLateOffsetStepRows,
    int RowLateOffsetStepPx)
{
    public string Key => $"{WindowWidth}x{WindowHeight}";
}

public sealed record OcrCaptureRegion(int X, int Y, int Width, int Height);
