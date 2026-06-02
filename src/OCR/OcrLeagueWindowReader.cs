using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.OCR;

public sealed class OcrLeagueWindowReader(
    IOptionsMonitor<OcrOptions> options,
    IOptionsMonitor<AppOptions> appOptions,
    IPoe2WindowResolutionProvider windowResolutionProvider,
    IAdaptiveRowShiftState adaptiveRowShiftState,
    ILogger<OcrLeagueWindowReader> logger) : ILeagueWindowReader
{
    private sealed record DebugCaptureContext(string DirectoryPath);

    private readonly IOptionsMonitor<OcrOptions> _options = options;
    private readonly IOptionsMonitor<AppOptions> _appOptions = appOptions;
    private readonly IAdaptiveRowShiftState _adaptiveRowShiftState = adaptiveRowShiftState;
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NonNameChars = new("[^-A-Za-z0-9'’ ]+", RegexOptions.Compiled);
    private static readonly Regex QuantityPrefixToken = new("^(?<quantity>[A-Za-z0-9|]{1,2})\\s*[xX]\\b", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityDigits = new("(?<quantity>\\d{1,2})\\s*[xX]?", RegexOptions.Compiled);
    private static readonly Regex LeadingGuardNumberToken = new("^\\s*(?<num>\\d{1,2})(?:\\D.*)?$", RegexOptions.Compiled);
    private const int DefaultAdaptiveShiftProbeWidthPx = 26;
    private const int DefaultAdaptiveShiftStepPx = 35;
    private const int DefaultAdaptiveShiftMaxPx = 160;
    private const int DefaultAdaptiveShiftProbeMinDarkPixels = 20;
    private const int MaxParallelRowOcr = 4;
    private bool _tesseractUnavailable;
    private bool _windowCaptureUnavailableLogged;
    private bool _waitingForWindowContextLogged;
    private bool _waitingForForegroundWindowLogged;
    private bool _waitingForLeagueListingPanelLogged;
    private bool _debugImageDirectoryLogged;
    private bool _tesseractExecutionConfirmedLogged;
    private string _lastCaptureMethod = string.Empty;
    private DateTimeOffset _lastDebugImageSavedAtUtc = DateTimeOffset.MinValue;

    public LeagueWindowSnapshot ReadSnapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var adaptiveParams = GetAdaptiveParams(windowResolutionProvider.CurrentResolutionProfile);

        if (_tesseractUnavailable)
        {
            _adaptiveRowShiftState.Update([], adaptiveParams.StepPx, false);
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt);
        }

        try
        {
            var rawText = CaptureAndRecognize(out var attemptedRecognition);
            if (!attemptedRecognition)
            {
                _adaptiveRowShiftState.Update([], adaptiveParams.StepPx, false);
                return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt);
            }

            if (_appOptions.CurrentValue.EnableDebugLogging && !_tesseractExecutionConfirmedLogged)
            {
                _tesseractExecutionConfirmedLogged = true;
                logger.LogInformation("OCR engine confirmed: tesseract executed successfully.");
            }

            var lines = ExtractLikelyItemNames(rawText);
            if (_appOptions.CurrentValue.EnableDebugLogging)
            {
                var items = lines.Count == 0 ? "<none>" : string.Join(" | ", lines);
                logger.LogInformation("OCR detected {Count} items: {Items}", lines.Count, items);
            }

            return new LeagueWindowSnapshot(lines, capturedAt);
        }
        catch (FileNotFoundException ex)
        {
            _tesseractUnavailable = true;
            logger.LogWarning(
                "OCR disabled: {Reason} If startup auto-install did not complete, install Tesseract and restart RuneshapePriceChecker.",
                ex.Message);
            _adaptiveRowShiftState.Update([], adaptiveParams.StepPx, false);
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OCR capture/recognition failed.");
            _adaptiveRowShiftState.Update([], adaptiveParams.StepPx, false);
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt);
        }
    }

    private string CaptureAndRecognize(out bool attemptedRecognition)
    {
        attemptedRecognition = false;
        var options = _options.CurrentValue;
        var adaptiveParams = GetAdaptiveParams(windowResolutionProvider.CurrentResolutionProfile);
        if (options.SaveDebugImages)
        {
            EnsureDebugImageDirectoryExists(options);
        }

        if (!windowResolutionProvider.IsPoe2WindowForeground || !IsPoe2ForegroundNow())
        {
            _adaptiveRowShiftState.Update([], adaptiveParams.StepPx, false);
            if (_appOptions.CurrentValue.EnableDebugLogging && !_waitingForForegroundWindowLogged)
            {
                _waitingForForegroundWindowLogged = true;
                logger.LogInformation("OCR paused: waiting for Path of Exile 2 to be the active foreground window.");
            }

            return string.Empty;
        }

        _waitingForForegroundWindowLogged = false;

        if (options.UseWindowClientCapture && windowResolutionProvider.CurrentWindowCaptureContext is null)
        {
            _adaptiveRowShiftState.Update([], adaptiveParams.StepPx, false);
            if (_appOptions.CurrentValue.EnableDebugLogging && !_waitingForWindowContextLogged)
            {
                _waitingForWindowContextLogged = true;
                logger.LogInformation("OCR warm-up: waiting for PoE2 window capture context before first scan.");
            }

            return string.Empty;
        }

        _waitingForWindowContextLogged = false;

        var region = ResolveCaptureRegion();
        ValidateRegion(region);

        using var capturedBitmap = CaptureBitmap(region, out var captureMethod, options);
        if (_appOptions.CurrentValue.EnableDebugLogging &&
            !string.Equals(_lastCaptureMethod, captureMethod, StringComparison.OrdinalIgnoreCase))
        {
            _lastCaptureMethod = captureMethod;
            logger.LogInformation("OCR capture source active: {CaptureMethod}.", captureMethod);
        }

        if (!IsLeaguePanelAnchorColorMatch(capturedBitmap, options, out var anchorSignal))
        {
            _adaptiveRowShiftState.Update([], adaptiveParams.StepPx, false);
            if (_appOptions.CurrentValue.EnableDebugLogging && !_waitingForLeagueListingPanelLogged)
            {
                _waitingForLeagueListingPanelLogged = true;
                logger.LogInformation(
                    "OCR paused: waiting for league listing panel anchor color. Signal X={X} Y={Y} RGB=({R},{G},{B}) L={Luminance} Spread={Spread} Distance={Distance:F1}.",
                    anchorSignal.X,
                    anchorSignal.Y,
                    anchorSignal.R,
                    anchorSignal.G,
                    anchorSignal.B,
                    anchorSignal.Luminance,
                    anchorSignal.ChannelSpread,
                    anchorSignal.DistanceToTarget);
            }

            return string.Empty;
        }

        _waitingForLeagueListingPanelLogged = false;

        using var ocrBitmap = options.EnableImagePreprocessing
            ? PreprocessForOcr(capturedBitmap, options)
            : (Bitmap)capturedBitmap.Clone();

        var debugContext = options.SaveDebugImages
            ? TryStartDebugCapture(capturedBitmap, ocrBitmap, region, captureMethod)
            : null;

        attemptedRecognition = true;
        return ExecuteTesseractByRows(ocrBitmap, debugContext, options);
    }

    private bool IsPoe2ForegroundNow()
    {
        var context = windowResolutionProvider.CurrentWindowCaptureContext;
        if (context is null)
        {
            return false;
        }

        var foregroundHandle = NativeMethods.GetForegroundWindow();
        if (foregroundHandle == IntPtr.Zero)
        {
            return false;
        }

        return foregroundHandle == context.WindowHandle ||
               NativeMethods.AreWindowFamilyRelated(context.WindowHandle, foregroundHandle);
    }

    private string ExecuteTesseractByRows(Bitmap bitmap, DebugCaptureContext? debugContext, OcrOptions options)
    {
        var profile = windowResolutionProvider.CurrentResolutionProfile;
        var adaptiveParams = GetAdaptiveParams(profile);
        var rowTextHeight = profile?.RowTextHeight ?? options.RowTextHeight;
        var rowGapHeight = profile?.RowGapHeight ?? options.RowGapHeight;
        var rowLateOffsetStartRow = profile?.RowLateOffsetStartRow ?? int.MaxValue;
        var rowLateOffsetStepRows = profile?.RowLateOffsetStepRows ?? 1;
        var rowLateOffsetStepPx = profile?.RowLateOffsetStepPx ?? 0;
        var adaptiveDecision = ComputeAdaptiveShiftStartRows(
            bitmap,
            debugContext,
            options,
            rowTextHeight,
            rowGapHeight,
            rowLateOffsetStartRow,
            rowLateOffsetStepRows,
            rowLateOffsetStepPx,
            adaptiveParams.ProbeWidthPx,
            adaptiveParams.StepPx,
            adaptiveParams.MaxPx,
            adaptiveParams.ProbeMinDarkPixels);
        _adaptiveRowShiftState.Update(
            adaptiveDecision.ShiftStartRows,
            adaptiveParams.StepPx,
            adaptiveDecision.ShiftStartRows.Count > 0);
        LogAdaptiveShiftDecision(adaptiveDecision, adaptiveParams);

        var rowRects = OcrRowLayout.BuildRowRectangles(
            bitmap.Width,
            bitmap.Height,
            options.OcrRowCount,
            options.UseFixedRowGeometry,
            options.RowStartOffsetY,
            rowTextHeight,
            rowGapHeight,
            rowLateOffsetStartRow,
            rowLateOffsetStepRows,
            rowLateOffsetStepPx,
            adaptiveDecision.ShiftStartRows,
            adaptiveParams.StepPx);
        if (rowRects.Count == 1)
        {
            using var cleaned = PrepareRowBitmapForOcr(bitmap, options);
            using var upscaled = UpscaleForOcr(cleaned, options.RowUpscaleFactor);
            using var bordered = AddWhiteBorder(upscaled, 2);
            if (debugContext is not null)
            {
                TrySaveRowDebugImage(debugContext, upscaled, 0);
            }

            var rowText = ExecuteTesseractForBitmap(bordered, options.PageSegmentationMode, options).Trim();
            rowText = TryRefineAmbiguousQuantityPrefix(rowText, upscaled, options);
            return rowText;
        }

        var rowBitmaps = new Bitmap[rowRects.Count];
        try
        {
            for (var rowIndex = 0; rowIndex < rowRects.Count; rowIndex++)
            {
                rowBitmaps[rowIndex] = bitmap.Clone(rowRects[rowIndex], PixelFormat.Format24bppRgb);
            }

            var rowTexts = new string[rowRects.Count];
            var parallelism = Math.Clamp(Math.Min(Environment.ProcessorCount, MaxParallelRowOcr), 1, MaxParallelRowOcr);
            Parallel.For(
                0,
                rowRects.Count,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                rowIndex =>
                {
                    using var rowBitmap = rowBitmaps[rowIndex];
                    using var cleanedRow = PrepareRowBitmapForOcr(rowBitmap, options);
                    using var upscaledRow = UpscaleForOcr(cleanedRow, options.RowUpscaleFactor);
                    using var borderedRow = AddWhiteBorder(upscaledRow, 2);
                    if (debugContext is not null)
                    {
                        TrySaveRowDebugImage(debugContext, upscaledRow, rowIndex);
                    }

                    var rowText = ExecuteTesseractForBitmap(borderedRow, options.RowPageSegmentationMode, options).Trim();
                    rowText = TryRefineAmbiguousQuantityPrefix(rowText, upscaledRow, options);
                    rowTexts[rowIndex] = rowText;
                });

            return string.Join(
                Environment.NewLine,
                rowTexts.Where(static text => !string.IsNullOrWhiteSpace(text)));
        }
        finally
        {
            for (var i = 0; i < rowBitmaps.Length; i++)
            {
                rowBitmaps[i]?.Dispose();
            }
        }
    }

    private void TrySaveRowDebugImage(DebugCaptureContext context, Bitmap rowBitmap, int rowIndex)
    {
        try
        {
            var rowPath = Path.Combine(
                context.DirectoryPath,
                $"{rowIndex + 1}.png");
            SaveBitmapWithOverwrite(rowBitmap, rowPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save OCR row debug image {RowIndex}.", rowIndex + 1);
        }
    }

    private void TrySaveBackupGuardDebugImage(DebugCaptureContext context, Bitmap guardBitmap, int rowNumber)
    {
        try
        {
            var rowPath = Path.Combine(
                context.DirectoryPath,
                $"{rowNumber}bg.png");
            SaveBitmapWithOverwrite(guardBitmap, rowPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save OCR backup guard debug image for row {RowNumber}.", rowNumber);
        }
    }

    private static void SaveBitmapWithOverwrite(Bitmap bitmap, string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private string ExecuteTesseractForBitmap(Bitmap bitmap, int psm, OcrOptions options)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"runeshapepricechecker-ocr-{Guid.NewGuid():N}.png");
        try
        {
            bitmap.Save(filePath, ImageFormat.Png);
            return ExecuteTesseract(filePath, psm, options, null);
        }
        finally
        {
            TryDelete(filePath);
        }
    }

    private string ExecuteTesseractForBitmap(Bitmap bitmap, int psm, OcrOptions options, IReadOnlyList<string>? extraConfigs)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"runeshapepricechecker-ocr-{Guid.NewGuid():N}.png");
        try
        {
            bitmap.Save(filePath, ImageFormat.Png);
            return ExecuteTesseract(filePath, psm, options, extraConfigs);
        }
        finally
        {
            TryDelete(filePath);
        }
    }

    private Bitmap CaptureBitmap(OcrCaptureRegion region, out string captureMethod, OcrOptions options)
    {
        if (options.UseWindowClientCapture &&
            windowResolutionProvider.CurrentWindowCaptureContext is { } context &&
            TryCaptureFromWindowClient(context, region, out var windowBitmap))
        {
            if (!IsLikelyInvalidCapture(windowBitmap))
            {
                captureMethod = "window-bitblt";
                return windowBitmap;
            }

            windowBitmap.Dispose();
        }

        if (options.UseWindowClientCapture &&
            windowResolutionProvider.CurrentWindowCaptureContext is { } printContext &&
            TryCaptureWithPrintWindow(printContext, region, out var printBitmap))
        {
            if (!IsLikelyInvalidCapture(printBitmap))
            {
                captureMethod = "window-printwindow";
                return printBitmap;
            }

            printBitmap.Dispose();
        }

        if (options.UseWindowClientCapture && !_windowCaptureUnavailableLogged)
        {
            _windowCaptureUnavailableLogged = true;
            logger.LogWarning("Window-client capture unavailable (BitBlt/PrintWindow). Falling back to desktop capture; overlapping windows can pollute OCR.");
        }

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

        if (IsLikelyInvalidCapture(bitmap))
        {
            logger.LogWarning("Desktop capture frame appears invalid/black. Verify PoE2 is visible and not minimized.");
        }

        captureMethod = "desktop-copyfromscreen";
        return bitmap;
    }

    private static bool IsLikelyInvalidCapture(Bitmap bitmap)
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

    private sealed record LeaguePanelAnchorSignal(
        int X,
        int Y,
        int R,
        int G,
        int B,
        int Luminance,
        int ChannelSpread,
        double DistanceToTarget);

    private static bool IsLeaguePanelAnchorColorMatch(Bitmap bitmap, OcrOptions options, out LeaguePanelAnchorSignal signal)
    {
        signal = new LeaguePanelAnchorSignal(0, 0, 0, 0, 0, 0, 0, double.MaxValue);
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        var sampleX = Math.Clamp(options.LeaguePanelAnchorSampleX, 0, bitmap.Width - 1);
        var sampleY = Math.Clamp(options.LeaguePanelAnchorSampleY, 0, bitmap.Height - 1);
        var sampleRadius = Math.Clamp(options.LeaguePanelAnchorSampleRadiusPx, 0, 3);

        var minX = Math.Max(0, sampleX - sampleRadius);
        var maxX = Math.Min(bitmap.Width - 1, sampleX + sampleRadius);
        var minY = Math.Max(0, sampleY - sampleRadius);
        var maxY = Math.Min(bitmap.Height - 1, sampleY + sampleRadius);

        var rgbPixels = ReadRgbPixels(bitmap);
        var sampleCount = 0;
        var sumR = 0;
        var sumG = 0;
        var sumB = 0;

        for (var y = minY; y <= maxY; y++)
        {
            var rowOffset = y * bitmap.Width;
            for (var x = minX; x <= maxX; x++)
            {
                var pixelIndex = rowOffset + x;
                var rgbIndex = pixelIndex * 3;
                sumR += rgbPixels[rgbIndex];
                sumG += rgbPixels[rgbIndex + 1];
                sumB += rgbPixels[rgbIndex + 2];
                sampleCount++;
            }
        }

        if (sampleCount == 0)
        {
            return false;
        }

        var r = sumR / sampleCount;
        var g = sumG / sampleCount;
        var b = sumB / sampleCount;
        var maxChannel = Math.Max(r, Math.Max(g, b));
        var minChannel = Math.Min(r, Math.Min(g, b));
        var spread = maxChannel - minChannel;
        var luminance = ((299 * r) + (587 * g) + (114 * b)) / 1000;

        var dr = r - options.LeaguePanelAnchorTargetR;
        var dg = g - options.LeaguePanelAnchorTargetG;
        var db = b - options.LeaguePanelAnchorTargetB;
        var distanceToTarget = Math.Sqrt((dr * dr) + (dg * dg) + (db * db));

        signal = new LeaguePanelAnchorSignal(
            sampleX,
            sampleY,
            r,
            g,
            b,
            luminance,
            spread,
            distanceToTarget);

        var isNearTargetPalette = distanceToTarget <= Math.Max(1, options.LeaguePanelAnchorTolerance);
        var isLightNeutral = luminance >= options.LeaguePanelAnchorMinLuminance &&
            spread <= options.LeaguePanelAnchorMaxChannelSpread;

        return isNearTargetPalette || isLightNeutral;
    }

    private static bool TryCaptureFromWindowClient(WindowCaptureContext context, OcrCaptureRegion absoluteRegion, out Bitmap bitmap)
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

        var sourceDc = NativeMethods.GetDC(context.WindowHandle);
        if (sourceDc == IntPtr.Zero)
        {
            return false;
        }

        bitmap = new Bitmap(absoluteRegion.Width, absoluteRegion.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        var destinationDc = graphics.GetHdc();
        try
        {
            const uint srccopy = 0x00CC0020;
            const uint captureBlt = 0x40000000;
            var success = NativeMethods.BitBlt(
                destinationDc,
                0,
                0,
                absoluteRegion.Width,
                absoluteRegion.Height,
                sourceDc,
                sourceX,
                sourceY,
                srccopy | captureBlt);

            if (!success)
            {
                bitmap.Dispose();
                bitmap = null!;
                return false;
            }

            return true;
        }
        finally
        {
            graphics.ReleaseHdc(destinationDc);
            NativeMethods.ReleaseDC(context.WindowHandle, sourceDc);
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

    private static Bitmap PreprocessForOcr(Bitmap source, OcrOptions options)
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

    private DebugCaptureContext? TryStartDebugCapture(Bitmap rawImage, Bitmap processedImage, OcrCaptureRegion region, string captureMethod)
    {
        var options = _options.CurrentValue;
        var intervalSeconds = Math.Max(1, options.DebugImageIntervalSeconds);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastDebugImageSavedAtUtc < TimeSpan.FromSeconds(intervalSeconds))
        {
            return null;
        }

        _lastDebugImageSavedAtUtc = now;

        var directory = ResolveDebugImageDirectory(options);

        try
        {
            Directory.CreateDirectory(directory);
            if (!_debugImageDirectoryLogged)
            {
                _debugImageDirectoryLogged = true;
                logger.LogInformation("OCR debug image output enabled. Directory: {Path}", Path.GetFullPath(directory));
            }

            var rawPath = Path.Combine(directory, "raw.png");
            var processedPath = Path.Combine(directory, "grayscale.png");

            SaveBitmapWithOverwrite(rawImage, rawPath);
            SaveBitmapWithOverwrite(processedImage, processedPath);

            logger.LogInformation(
                "Saved OCR debug images. Method={Method} Region=X={X} Y={Y} W={W} H={H} Raw={RawPath} Processed={ProcessedPath}. Row images overwrite as N.png and backup-guard probes overwrite as Nbg.png.",
                captureMethod,
                region.X,
                region.Y,
                region.Width,
                region.Height,
                rawPath,
                processedPath);

            return new DebugCaptureContext(directory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save OCR debug image.");
            return null;
        }
    }

    private string ResolveDebugImageDirectory(OcrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DebugImageDirectory))
        {
            return Path.Combine(AppContext.BaseDirectory, "ocr-debug");
        }

        return Path.IsPathRooted(options.DebugImageDirectory)
            ? options.DebugImageDirectory
            : Path.Combine(AppContext.BaseDirectory, options.DebugImageDirectory);
    }

    private void EnsureDebugImageDirectoryExists(OcrOptions options)
    {
        var directory = ResolveDebugImageDirectory(options);
        try
        {
            Directory.CreateDirectory(directory);
            if (!_debugImageDirectoryLogged)
            {
                _debugImageDirectoryLogged = true;
                logger.LogInformation("OCR debug image output enabled. Directory: {Path}", Path.GetFullPath(directory));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create OCR debug image directory: {Path}", directory);
        }
    }

    private string ExecuteTesseract(string imagePath, int psm, OcrOptions options, IReadOnlyList<string>? extraConfigs)
    {
        if (!IsExecutableAvailable(options.TesseractExePath))
        {
            throw new FileNotFoundException(
                $"Tesseract executable was not found. Configure OCR:TesseractExePath or install tesseract and add it to PATH. Current value: '{options.TesseractExePath}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.TesseractExePath,
            Arguments = BuildTesseractArguments(imagePath, options.Language, psm, extraConfigs),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start tesseract process.");

        if (!process.WaitForExit(TimeSpan.FromSeconds(Math.Max(1, options.CommandTimeoutSeconds))))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignored: best-effort timeout cleanup.
            }

            throw new TimeoutException("OCR command timed out.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Tesseract failed (exit code {process.ExitCode}): {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            logger.LogDebug("Tesseract stderr: {stderr}", stderr.Trim());
        }

        return stdout;
    }

    private static bool IsExecutableAvailable(string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            return File.Exists(executable);
        }

        var candidates = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { executable }
            : new[] { executable, $"{executable}.exe" };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathSegments = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in pathSegments)
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(segment.Trim(), candidate);
                if (File.Exists(fullPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractLikelyItemNames(string rawText)
    {
        var lines = rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeOcrLine)
            .Where(line => line.Length >= 3)
            .Where(line => line.Any(char.IsLetter))
            .ToArray();

        return lines;
    }

    private static string NormalizeOcrLine(string line)
    {
        var normalized = line.Replace('’', '\'').Replace('`', '\'');
        normalized = NonNameChars.Replace(normalized, " ");
        normalized = MultiWhitespace.Replace(normalized, " ").Trim();

        var parsed = PricingTextRules.ParseDetectedItem(normalized);
        if (!string.IsNullOrWhiteSpace(parsed.Name))
        {
            normalized = $"{parsed.Quantity}x {parsed.Name}";
        }

        return normalized.Trim(' ', '-', '\'', ',');
    }

    private static string BuildTesseractArguments(string imagePath, string language, int psm, IReadOnlyList<string>? extraConfigs)
    {
        var args = new StringBuilder();
        args.Append('"').Append(imagePath).Append('"');
        args.Append(" stdout");
        args.Append(" -l ").Append(language);
        args.Append(" --oem 1");
        args.Append(" --psm ").Append(psm);
        args.Append(" -c preserve_interword_spaces=1");
        args.Append(" -c tessedit_do_invert=0");
        args.Append(" -c load_system_dawg=0");
        args.Append(" -c load_freq_dawg=0");
        if (extraConfigs is not null)
        {
            foreach (var config in extraConfigs)
            {
                if (string.IsNullOrWhiteSpace(config))
                {
                    continue;
                }

                args.Append(" -c ").Append(config.Trim());
            }
        }

        return args.ToString();
    }

    private string TryRefineAmbiguousQuantityPrefix(string rowText, Bitmap upscaledRow, OcrOptions options)
    {
        if (string.IsNullOrWhiteSpace(rowText))
        {
            return string.Empty;
        }

        var prefixMatch = QuantityPrefixToken.Match(rowText.Trim());
        if (!prefixMatch.Success)
        {
            return rowText;
        }

        var rawToken = prefixMatch.Groups["quantity"].Value;
        if (int.TryParse(rawToken, out var parsedNumeric) && parsedNumeric > 0)
        {
            return rowText;
        }

        if (!IsAmbiguousQuantityToken(rawToken))
        {
            return rowText;
        }

        using var quantityProbe = BuildQuantityProbeBitmap(upscaledRow);
        using var borderedProbe = AddWhiteBorder(quantityProbe, 2);
        var quantityOcr = ExecuteTesseractForBitmap(
            borderedProbe,
            8,
            options,
            ["tessedit_char_whitelist=0123456789xX", "classify_bln_numeric_mode=1"]);
        if (_appOptions.CurrentValue.EnableDebugLogging)
        {
            var normalizedProbeText = MultiWhitespace.Replace(quantityOcr, " ").Trim();
            logger.LogInformation(
                "OCR backup quantity refine check. PrefixToken='{PrefixToken}' RowText='{RowText}' ProbeText='{ProbeText}'.",
                rawToken,
                rowText.Trim(),
                normalizedProbeText);
        }

        var quantityMatch = LeadingQuantityDigits.Match(quantityOcr.Trim());
        if (quantityMatch.Success &&
            int.TryParse(quantityMatch.Groups["quantity"].Value, out var quantity) &&
            quantity > 0)
        {
            var suffix = rowText[prefixMatch.Length..].TrimStart();
            if (_appOptions.CurrentValue.EnableDebugLogging)
            {
                logger.LogInformation(
                    "OCR backup quantity refine applied. ParsedQuantity={Quantity} OriginalPrefix='{OriginalPrefix}'.",
                    quantity,
                    rawToken);
            }

            return string.IsNullOrWhiteSpace(suffix)
                ? $"{quantity}x"
                : $"{quantity}x {suffix}";
        }

        if (_appOptions.CurrentValue.EnableDebugLogging)
        {
            logger.LogInformation(
                "OCR backup quantity refine skipped. Probe text did not parse into a leading number for prefix token '{PrefixToken}'.",
                rawToken);
        }

        return rowText;
    }

    private static bool IsAmbiguousQuantityToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var normalized = token.Trim().ToUpperInvariant();
        return normalized is "A" or "I" or "L" or "T" or "|" or "O" or "0" or "B" or "S";
    }

    private static Bitmap BuildQuantityProbeBitmap(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;

            var length = Math.Abs(data.Stride) * height;
            var bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var luminance = bytes[rowOffset + (x * 3)];
                    if (luminance > 128)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return (Bitmap)source.Clone();
            }

            var textWidth = maxX - minX + 1;
            var probeWidth = Math.Clamp((textWidth / 3) + 8, 20, Math.Min(width - minX, 96));
            var probeX = Math.Clamp(minX - 2, 0, Math.Max(0, width - probeWidth));
            var probeY = Math.Clamp(minY - 2, 0, Math.Max(0, height - (maxY - minY + 5)));
            var probeHeight = Math.Clamp((maxY - minY + 5), 10, height - probeY);
            var probeRect = new Rectangle(probeX, probeY, probeWidth, probeHeight);
            return source.Clone(probeRect, PixelFormat.Format24bppRgb);
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static Bitmap AddWhiteBorder(Bitmap source, int borderPx)
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

    private sealed record AdaptiveShiftComputationResult(
        HashSet<int> ShiftStartRows,
        HashSet<int> SuppressedByQuantityPrefixRows,
        bool DisabledByQuantityPrefixGuard,
        IReadOnlyList<QuantityPrefixGuardRowObservation> GuardObservations);

    private sealed record QuantityPrefixGuardRowObservation(
        int RowNumber,
        int RowY,
        int ProbeWidthPx,
        bool DarkSignal,
        bool PrefixProbeRun,
        bool StartsWithPrefix,
        string ProbeText,
        string Action,
        bool RawRetryUsed,
        string RawRetryPrimaryText,
        string RawRetryText,
        bool RawRetryStartsWithPrefix);

    private sealed record QuantityPrefixProbeResult(
        bool HasPrefix,
        string ProbeText,
        int ProbeWidthPx,
        bool RawRetryUsed,
        string RawRetryPrimaryText,
        string RawRetryText,
        bool RawRetryStartsWithPrefix);

    private AdaptiveShiftComputationResult ComputeAdaptiveShiftStartRows(
        Bitmap bitmap,
        DebugCaptureContext? debugContext,
        OcrOptions options,
        int rowTextHeight,
        int rowGapHeight,
        int rowLateOffsetStartRow,
        int rowLateOffsetStepRows,
        int rowLateOffsetStepPx,
        int probeWidthPx,
        int stepPx,
        int maxPx,
        int probeMinDarkPixels)
    {
        if (!options.UseFixedRowGeometry || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return new AdaptiveShiftComputationResult([], [], false, []);
        }

        var shifts = new HashSet<int>();
        var suppressedByQuantityPrefix = new HashSet<int>();
        var guardObservations = new List<QuantityPrefixGuardRowObservation>(capacity: Math.Max(4, options.OcrRowCount));
        var rgb = ReadRgbPixels(bitmap);
        var cumulativeShift = 0;
        var rowCount = Math.Max(1, options.OcrRowCount);
        var probeWidth = Math.Min(Math.Max(1, probeWidthPx), bitmap.Width);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            var y = options.RowStartOffsetY + (rowIndex * (rowTextHeight + rowGapHeight));

            if (rowLateOffsetStepPx > 0 && rowNumber >= rowLateOffsetStartRow)
            {
                var stepIndex = ((rowNumber - rowLateOffsetStartRow) / Math.Max(1, rowLateOffsetStepRows)) + 1;
                y += stepIndex * rowLateOffsetStepPx;
            }

            y += cumulativeShift;
            if (y >= bitmap.Height)
            {
                guardObservations.Add(new QuantityPrefixGuardRowObservation(
                    rowNumber,
                    y,
                    0,
                    false,
                    false,
                    false,
                    string.Empty,
                    "row-out-of-bounds-stop",
                    false,
                    string.Empty,
                    string.Empty,
                    false));
                break;
            }

            var probeHeight = Math.Min(Math.Max(1, rowTextHeight), bitmap.Height - y);
            if (probeHeight <= 0)
            {
                guardObservations.Add(new QuantityPrefixGuardRowObservation(
                    rowNumber,
                    y,
                    0,
                    false,
                    false,
                    false,
                    string.Empty,
                    "invalid-probe-height-stop",
                    false,
                    string.Empty,
                    string.Empty,
                    false));
                break;
            }

            var hasDarkSignal = false;
            if (cumulativeShift < maxPx)
            {
                hasDarkSignal = HasDarkTextSignal(rgb, bitmap.Width, y, probeWidth, probeHeight, options, probeMinDarkPixels);
            }

            if (!hasDarkSignal)
            {
                guardObservations.Add(new QuantityPrefixGuardRowObservation(
                    rowNumber,
                    y,
                    0,
                    false,
                    false,
                    false,
                    string.Empty,
                    cumulativeShift >= maxPx ? "max-shift-reached" : "no-dark-signal",
                    false,
                    string.Empty,
                    string.Empty,
                    false));
                continue;
            }

            var prefixProbe = ProbeQuantityPrefix(bitmap, debugContext, rowNumber, y, probeWidth, probeHeight, options);
            var normalizedProbeText = MultiWhitespace.Replace(prefixProbe.ProbeText ?? string.Empty, " ").Trim();
            var compactProbeText = normalizedProbeText.Length > 32
                ? normalizedProbeText[..32]
                : normalizedProbeText;

            if (prefixProbe.HasPrefix)
            {
                suppressedByQuantityPrefix.Add(rowNumber);
                guardObservations.Add(new QuantityPrefixGuardRowObservation(
                    rowNumber,
                    y,
                    prefixProbe.ProbeWidthPx,
                    true,
                    true,
                    true,
                    compactProbeText,
                    "suppressed-by-prefix",
                    prefixProbe.RawRetryUsed,
                    prefixProbe.RawRetryPrimaryText,
                    prefixProbe.RawRetryText,
                    prefixProbe.RawRetryStartsWithPrefix));
                continue;
            }

            shifts.Add(rowNumber);
            cumulativeShift = Math.Min(maxPx, cumulativeShift + stepPx);
            guardObservations.Add(new QuantityPrefixGuardRowObservation(
                rowNumber,
                y,
                prefixProbe.ProbeWidthPx,
                true,
                true,
                false,
                compactProbeText,
                "shift-applied",
                prefixProbe.RawRetryUsed,
                prefixProbe.RawRetryPrimaryText,
                prefixProbe.RawRetryText,
                prefixProbe.RawRetryStartsWithPrefix));
        }

        var disabledByQuantityPrefixGuard = suppressedByQuantityPrefix.Count > 0 && shifts.Count == 0;

        return new AdaptiveShiftComputationResult(
            shifts,
            suppressedByQuantityPrefix,
            disabledByQuantityPrefixGuard,
            guardObservations);
    }

    private QuantityPrefixProbeResult ProbeQuantityPrefix(
        Bitmap bitmap,
        DebugCaptureContext? debugContext,
        int rowNumber,
        int y,
        int probeWidth,
        int probeHeight,
        OcrOptions options)
    {
        try
        {
            var x = 0;
            var width = Math.Clamp(probeWidth, 1, bitmap.Width - x);
            var height = Math.Clamp(probeHeight, 1, bitmap.Height - y);
            var probeRect = new Rectangle(x, y, width, height);

            using var rowProbe = bitmap.Clone(probeRect, PixelFormat.Format24bppRgb);
            if (debugContext is not null)
            {
                // Save the pre-upscale guard probe so Nbg.png width matches profile probe width.
                TrySaveBackupGuardDebugImage(debugContext, rowProbe, rowNumber);
            }

            using var cleanedProbe = PrepareRowBitmapForOcr(rowProbe, options);
            using var upscaledProbe = UpscaleForOcr(cleanedProbe, options.RowUpscaleFactor);
            using var borderedProbe = AddWhiteBorder(upscaledProbe, 2);
            var probeText = ExecuteTesseractForBitmap(
                borderedProbe,
                8,
                options,
                ["tessedit_char_whitelist=0123456789xX", "classify_bln_numeric_mode=1"]);

            var hasPrefix = MatchesQuantityPrefixGuard(probeText);
            var rawRetryUsed = false;
            var rawRetryPrimaryText = string.Empty;
            var rawRetryText = string.Empty;
            var rawRetryStartsWithPrefix = false;
            if (!hasPrefix)
            {
                var compactPrimary = MultiWhitespace.Replace(probeText, " ").Trim();
                if (compactPrimary.Length <= 2)
                {
                    using var upscaledRawProbe = UpscaleForOcr(rowProbe, options.RowUpscaleFactor);
                    using var borderedRawProbe = AddWhiteBorder(upscaledRawProbe, 2);
                    var rawProbeText = ExecuteTesseractForBitmap(
                        borderedRawProbe,
                        7,
                        options,
                        ["tessedit_char_whitelist=0123456789xX", "classify_bln_numeric_mode=1"]);

                    var rawHasPrefix = MatchesQuantityPrefixGuard(rawProbeText);
                    rawRetryUsed = true;
                    rawRetryPrimaryText = compactPrimary;
                    rawRetryText = MultiWhitespace.Replace(rawProbeText, " ").Trim();
                    rawRetryStartsWithPrefix = rawHasPrefix;
                    if (rawHasPrefix || string.IsNullOrWhiteSpace(compactPrimary))
                    {
                        probeText = rawProbeText;
                        hasPrefix = rawHasPrefix;
                    }
                }
            }

            return new QuantityPrefixProbeResult(
                hasPrefix,
                probeText,
                width,
                rawRetryUsed,
                rawRetryPrimaryText,
                rawRetryText,
                rawRetryStartsWithPrefix);
        }
        catch (Exception ex)
        {
            if (_appOptions.CurrentValue.EnableDebugLogging)
            {
                logger.LogDebug(ex, "Quantity-prefix guard probe OCR failed; adaptive fallback will use dark-signal heuristic.");
            }

            return new QuantityPrefixProbeResult(false, string.Empty, Math.Clamp(probeWidth, 1, bitmap.Width), false, string.Empty, string.Empty, false);
        }
    }

    private static bool HasDarkTextSignal(
        byte[] rgbPixels,
        int bitmapWidth,
        int y,
        int probeWidth,
        int probeHeight,
        OcrOptions options,
        int probeMinDarkPixels)
    {
        var darkPixels = 0;

        for (var py = 0; py < probeHeight; py++)
        {
            var rowOffset = (y + py) * bitmapWidth;
            for (var px = 0; px < probeWidth; px++)
            {
                var pixelIndex = rowOffset + px;
                if (IsLikelyTextColor(rgbPixels, pixelIndex, options))
                {
                    darkPixels++;
                    if (darkPixels >= Math.Max(1, probeMinDarkPixels))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool MatchesQuantityPrefixGuard(string probeText)
    {
        var trimmed = probeText?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed[0] is 'x' or 'X')
        {
            return true;
        }

        // OCR sometimes drops the trailing x in narrow guard probes; accept only clean leading 1..10.
        var numberMatch = LeadingGuardNumberToken.Match(trimmed);
        if (!numberMatch.Success)
        {
            return false;
        }

        if (!int.TryParse(numberMatch.Groups["num"].Value, out var number))
        {
            return false;
        }

        return number is >= 1 and <= 10;
    }

    private void LogAdaptiveShiftDecision(
        AdaptiveShiftComputationResult decision,
        (int ProbeWidthPx, int StepPx, int MaxPx, int ProbeMinDarkPixels) adaptiveParams)
    {
        if (!_appOptions.CurrentValue.EnableDebugLogging)
        {
            return;
        }

        var activeRows = decision.ShiftStartRows.Count == 0
            ? "<none>"
            : string.Join(",", decision.ShiftStartRows.OrderBy(static row => row));
        var suppressedRows = decision.SuppressedByQuantityPrefixRows.Count == 0
            ? "<none>"
            : string.Join(",", decision.SuppressedByQuantityPrefixRows.OrderBy(static row => row));
        var guardSummary = decision.GuardObservations.Count == 0
            ? "<none>"
            : string.Join(
                Environment.NewLine + "  - ",
                decision.GuardObservations.Select(
                    static observation =>
                        $"r{observation.RowNumber}: y={observation.RowY}, dark={observation.DarkSignal}, probeRun={observation.PrefixProbeRun}, prefix={observation.StartsWithPrefix}, w={observation.ProbeWidthPx}, text='{observation.ProbeText}', action={observation.Action}, rawRetryUsed={observation.RawRetryUsed}, rawPrimary='{observation.RawRetryPrimaryText}', rawText='{observation.RawRetryText}', rawPrefix={observation.RawRetryStartsWithPrefix}"));

        if (decision.GuardObservations.Count > 0)
        {
            guardSummary = "  - " + guardSummary;
        }

        if (decision.ShiftStartRows.Count > 0)
        {
            logger.LogInformation(
                "Adaptive row-bump fallback ENABLED. Shift rows={ActiveRows}; " + Environment.NewLine +
                "step={StepPx}px; probeWidth={ProbeWidth}px; maxShift={MaxPx}px; darkPixelThreshold={MinDark}. " + Environment.NewLine +
                "Quantity-prefix guard suppressed rows={SuppressedRows}. " + Environment.NewLine +
                "Quantity-prefix guard summary=" + Environment.NewLine +
                "{GuardSummary}.",
                activeRows,
                adaptiveParams.StepPx,
                adaptiveParams.ProbeWidthPx,
                adaptiveParams.MaxPx,
                adaptiveParams.ProbeMinDarkPixels,
                suppressedRows,
                guardSummary);
            return;
        }

        logger.LogInformation(
            "Adaptive row-bump fallback DISABLED. No shift rows active. " + Environment.NewLine +
            "Quantity-prefix guard suppressed rows={SuppressedRows}; guardDisabledFallback={GuardDisabled}. " + Environment.NewLine +
            "Quantity-prefix guard summary=" + Environment.NewLine +
            "{GuardSummary}.",
            suppressedRows,
            decision.DisabledByQuantityPrefixGuard,
            guardSummary);
    }

    private static (int ProbeWidthPx, int StepPx, int MaxPx, int ProbeMinDarkPixels) GetAdaptiveParams(OcrResolutionProfile? profile)
    {
        var probeWidthPx = profile?.AdaptiveShiftProbeWidthPx ?? DefaultAdaptiveShiftProbeWidthPx;
        var stepPx = profile?.AdaptiveShiftStepPx ?? DefaultAdaptiveShiftStepPx;
        var maxPx = profile?.AdaptiveShiftMaxPx ?? DefaultAdaptiveShiftMaxPx;
        var probeMinDarkPixels = profile?.AdaptiveShiftProbeMinDarkPixels ?? DefaultAdaptiveShiftProbeMinDarkPixels;

        return (Math.Max(1, probeWidthPx), Math.Max(1, stepPx), Math.Max(stepPx, maxPx), Math.Max(1, probeMinDarkPixels));
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
                var index = offset + x;
                if (source[index] != 0)
                {
                    continue;
                }

                var neighbors = CountBlackNeighbors(source, width, x, y);
                if (neighbors <= 1)
                {
                    pixels[index] = 255;
                }
            }
        }
    }

    private static int CountBlackNeighbors(byte[] pixels, int width, int x, int y)
    {
        var index = (y * width) + x;
        var neighbors = 0;

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var neighborIndex = index + (dy * width) + dx;
                if (pixels[neighborIndex] == 0)
                {
                    neighbors++;
                }
            }
        }

        return neighbors;
    }

    private static Bitmap UpscaleForOcr(Bitmap source, int scaleFactor)
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

    private static Bitmap PrepareRowBitmapForOcr(Bitmap source, OcrOptions options)
    {
        var cleaned = (Bitmap)source.Clone();

        var rect = new Rectangle(0, 0, cleaned.Width, cleaned.Height);
        var data = cleaned.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var width = data.Width;
            var height = data.Height;
            var stride = data.Stride;

            var length = Math.Abs(stride) * height;
            var bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);

            var binary = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;
                var binaryOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    binary[binaryOffset + x] = bytes[rowOffset + (x * 3)] < 128 ? (byte)0 : (byte)255;
                }
            }

            ApplyRowNoiseMask(binary, width, height, options);
            RemoveSmallBlackComponents(binary, width, height, Math.Max(0, options.RowSpeckleMaxArea));

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;
                var binaryOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var index = rowOffset + (x * 3);
                    var value = binary[binaryOffset + x];
                    bytes[index] = value;
                    bytes[index + 1] = value;
                    bytes[index + 2] = value;
                }
            }

            Marshal.Copy(bytes, 0, data.Scan0, length);
            return cleaned;
        }
        finally
        {
            cleaned.UnlockBits(data);
        }
    }

    private static void ApplyRowNoiseMask(byte[] binary, int width, int height, OcrOptions options)
    {
        var top = Math.Clamp(options.RowNoiseMaskTopPx, 0, height);
        var bottom = Math.Clamp(options.RowNoiseMaskBottomPx, 0, height);

        for (var y = 0; y < top; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                binary[row + x] = 255;
            }
        }

        for (var y = Math.Max(0, height - bottom); y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                binary[row + x] = 255;
            }
        }
    }

    private static void RemoveSmallBlackComponents(byte[] binary, int width, int height, int maxArea)
    {
        if (maxArea <= 0 || width <= 2 || height <= 2)
        {
            return;
        }

        var visited = new bool[width * height];
        var queue = new int[width * height];
        var component = new int[width * height];

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var start = (y * width) + x;
                if (visited[start] || binary[start] != 0)
                {
                    continue;
                }

                var head = 0;
                var tail = 0;
                var count = 0;
                var minX = x;
                var maxX = x;
                var maxY = y;
                queue[tail++] = start;
                visited[start] = true;

                while (head < tail)
                {
                    var current = queue[head++];
                    component[count++] = current;

                    var cy = current / width;
                    var cx = current - (cy * width);
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    maxY = Math.Max(maxY, cy);

                    var minNeighborY = Math.Max(0, cy - 1);
                    var maxNeighborY = Math.Min(height - 1, cy + 1);
                    var minNeighborX = Math.Max(0, cx - 1);
                    var maxNeighborX = Math.Min(width - 1, cx + 1);

                    for (var ny = minNeighborY; ny <= maxNeighborY; ny++)
                    {
                        var row = ny * width;
                        for (var nx = minNeighborX; nx <= maxNeighborX; nx++)
                        {
                            if (nx == cx && ny == cy)
                            {
                                continue;
                            }

                            var neighbor = row + nx;
                            if (visited[neighbor] || binary[neighbor] != 0)
                            {
                                continue;
                            }

                            visited[neighbor] = true;
                            queue[tail++] = neighbor;
                        }
                    }
                }

                if (count > maxArea)
                {
                    continue;
                }

                if (HasBlackSupportBelow(binary, width, height, minX, maxX, maxY))
                {
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    binary[component[i]] = 255;
                }
            }
        }
    }

    private static bool HasBlackSupportBelow(byte[] binary, int width, int height, int minX, int maxX, int maxY)
    {
        const int supportDepth = 8;
        var scanStartY = maxY + 1;
        if (scanStartY >= height)
        {
            return false;
        }

        var scanEndY = Math.Min(height - 1, maxY + supportDepth);
        var scanMinX = Math.Max(0, minX - 1);
        var scanMaxX = Math.Min(width - 1, maxX + 1);

        for (var y = scanStartY; y <= scanEndY; y++)
        {
            var row = y * width;
            for (var x = scanMinX; x <= scanMaxX; x++)
            {
                if (binary[row + x] == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private OcrCaptureRegion ResolveCaptureRegion()
    {
        var region = windowResolutionProvider.CurrentCaptureRegion;
        if (region is null)
        {
            throw new InvalidOperationException("No OCR capture region is available. Add/update the current resolution in OcrResolutionProfiles.");
        }

        return region;
    }

    private static void ValidateRegion(OcrCaptureRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new InvalidOperationException("OCR capture region must have positive width and height.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore temp-file cleanup failures.
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(
            IntPtr hdcDest,
            int nXDest,
            int nYDest,
            int nWidth,
            int nHeight,
            IntPtr hdcSrc,
            int nXSrc,
            int nYSrc,
            uint dwRop);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        public static bool AreWindowFamilyRelated(IntPtr candidateWindowHandle, IntPtr foregroundWindowHandle)
        {
            if (candidateWindowHandle == IntPtr.Zero || foregroundWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            if (candidateWindowHandle == foregroundWindowHandle)
            {
                return true;
            }

            if (IsChild(candidateWindowHandle, foregroundWindowHandle) || IsChild(foregroundWindowHandle, candidateWindowHandle))
            {
                return true;
            }

            const uint gaRoot = 2;
            var candidateRoot = GetAncestor(candidateWindowHandle, gaRoot);
            var foregroundRoot = GetAncestor(foregroundWindowHandle, gaRoot);

            return candidateRoot != IntPtr.Zero && candidateRoot == foregroundRoot;
        }
    }
}
