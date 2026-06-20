using System.Drawing.Imaging;

namespace RuneshapePriceChecker.OCR;

internal static class OcrPipeline
{
    public sealed record Result(
        string Text,
        int[] RowYPositions,
        int[] RowHeights,
        Rectangle? CropBounds);
    public static Result Run(
        Bitmap capturedBitmap,
        OcrOptions options,
        OcrPerfTiming perf,
        Func<Bitmap, int, int, string?> recognizeRow,
        string? debugDirectory)
    {
        Bitmap masked;
        using (perf.Measure(OcrPerfTiming.Slot.KeepBlack))
            masked = OcrImagePreprocessor.KeepBlackAndNeighbors(capturedBitmap);

        Bitmap preprocessed;
        using (perf.Measure(OcrPerfTiming.Slot.Preprocess))
            preprocessed = OcrImagePreprocessor.PreprocessForOcr(masked, options);

        using var _ = preprocessed;
        using var __ = masked;

        var crop = OcrImagePreprocessor.FindContentBounds(preprocessed);

        if (debugDirectory is not null)
            SaveDebugSteps(capturedBitmap, masked, preprocessed, crop, debugDirectory);

        var (rowYs, rowHeights) = DetectRowPositions(preprocessed, crop);

        if (rowYs.Length == 0)
            return new Result(string.Empty, [], [], crop);

        var rowTexts = new string[rowYs.Length];
        for (var i = 0; i < rowYs.Length; i++)
        {
            var text = recognizeRow(preprocessed, rowYs[i], rowHeights[i]);
            rowTexts[i] = text ?? string.Empty;

            if (debugDirectory is not null)
                SaveRowDebugImage(preprocessed, crop, rowYs[i], rowHeights[i], i, debugDirectory);
        }

        if (debugDirectory is not null)
            SaveRowOverlayDebugImage(preprocessed, crop, rowYs, rowHeights, debugDirectory);

        return new Result(
            string.Join(Environment.NewLine, rowTexts),
            rowYs,
            rowHeights,
            crop);
    }
    public static Bitmap PrepareRowBitmap(Bitmap source, Rectangle? contentCrop, int rowY, int rowHeight)
    {
        var cropX = contentCrop?.X ?? 0;
        var cropW = contentCrop?.Width > 0
            ? Math.Min(contentCrop.Value.Width, source.Width - cropX)
            : source.Width;
        if (cropW <= 0) cropW = 200;

        // 4px horizontal padding so character edges aren't clipped
        var padX = Math.Max(0, cropX - 4);
        var padW = Math.Min(cropW + 8, source.Width - padX);

        var cropRect = new Rectangle(padX, rowY, padW, rowHeight);
        var rowBitmap = source.Clone(cropRect, source.PixelFormat);

        using var upscaled = OcrImagePreprocessor.UpscaleForOcr(rowBitmap, 2);
        var bordered = OcrImagePreprocessor.AddWhiteBorder(upscaled, 2);

        rowBitmap.Dispose();
        return bordered;
    }
    public static (int[] Ys, int[] Heights) DetectRowPositions(Bitmap source, Rectangle? contentCrop)
    {
        var scanRegion = contentCrop ?? new Rectangle(0, 0, source.Width, source.Height);
        var scanX = scanRegion.X;
        var scanW = scanRegion.Width;
        var scanY = scanRegion.Y;
        var scanH = scanRegion.Height;

        const int threshold = 2;
        const int minWidthPixels = 2;

        var widthThreshold = Math.Max(minWidthPixels, (int)(scanW * 0.03));

        var blackCounts = new int[scanH];
        var srcRect = new Rectangle(scanX, scanY, scanW, scanH);
        var data = source.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var stride = data.Stride;
        var length = Math.Abs(stride) * scanH;
        var bytes = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, length);
        source.UnlockBits(data);

        for (var y = 0; y < scanH; y++)
        {
            var rowOffset = y * stride;
            var count = 0;
            for (var x = 0; x < scanW; x++)
            {
                var idx = rowOffset + x * 3;
                if (bytes[idx] < 128)
                    count++;
            }
            blackCounts[y] = count >= widthThreshold ? count : 0;
        }

        List<(int Start, int End)> regions = [];
        var inRow = false;
        var regionStart = 0;

        for (var y = 0; y < scanH; y++)
        {
            if (blackCounts[y] >= threshold)
            {
                if (!inRow)
                {
                    regionStart = y;
                    inRow = true;
                }
            }
            else
            {
                if (inRow && y - regionStart >= 4)
                {
                    regions.Add((regionStart, y - 1));
                }
                inRow = false;
            }
        }
        if (inRow && scanH - regionStart >= 4)
            regions.Add((regionStart, scanH - 1));

        if (regions.Count == 0)
            return ([], []);

        // Merge regions less than 10px apart
        var merged = new List<(int Start, int End)> { regions[0] };
        for (var r = 1; r < regions.Count; r++)
        {
            var last = merged[^1];
            if (regions[r].Start - last.End <= 10)
                merged[^1] = (last.Start, regions[r].End);
            else
                merged.Add(regions[r]);
        }

        // Expand each merged band by 4px on each side (for descenders/ascenders)
        var result = new int[merged.Count];
        var heights = new int[merged.Count];
        for (var r = 0; r < merged.Count; r++)
        {
            var start = Math.Max(0, merged[r].Start - 4);
            var end = Math.Min(scanH - 1, merged[r].End + 4);
            result[r] = scanY + start;
            heights[r] = end - start + 1;
        }

        return (result, heights);
    }

    #region Debug image helpers

    internal static void SaveDebugSteps(Bitmap raw, Bitmap masked, Bitmap preprocessed, Rectangle? crop, string dir)
    {
        OcrImagePreprocessor.SavePng(raw, Path.Combine(dir, "1 Raw.png"));
        OcrImagePreprocessor.SavePng(masked, Path.Combine(dir, "2 Mask.png"));
        OcrImagePreprocessor.SavePng(preprocessed, Path.Combine(dir, "3 Preprocessed.png"));
        if (crop.HasValue)
        {
            using var croppedDbg = OcrImagePreprocessor.CropBitmap(preprocessed, crop.Value);
            OcrImagePreprocessor.SavePng(croppedDbg, Path.Combine(dir, "4 Cropped.png"));
        }
    }

    public static void SaveRowDebugImage(Bitmap preprocessed, Rectangle? crop, int rowY, int rowHeight, int index, string dir)
    {
        var cropX = crop?.X ?? 0;
        var cropW = crop?.Width > 0
            ? Math.Min(crop.Value.Width, preprocessed.Width - cropX)
            : preprocessed.Width;
        if (cropW <= 0) return;

        var rowRect = new Rectangle(cropX, rowY, cropW, rowHeight);
        if (rowRect.Height <= 0 || rowRect.Bottom > preprocessed.Height) return;

        using var rowDbg = preprocessed.Clone(rowRect, preprocessed.PixelFormat);
        OcrImagePreprocessor.SavePng(rowDbg, Path.Combine(dir, $"Row{index + 1}.png"));
    }

    public static void SaveRowOverlayDebugImage(Bitmap preprocessed, Rectangle? crop, int[] rowYs, int[] rowHeights, string dir)
    {
        var cropRect = crop ?? new Rectangle(0, 0, preprocessed.Width, preprocessed.Height);
        using var cropped = OcrImagePreprocessor.CropBitmap(preprocessed, cropRect);
        using var g = Graphics.FromImage(cropped);
        using var pen = new Pen(Color.FromArgb(180, 200, 50, 220), 2f);

        for (var i = 0; i < rowYs.Length && i < rowHeights.Length; i++)
        {
            var localY = rowYs[i] - cropRect.Y;
            var h = rowHeights[i];
            if (localY >= 0 && localY + h <= cropped.Height)
                g.DrawRectangle(pen, 1, localY, cropped.Width - 2, h - 1);
        }

        OcrImagePreprocessor.SavePng(cropped, Path.Combine(dir, "5 Rows.png"));
    }

    #endregion
}
