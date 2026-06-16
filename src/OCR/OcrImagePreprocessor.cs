using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using StructLinq;

namespace RuneshapePriceChecker.OCR;

internal sealed record OcrResult(string Text, int[] RowYPositions, Rectangle? CropBounds = null);

internal static class OcrImagePreprocessor
{
    private static readonly int[] EmptyRowPositions = [];

    public static OcrResult Process(Bitmap captured, NativeTesseractEngine engine, OcrOptions options, string? debugDirectory, OcrPerfTiming? perf = null)
    {
        Bitmap masked;
        using (perf?.Measure(OcrPerfTiming.Slot.KeepBlack))
            masked = KeepBlackAndNeighbors(captured);

        Bitmap preprocessed;
        using (perf?.Measure(OcrPerfTiming.Slot.Preprocess))
            preprocessed = PreprocessForOcr(masked, options);

        // Find black-pixel bounding box and crop to reduce Tesseract work area
        var crop = FindContentBounds(preprocessed);
        using var cropped = crop.HasValue
            ? CropBitmap(preprocessed, crop.Value)
            : (Bitmap)preprocessed.Clone();

        Bitmap bordered;
        using (perf?.Measure(OcrPerfTiming.Slot.Upscale))
        {
            using var upscaled = UpscaleForOcr(cropped, OcrConstants.RowUpscaleFactor);
            bordered = AddWhiteBorder(upscaled, 2);
        }

        if (debugDirectory is not null)
        {
            SavePng(masked, Path.Combine(debugDirectory, "text-extract.png"));
            SavePng(preprocessed, Path.Combine(debugDirectory, "preprocessed.png"));
        }

        engine.SetPageSegMode(4);
        var recognizedText = engine.Recognize(bordered, out var lineYPositions, OcrConstants.RowUpscaleFactor, perf);

        var lines = SplitAndTrim(recognizedText);

        // Adjust Y positions: undo crop offset
        if (crop.HasValue)
        {
            var cropY = crop.Value.Y;
            for (var i = 0; i < lineYPositions.Length; i++)
                lineYPositions[i] += cropY;
        }

        int[] finalRowPositions;
        if (lineYPositions.Length >= lines.Length)
            finalRowPositions = [.. lineYPositions.AsSpan(0, lines.Length)];
        else
            finalRowPositions = ComputeRowPositions(preprocessed, lines.Length);

        return new OcrResult(string.Join(Environment.NewLine, lines), finalRowPositions, crop);
    }

    public static string[] SplitAndTrim(string text)
    {
        return text
            .Split('\n')
            .ToStructEnumerable()
            .Select(line => line.Trim())
            .Where(line => line.Length > 0, _ => _)
            .ToArray();
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

        // Flat 1D byte array for mask: 0=discard, 1=keep. Better cache locality than bool[,].
        var keep = new byte[width * height];

        // First pass: for each black pixel, mark an 11x11 neighborhood as kept
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var keepRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var idx = rowOffset + x * 3;
                if (srcBytes[idx] != 0 || srcBytes[idx + 1] != 0 || srcBytes[idx + 2] != 0)
                    continue;

                // Mark 11x11 block around black pixel
                var y0 = Math.Max(0, y - 5);
                var y1 = Math.Min(height - 1, y + 5);
                var x0 = Math.Max(0, x - 5);
                var x1 = Math.Min(width - 1, x + 5);
                for (var ny = y0; ny <= y1; ny++)
                {
                    var keepNY = ny * width;
                    for (var nx = x0; nx <= x1; nx++)
                        keep[keepNY + nx] = 1;
                }
            }
        }

        // Second pass: build output bitmap from masked pixels
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var dstStride = dstData.Stride;
        var dstLength = Math.Abs(dstStride) * height;
        var dstBytes = new byte[dstLength];

        for (var y = 0; y < height; y++)
        {
            var srcRow = y * stride;
            var dstRow = y * dstStride;
            var keepRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var di = dstRow + x * 3;
                if (keep[keepRow + x] != 0)
                {
                    var si = srcRow + x * 3;
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
        var width = source.Width;
        var height = source.Height;

        // Fast grayscale via LockBits (avoids GDI ColorMatrix rendering overhead)
        var grayscale = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        FastGrayscale(source, grayscale);

        using var grayscaleSnapshot = (Bitmap)grayscale.Clone();

        var rect = new Rectangle(0, 0, width, height);
        var data = grayscale.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
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
            var finalBlackCount = 0;
            for (var i = 0; i < binarizedPixels.Length; i++)
            {
                if (binarizedPixels[i] == 0)
                    finalBlackCount++;
            }
            var blackRatio = totalPixels == 0 ? 1d : (double)finalBlackCount / totalPixels;
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
            return (Bitmap)source.Clone();

        var srcW = source.Width;
        var srcH = source.Height;
        var dstW = srcW * scale;
        var dstH = srcH * scale;

        var srcRect = new Rectangle(0, 0, srcW, srcH);
        var srcData = source.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var srcStride = srcData.Stride;
        var srcBytes = new byte[Math.Abs(srcStride) * srcH];
        Marshal.Copy(srcData.Scan0, srcBytes, 0, srcBytes.Length);
        source.UnlockBits(srcData);

        var upscaled = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb);
        var dstRect = new Rectangle(0, 0, dstW, dstH);
        var dstData = upscaled.LockBits(dstRect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var dstStride = dstData.Stride;
        var dstBytes = new byte[Math.Abs(dstStride) * dstH];

        // Nearest-neighbor pixel replication (no GDI overhead)
        for (var sy = 0; sy < srcH; sy++)
        {
            var srcRow = sy * srcStride;
            for (var dy = 0; dy < scale; dy++)
            {
                var dstRow = (sy * scale + dy) * dstStride;
                for (var sx = 0; sx < srcW; sx++)
                {
                    var b = srcBytes[srcRow + sx * 3];
                    var g = srcBytes[srcRow + sx * 3 + 1];
                    var r = srcBytes[srcRow + sx * 3 + 2];
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var di = dstRow + (sx * scale + dx) * 3;
                        dstBytes[di] = b;
                        dstBytes[di + 1] = g;
                        dstBytes[di + 2] = r;
                    }
                }
            }
        }

        Marshal.Copy(dstBytes, 0, dstData.Scan0, dstBytes.Length);
        upscaled.UnlockBits(dstData);
        return upscaled;
    }

    public static Bitmap AddWhiteBorder(Bitmap source, int borderPx)
    {
        var border = Math.Clamp(borderPx, 0, 8);
        if (border == 0)
            return (Bitmap)source.Clone();

        var srcW = source.Width;
        var srcH = source.Height;
        var dstW = srcW + border * 2;
        var dstH = srcH + border * 2;

        var bordered = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb);
        var dstRect = new Rectangle(0, 0, dstW, dstH);
        var dstData = bordered.LockBits(dstRect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var dstStride = dstData.Stride;
        var dstBytes = new byte[Math.Abs(dstStride) * dstH];

        // Fill with white
        Array.Fill(dstBytes, (byte)255);

        // Copy source into center (avoid GDI DrawImage)
        if (srcW > 0 && srcH > 0)
        {
            var srcRect = new Rectangle(0, 0, srcW, srcH);
            var srcData = source.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var srcStride = srcData.Stride;
            var srcBytes = new byte[Math.Abs(srcStride) * srcH];
            Marshal.Copy(srcData.Scan0, srcBytes, 0, srcBytes.Length);
            source.UnlockBits(srcData);

            for (var y = 0; y < srcH; y++)
            {
                var srcRow = y * srcStride;
                var dstRow = (y + border) * dstStride + border * 3;
                for (var x = 0; x < srcW; x++)
                {
                    var si = srcRow + x * 3;
                    var di = dstRow + x * 3;
                    dstBytes[di] = srcBytes[si];
                    dstBytes[di + 1] = srcBytes[si + 1];
                    dstBytes[di + 2] = srcBytes[si + 2];
                }
            }
        }

        Marshal.Copy(dstBytes, 0, dstData.Scan0, dstBytes.Length);
        bordered.UnlockBits(dstData);
        return bordered;
    }

    public static Rectangle? FindContentBounds(Bitmap binarized)
    {
        var width = binarized.Width;
        var height = binarized.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = binarized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var len = Math.Abs(stride) * height;
            var bytes = new byte[len];
            Marshal.Copy(data.Scan0, bytes, 0, len);

            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;

            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (bytes[row + x * 3] >= 128) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (minX > maxX) return null;

            const int margin = 4;
            minX = Math.Max(0, minX - margin);
            minY = Math.Max(0, minY - margin);
            maxX = Math.Min(width - 1, maxX + margin);
            maxY = Math.Min(height - 1, maxY + margin);

            var cropW = maxX - minX + 1;
            var cropH = maxY - minY + 1;

            // Only crop if it saves at least 20% of area
            if (cropW * cropH >= (width * height) * 0.8)
                return null;

            return new Rectangle(minX, minY, cropW, cropH);
        }
        finally
        {
            binarized.UnlockBits(data);
        }
    }

    public static Bitmap CropBitmap(Bitmap source, Rectangle crop)
    {
        var result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.DrawImage(source,
            new Rectangle(0, 0, crop.Width, crop.Height),
            crop,
            GraphicsUnit.Pixel);
        return result;
    }

    private static (int y0, int y1)[] FindRowBounds(Bitmap binarized)
    {
        var w = binarized.Width;
        var h = binarized.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = binarized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var len = Math.Abs(stride) * h;
            var bytes = new byte[len];
            Marshal.Copy(data.Scan0, bytes, 0, len);

            // Count black pixels per row
            var blackCounts = new int[h];
            for (var y = 0; y < h; y++)
            {
                var row = y * stride;
                var count = 0;
                for (var x = 0; x < w; x++)
                    if (bytes[row + x * 3] < 128) count++;
                blackCounts[y] = count;
            }

            // Find contiguous bands of black pixels (rows with >2% black pixels)
            var threshold = Math.Max(1, w / 50); // ~2% of width
            var rows = new List<(int, int)>();
            var inBand = false;
            var bandStart = 0;

            for (var y = 0; y < h; y++)
            {
                if (blackCounts[y] >= threshold)
                {
                    if (!inBand) { inBand = true; bandStart = y; }
                }
                else if (inBand)
                {
                    inBand = false;
                    var bandHeight = y - bandStart;
                    if (bandHeight >= 4) // skip noise bands
                        rows.Add((bandStart, y));
                }
            }
            if (inBand)
            {
                var bandHeight = h - bandStart;
                if (bandHeight >= 4) rows.Add((bandStart, h));
            }

            return [.. rows];
        }
        finally
        {
            binarized.UnlockBits(data);
        }
    }

    private static Bitmap ExtractRowBitmap(Bitmap upscaled, int y0, int rowHeight, int upscaleFactor)
    {
        var w = upscaled.Width;
        var margin = upscaleFactor; // small vertical margin
        var extractY = Math.Max(0, y0 - margin);
        var extractH = Math.Min(upscaled.Height - extractY, rowHeight + margin * 2);
        var result = new Bitmap(w, extractH, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.White);
        g.DrawImage(upscaled,
            new Rectangle(0, 0, w, extractH),
            new Rectangle(0, extractY, w, extractH),
            GraphicsUnit.Pixel);
        return result;
    }

    private static void FastGrayscale(Bitmap source, Bitmap dest)
    {
        var w = source.Width;
        var h = source.Height;
        var srcRect = new Rectangle(0, 0, w, h);
        var srcData = source.LockBits(srcRect, ImageLockMode.ReadOnly, source.PixelFormat);
        var dstData = dest.LockBits(srcRect, ImageLockMode.WriteOnly, dest.PixelFormat);
        try
        {
            var srcBpp = Image.GetPixelFormatSize(source.PixelFormat) / 8;
            var srcStride = srcData.Stride;
            var dstStride = dstData.Stride;
            var srcBytes = new byte[Math.Abs(srcStride) * h];
            var dstBytes = new byte[Math.Abs(dstStride) * h];
            Marshal.Copy(srcData.Scan0, srcBytes, 0, srcBytes.Length);

            for (var y = 0; y < h; y++)
            {
                var srcRow = y * srcStride;
                var dstRow = y * dstStride;
                for (var x = 0; x < w; x++)
                {
                    var si = srcRow + x * srcBpp;
                    // ITU-R BT.601 luminance: integer approx of 0.299R + 0.587G + 0.114B
                    byte r = srcBytes[si + 2]; // BGR in memory
                    byte g = srcBytes[si + 1];
                    byte b = srcBytes[si];
                    byte lum = (byte)((77 * r + 150 * g + 29 * b) >> 8);
                    var di = dstRow + x * 3;
                    dstBytes[di] = lum;
                    dstBytes[di + 1] = lum;
                    dstBytes[di + 2] = lum;
                }
            }
            Marshal.Copy(dstBytes, 0, dstData.Scan0, dstBytes.Length);
        }
        finally
        {
            source.UnlockBits(srcData);
            dest.UnlockBits(dstData);
        }
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
