using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;

namespace RuneshapePriceChecker.OCR;

internal sealed record OcrResult(string Text, int[] RowYPositions);

internal static class OcrImagePreprocessor
{
    private static readonly int[] EmptyRowPositions = [];

    public static OcrResult Process(Bitmap captured, NativeTesseractEngine engine, OcrOptions options, string? debugDirectory)
    {
        using var masked = KeepBlackAndNeighbors(captured);
        using var preprocessed = PreprocessForOcr(masked, options);
        using var upscaled = UpscaleForOcr(preprocessed, OcrConstants.RowUpscaleFactor);
        using var bordered = AddWhiteBorder(upscaled, 2);

        if (debugDirectory is not null)
        {
            SavePng(masked, Path.Combine(debugDirectory, "text-extract.png"));
            SavePng(preprocessed, Path.Combine(debugDirectory, "preprocessed.png"));
        }

        engine.SetPageSegMode(3);
        var recognizedText = engine.Recognize(bordered, out var lineYPositions, OcrConstants.RowUpscaleFactor);

        var lines = recognizedText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();

        int[] finalRowPositions;
        if (lineYPositions.Length >= lines.Length)
        {
            finalRowPositions = lineYPositions.Take(lines.Length).ToArray();
        }
        else
        {
            finalRowPositions = ComputeRowPositions(preprocessed, lines.Length);
        }

        return new OcrResult(string.Join(Environment.NewLine, lines), finalRowPositions);
    }

    private static void SavePng(Bitmap bitmap, string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        bitmap.Save(path, ImageFormat.Png);
    }

    public static Bitmap KeepBlackAndNeighbors(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var rect = new Rectangle(0, 0, width, height);
        var srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var stride = srcData.Stride;
        var length = Math.Abs(stride) * height;
        var srcBytes = new byte[length];
        Marshal.Copy(srcData.Scan0, srcBytes, 0, length);
        source.UnlockBits(srcData);

        var keep = new bool[height, width];

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = rowOffset + (x * 3);
                if (srcBytes[index] != 0 || srcBytes[index + 1] != 0 || srcBytes[index + 2] != 0)
                    continue;

                for (var dy = -5; dy <= 5; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (var dx = -5; dx <= 5; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width) continue;
                        keep[ny, nx] = true;
                    }
                }
            }
        }

        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var dstStride = dstData.Stride;
        var dstLength = Math.Abs(dstStride) * height;
        var dstBytes = new byte[dstLength];

        for (var y = 0; y < height; y++)
        {
            var srcRow = y * stride;
            var dstRow = y * dstStride;
            for (var x = 0; x < width; x++)
            {
                var si = srcRow + (x * 3);
                var di = dstRow + (x * 3);
                if (keep[y, x])
                {
                    dstBytes[di] = srcBytes[si];
                    dstBytes[di + 1] = srcBytes[si + 1];
                    dstBytes[di + 2] = srcBytes[si + 2];
                }
                else
                {
                    dstBytes[di] = dstBytes[di + 1] = dstBytes[di + 2] = 255;
                }
            }
        }

        Marshal.Copy(dstBytes, 0, dstData.Scan0, dstLength);
        result.UnlockBits(dstData);

        return result;
    }

    public static Bitmap PreprocessForOcr(Bitmap source, OcrOptions options)
    {
        var threshold = Math.Clamp(options.BinarizationThreshold, 0, 255);

        var grayscale = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(grayscale))
        using (var attributes = new ImageAttributes())
        {
            var colorMatrix = new ColorMatrix(
            [
                [0.299f, 0.299f, 0.299f, 0, 0],
                [0.587f, 0.587f, 0.587f, 0, 0],
                [0.114f, 0.114f, 0.114f, 0, 0],
                [0, 0, 0, 1, 0],
                [0, 0, 0, 0, 1]
            ]);

            attributes.SetColorMatrix(colorMatrix);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, source.Width, source.Height),
                0,
                0,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel,
                attributes);
        }

        using var grayscaleSnapshot = (Bitmap)grayscale.Clone();

        var rect = new Rectangle(0, 0, grayscale.Width, grayscale.Height);
        var data = grayscale.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var width = data.Width;
            var height = data.Height;
            var kernelRadius = Math.Clamp(Math.Min(width, height) / 40, 4, 10);
            var contrastBias = Math.Clamp((threshold - 100) / 6, 6, 20);

            var length = Math.Abs(data.Stride) * data.Height;
            var bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);
            var grayscalePixels = new byte[width * height];

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * data.Stride;
                var grayOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    grayscalePixels[grayOffset + x] = bytes[rowOffset + (x * 3)];
                }
            }

            var integral = BuildIntegralImage(grayscalePixels, width, height);
            var binarizedPixels = new byte[width * height];

            var sourceRgb = ReadRgbPixels(source);

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var mean = GetLocalMean(integral, width, height, x, y, kernelRadius);
                    var luminance = grayscalePixels[rowOffset + x];
                    var binarized = luminance + contrastBias < mean ? (byte)0 : (byte)255;
                    if (binarized == 0 &&
                        options.EnableTextColorFiltering &&
                        !IsLikelyTextColor(sourceRgb, rowOffset + x, options))
                    {
                        binarized = 255;
                    }

                    binarizedPixels[rowOffset + x] = binarized;
                }
            }

            RemoveIsolatedBlackNoise(binarizedPixels, width, height);

            var totalPixels = width * height;
            var finalBlackPixels = binarizedPixels.Count(pixel => pixel == 0);
            var blackRatio = totalPixels == 0 ? 1d : (double)finalBlackPixels / totalPixels;
            if (blackRatio > 0.97d)
            {
                return (Bitmap)grayscaleSnapshot.Clone();
            }

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * data.Stride;
                var binaryOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var index = rowOffset + (x * 3);
                    var value = binarizedPixels[binaryOffset + x];
                    bytes[index] = value;
                    bytes[index + 1] = value;
                    bytes[index + 2] = value;
                }
            }

            Marshal.Copy(bytes, 0, data.Scan0, length);
            return grayscale;
        }
        finally
        {
            grayscale.UnlockBits(data);
        }
    }

    public static Bitmap UpscaleForOcr(Bitmap source, int scaleFactor)
    {
        var scale = Math.Clamp(scaleFactor, 1, 4);
        if (scale == 1)
        {
            return (Bitmap)source.Clone();
        }

        var upscaled = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(upscaled);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.DrawImage(source, 0, 0, upscaled.Width, upscaled.Height);
        return upscaled;
    }

    public static Bitmap AddWhiteBorder(Bitmap source, int borderPx)
    {
        var border = Math.Clamp(borderPx, 0, 8);
        if (border == 0)
        {
            return (Bitmap)source.Clone();
        }

        var bordered = new Bitmap(source.Width + (border * 2), source.Height + (border * 2), PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bordered);
        graphics.Clear(Color.White);
        graphics.DrawImage(source, border, border, source.Width, source.Height);
        return bordered;
    }

    public static int[] ComputeRowPositions(Bitmap binarized, int itemCount)
    {
        var width = binarized.Width;
        var height = binarized.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = binarized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var length = Math.Abs(stride) * height;
            var bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);

            var blackCounts = new int[height];
            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (bytes[rowOffset + (x * 3)] == 0)
                        blackCounts[y]++;
                }
            }

            var threshold = Math.Max(1, width / 30);
            var textTop = -1;
            var textBottom = -1;
            for (var y = 0; y < height; y++)
            {
                if (blackCounts[y] >= threshold) { if (textTop < 0) textTop = y; textBottom = y; }
            }

            if (textTop < 0) return EmptyRowPositions;

            var rowH = (float)(textBottom - textTop + 1) / itemCount;
            var positions = new int[itemCount];
            for (var i = 0; i < itemCount; i++)
                positions[i] = textTop + (int)((i + 0.5f) * rowH);
            return positions;
        }
        finally
        {
            binarized.UnlockBits(data);
        }
    }

    private static int[] BuildIntegralImage(byte[] grayscalePixels, int width, int height)
    {
        var integralWidth = width + 1;
        var integral = new int[integralWidth * (height + 1)];

        for (var y = 1; y <= height; y++)
        {
            var rowSum = 0;
            var grayOffset = (y - 1) * width;
            var integralOffset = y * integralWidth;
            var previousIntegralOffset = (y - 1) * integralWidth;

            for (var x = 1; x <= width; x++)
            {
                rowSum += grayscalePixels[grayOffset + x - 1];
                integral[integralOffset + x] = integral[previousIntegralOffset + x] + rowSum;
            }
        }

        return integral;
    }

    private static byte[] ReadRgbPixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var width = data.Width;
            var height = data.Height;
            var stride = data.Stride;
            var rawLength = Math.Abs(stride) * height;
            var raw = new byte[rawLength];
            Marshal.Copy(data.Scan0, raw, 0, rawLength);

            var rgb = new byte[width * height * 3];
            for (var y = 0; y < height; y++)
            {
                var srcRow = y * stride;
                var dstRow = y * width * 3;
                for (var x = 0; x < width; x++)
                {
                    var src = srcRow + (x * 3);
                    var dst = dstRow + (x * 3);
                    rgb[dst] = raw[src + 2];
                    rgb[dst + 1] = raw[src + 1];
                    rgb[dst + 2] = raw[src];
                }
            }

            return rgb;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool IsLikelyTextColor(byte[] rgbPixels, int pixelIndex, OcrOptions options)
    {
        var rgbIndex = pixelIndex * 3;
        var r = rgbPixels[rgbIndex];
        var g = rgbPixels[rgbIndex + 1];
        var b = rgbPixels[rgbIndex + 2];

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var spread = max - min;
        var luminance = ((299 * r) + (587 * g) + (114 * b)) / 1000;

        if (luminance > options.TextColorMaxLuminance)
        {
            return false;
        }

        var dr = r - options.TextColorTargetR;
        var dg = g - options.TextColorTargetG;
        var db = b - options.TextColorTargetB;
        var distanceSquared = (dr * dr) + (dg * dg) + (db * db);
        var toleranceSquared = options.TextColorTolerance * options.TextColorTolerance;

        if (distanceSquared <= toleranceSquared)
        {
            return true;
        }

        return spread <= options.TextColorMaxChannelSpread;
    }

    private static int GetLocalMean(int[] integral, int width, int height, int x, int y, int radius)
    {
        var integralWidth = width + 1;

        var x0 = Math.Max(0, x - radius);
        var y0 = Math.Max(0, y - radius);
        var x1 = Math.Min(width - 1, x + radius);
        var y1 = Math.Min(height - 1, y + radius);

        var area = (x1 - x0 + 1) * (y1 - y0 + 1);

        var topLeft = integral[(y0 * integralWidth) + x0];
        var topRight = integral[(y0 * integralWidth) + x1 + 1];
        var bottomLeft = integral[((y1 + 1) * integralWidth) + x0];
        var bottomRight = integral[((y1 + 1) * integralWidth) + x1 + 1];

        return (bottomRight - bottomLeft - topRight + topLeft) / Math.Max(1, area);
    }

    private static void RemoveIsolatedBlackNoise(byte[] pixels, int width, int height)
    {
        var source = (byte[])pixels.Clone();

        for (var y = 1; y < height - 1; y++)
        {
            var offset = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var idx = offset + x;
                if (source[idx] != 0)
                    continue;

                var blackCount = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = (y + dy) * width;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (source[ny + (x + dx)] == 0) blackCount++;
                    }
                }

                if (blackCount <= 2)
                    pixels[idx] = 255;
            }
        }
    }
}
