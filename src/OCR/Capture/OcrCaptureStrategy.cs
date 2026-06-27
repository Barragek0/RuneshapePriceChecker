using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RuneshapePriceChecker.Startup;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.OCR;

public sealed record CaptureResult(Bitmap Bitmap, string Method);

internal sealed partial class OcrCaptureStrategy(ILogger<OcrCaptureStrategy> logger)
{
    private readonly ILogger<OcrCaptureStrategy> _logger = logger;
    internal static readonly ConcurrentDictionary<string, byte> FailedModes = new(StringComparer.OrdinalIgnoreCase);

    public CaptureResult Capture(OcrCaptureRegion region, WindowCaptureContext? context, OcrOptions options)
    {
        if (LosslessScaling.IsRunning)
            return TryDesktopOnly(region);

        var mode = options.CaptureMode?.ToLowerInvariant() ?? "printwindow";

        if (mode == "desktop")
            return TryDesktopOnly(region);

        if (context is not null && mode == "printwindow")
        {
            if (TryPrintWindow(context, region, out var pwBmp))
            {
                _ = FailedModes.TryRemove("printwindow", out _);
                return new CaptureResult(pwBmp, "window-printwindow");
            }
            _ = FailedModes.TryAdd("printwindow", 0);
            _logger.LogWarning("PrintWindow capture failed — falling back to Desktop.");
            return TryDesktopOnly(region);
        }

        return TryDesktopOnly(region);
    }

    private bool TryPrintWindow(WindowCaptureContext context, OcrCaptureRegion region, out Bitmap bitmap)
    {
        if (TryCaptureWithPrintWindow(context, region, out bitmap))
        {
            if (!IsLikelyInvalidCapture(bitmap))
                return true;
            _logger.LogWarning("PrintWindow: captured bitmap is invalid (all same color or near-black).");
            bitmap.Dispose();
        }

        bitmap = null!;
        return false;
    }

#pragma warning disable CA2000 // Ownership transferred to CaptureResult
    private static CaptureResult TryDesktopOnly(OcrCaptureRegion region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                region.X,
                region.Y,
                0,
                0,
                new Size(region.Width, region.Height),
                CopyPixelOperation.SourceCopy);
        }

        return new CaptureResult(bitmap, "desktop-copyfromscreen");
    }
#pragma warning restore CA2000

    public static bool IsLikelyInvalidCapture(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var length = Math.Abs(data.Stride) * data.Height;
            var bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);

            var min = 255;
            var max = 0;
            var nearBlack = 0;
            var total = data.Width * data.Height;

            for (var y = 0; y < data.Height; y++)
            {
                var row = y * data.Stride;
                for (var x = 0; x < data.Width; x++)
                {
                    var index = row + (x * 3);
                    var luminance = bytes[index];

                    if (luminance < min)
                    {
                        min = luminance;
                    }

                    if (luminance > max)
                    {
                        max = luminance;
                    }

                    if (luminance < 8)
                    {
                        nearBlack++;
                    }
                }
            }

            var dynamicRange = max - min;
            var nearBlackRatio = total == 0 ? 1d : (double)nearBlack / total;

            return dynamicRange < 8 || nearBlackRatio > 0.995d;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool TryCaptureWithPrintWindow(WindowCaptureContext context, OcrCaptureRegion absoluteRegion, out Bitmap bitmap)
    {
        bitmap = null!;

        var sourceX = absoluteRegion.X - context.ClientX;
        var sourceY = absoluteRegion.Y - context.ClientY;

        if (sourceX < 0 || sourceY < 0)
        {
            return false;
        }

        if (sourceX + absoluteRegion.Width > context.ClientWidth ||
            sourceY + absoluteRegion.Height > context.ClientHeight)
        {
            return false;
        }

        using var clientBitmap = new Bitmap(context.ClientWidth, context.ClientHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(clientBitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                const uint pwClientOnly = 0x00000001;
                const uint pwRenderFullContent = 0x00000002;
                var captured = NativeMethods.PrintWindow(context.WindowHandle, hdc, pwClientOnly | pwRenderFullContent);
                if (!captured)
                {
                    return false;
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        var cropRect = new Rectangle(sourceX, sourceY, absoluteRegion.Width, absoluteRegion.Height);
        bitmap = clientBitmap.Clone(cropRect, PixelFormat.Format24bppRgb);
        return true;
    }

    public static void SaveBitmapWithOverwrite(Bitmap bitmap, string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    }
}
