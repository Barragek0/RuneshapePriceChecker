namespace RuneshapePriceChecker.OCR;

public static class OcrResolutionProfiles
{
    private static readonly Dictionary<string, OcrResolutionProfile> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1600x900"] = new(43, 128, 414, 447),
            ["1920x1080"] = new(52, 154, 497, 536),
            ["2560x1440"] = new(69, 205, 663, 715),
            ["3440x1440"] = new(69, 205, 663, 715),
            ["3840x2160"] = new(104, 308, 994, 1072),
        };

    public static bool TryGet(string resolutionKey, out OcrResolutionProfile profile)
    {
        return Profiles.TryGetValue(resolutionKey, out profile!);
    }

    public static OcrResolutionProfile? Interpolate(int width, int height)
    {
        if (Profiles.Count == 0) return null;

        var key = $"{width}x{height}";
        if (Profiles.TryGetValue(key, out var exact))
            return exact;

        long targetPixels = (long)width * height;

        var sorted = Profiles
            .Select(kvp => new
            {
                kvp.Key,
                kvp.Value,
                w = int.Parse(kvp.Key.Split('x')[0]),
                h = int.Parse(kvp.Key.Split('x')[1]),
                pixels = (long)int.Parse(kvp.Key.Split('x')[0]) * int.Parse(kvp.Key.Split('x')[1])
            })
            .OrderBy(p => p.pixels)
            .ToList();

        // Find two nearest profiles bracketing the target resolution
        var lower = sorted.LastOrDefault(p => p.pixels <= targetPixels);
        var upper = sorted.FirstOrDefault(p => p.pixels >= targetPixels);

        if (lower is null && upper is null) return null;
        if (lower is null) return upper!.Value;
        if (upper is null || lower == upper) return lower.Value;

        double tx = upper.w != lower.w ? (double)(width - lower.w) / (upper.w - lower.w) : 0.0;
        double ty = upper.h != lower.h ? (double)(height - lower.h) / (upper.h - lower.h) : 0.0;
        tx = Math.Clamp(tx, 0.0, 1.0);
        ty = Math.Clamp(ty, 0.0, 1.0);

        return new OcrResolutionProfile(
            (int)(lower.Value.CaptureOffsetX + (upper.Value.CaptureOffsetX - lower.Value.CaptureOffsetX) * tx),
            (int)(lower.Value.CaptureOffsetY + (upper.Value.CaptureOffsetY - lower.Value.CaptureOffsetY) * ty),
            (int)(lower.Value.CaptureWidth + (upper.Value.CaptureWidth - lower.Value.CaptureWidth) * tx),
            (int)(lower.Value.CaptureHeight + (upper.Value.CaptureHeight - lower.Value.CaptureHeight) * ty));
    }
}

public sealed record OcrResolutionProfile(
    int CaptureOffsetX,
    int CaptureOffsetY,
    int CaptureWidth,
    int CaptureHeight);

public sealed record OcrCaptureRegion(int X, int Y, int Width, int Height);
