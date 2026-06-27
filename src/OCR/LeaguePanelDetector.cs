using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.OCR;

public sealed record ListDetectorDiag(
    int AvgBrightness,
    int BlackCount,
    int TotalCount,
    bool PanelOpen,
    int MinSum);

public sealed class LeaguePanelDetector(IOptionsMonitor<OcrOptions>? options = null)
{
    public const double DefaultLeftFraction = 0.40;
    public const double DefaultRightFraction = 0.98;
    public const double DefaultTopRowFraction = 0.26;
    public const int DefaultBrightnessThreshold = 120;
    public const int DefaultBlackPixelMaxSum = 20;
    public const int DefaultMinBlackPixels = 60;

    private readonly IOptionsMonitor<OcrOptions>? _options = options;
    private int _brightStreak;
    private int _darkStreak;

    public bool IsOpen { get; private set; }

    public bool Update(Bitmap regionBitmap, out ListDetectorDiag diag)
    {
        ArgumentNullException.ThrowIfNull(regionBitmap);

        var opts = _options?.CurrentValue;
        var brightnessThreshold = opts?.PanelBrightnessThreshold ?? DefaultBrightnessThreshold;
        var blackPixelMaxSum = opts?.PanelBlackPixelMaxSum ?? DefaultBlackPixelMaxSum;
        var minBlackPixels = opts?.PanelMinBlackPixels ?? DefaultMinBlackPixels;

        var (avgBrightness, blackCount, totalCount, minSum) = ScanTopRow(regionBitmap, blackPixelMaxSum);
        bool rawBright = avgBrightness > brightnessThreshold && blackCount >= minBlackPixels;

        diag = new ListDetectorDiag(avgBrightness, blackCount, totalCount, IsOpen, minSum);

        if (rawBright) { _brightStreak++; _darkStreak = 0; }
        else { _darkStreak++; _brightStreak = 0; }

        if (!IsOpen && _brightStreak >= 3) IsOpen = true;
        else if (IsOpen && _darkStreak >= 3) IsOpen = false;

        return IsOpen;
    }

    private static (int avgBrightness, int blackCount, int totalCount, int minSum) ScanTopRow(Bitmap bmp, int blackPixelMaxSum)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data;
        try { data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat); }
        catch { return (0, 0, 0, 0); }

        try
        {
            int bpp = Image.GetPixelFormatSize(bmp.PixelFormat) / 8;
            if (bpp < 3) { bmp.UnlockBits(data); return (0, 0, 0, 0); }

            int stride = data.Stride;
            int len = Math.Abs(stride) * data.Height;
            var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
            try
            {
                Marshal.Copy(data.Scan0, bytes, 0, len);

                long totalBrightness = 0;
                int totalCount = 0;
                int blackCount = 0;
                int minSum = int.MaxValue;

                for (int y = 0; y < bmp.Height; y++)
                {
                    int rowOff = y * stride;
                    for (int cx = 0; cx < bmp.Width; cx++)
                    {
                        int idx = rowOff + (cx * bpp);
                        int r = bytes[idx + 2];
                        int g = bytes[idx + 1];
                        int b = bytes[idx];
                        int sum = r + g + b;

                        totalBrightness += sum / 3;
                        totalCount++;
                        if (sum < blackPixelMaxSum)
                            blackCount++;
                        if (sum < minSum)
                            minSum = sum;
                    }
                }

                int avg = totalCount > 0 ? (int)(totalBrightness / totalCount) : 0;
                return (avg, blackCount, totalCount, minSum == int.MaxValue ? 0 : minSum);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
