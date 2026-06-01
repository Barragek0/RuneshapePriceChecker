using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.OCR;

public sealed class OcrLeagueWindowReader(
    IOptionsMonitor<OcrOptions> options,
    IOptionsMonitor<AppOptions> appOptions,
    IPoe2WindowResolutionProvider windowResolutionProvider,
    ILogger<OcrLeagueWindowReader> logger) : ILeagueWindowReader
{
    private sealed record DebugCaptureContext(string DirectoryPath, string Prefix);

    private readonly IOptionsMonitor<OcrOptions> _options = options;
    private readonly IOptionsMonitor<AppOptions> _appOptions = appOptions;
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NonNameChars = new("[^-A-Za-z0-9'’ ]+", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantity = new("^(?<quantity>\\d+|[IiLl|])\\s*[xX]\\s*(?<name>.+)$", RegexOptions.Compiled);
    private bool _tesseractUnavailable;
    private bool _windowCaptureUnavailableLogged;
    private bool _waitingForWindowContextLogged;
    private bool _waitingForForegroundWindowLogged;
    private bool _debugImageDirectoryLogged;
    private bool _tesseractExecutionConfirmedLogged;
    private DateTimeOffset _lastDebugImageSavedAtUtc = DateTimeOffset.MinValue;

    public LeagueWindowSnapshot ReadSnapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;

        if (_tesseractUnavailable)
        {
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt);
        }

        try
        {
            var rawText = CaptureAndRecognize();
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
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OCR capture/recognition failed.");
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt);
        }
    }

    private string CaptureAndRecognize()
    {
        var options = _options.CurrentValue;
        if (!windowResolutionProvider.IsPoe2WindowForeground)
        {
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
        using var ocrBitmap = options.EnableImagePreprocessing
            ? PreprocessForOcr(capturedBitmap, options)
            : (Bitmap)capturedBitmap.Clone();

        var debugContext = options.SaveDebugImages
            ? TryStartDebugCapture(capturedBitmap, ocrBitmap, region, captureMethod)
            : null;

        return ExecuteTesseractByRows(ocrBitmap, debugContext, options);
    }

    private string ExecuteTesseractByRows(Bitmap bitmap, DebugCaptureContext? debugContext, OcrOptions options)
    {
        var profile = windowResolutionProvider.CurrentResolutionProfile;
        var rowTextHeight = profile?.RowTextHeight ?? options.RowTextHeight;
        var rowGapHeight = profile?.RowGapHeight ?? options.RowGapHeight;

        var rowRects = OcrRowLayout.BuildRowRectangles(
            bitmap.Width,
            bitmap.Height,
            options.OcrRowCount,
            options.UseFixedRowGeometry,
            options.RowStartOffsetY,
            rowTextHeight,
            rowGapHeight);
        if (rowRects.Count == 1)
        {
            using var cleaned = PrepareRowBitmapForOcr(bitmap, options);
            using var upscaled = UpscaleForOcr(cleaned, options.RowUpscaleFactor);
            if (debugContext is not null)
            {
                TrySaveRowDebugImage(debugContext, upscaled, 0, rowRects[0]);
            }

            return ExecuteTesseractForBitmap(upscaled, options.PageSegmentationMode, options);
        }

        var rowTexts = new List<string>(rowRects.Count);

        for (var rowIndex = 0; rowIndex < rowRects.Count; rowIndex++)
        {
            var rowRect = rowRects[rowIndex];
            using var rowBitmap = bitmap.Clone(rowRect, PixelFormat.Format24bppRgb);
            using var cleanedRow = PrepareRowBitmapForOcr(rowBitmap, options);
            using var upscaledRow = UpscaleForOcr(cleanedRow, options.RowUpscaleFactor);
            if (debugContext is not null)
            {
                TrySaveRowDebugImage(debugContext, upscaledRow, rowIndex, rowRect);
            }

            var rowText = ExecuteTesseractForBitmap(upscaledRow, options.RowPageSegmentationMode, options).Trim();
            if (!string.IsNullOrWhiteSpace(rowText))
            {
                rowTexts.Add(rowText);
            }
        }

        return string.Join(Environment.NewLine, rowTexts);
    }

    private void TrySaveRowDebugImage(DebugCaptureContext context, Bitmap rowBitmap, int rowIndex, Rectangle rowRect)
    {
        try
        {
            var rowPath = Path.Combine(
                context.DirectoryPath,
                $"{context.Prefix}-row-{rowIndex + 1:00}-y{rowRect.Y}-h{rowRect.Height}.png");
            rowBitmap.Save(rowPath, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save OCR row debug image {RowIndex}.", rowIndex + 1);
        }
    }

    private string ExecuteTesseractForBitmap(Bitmap bitmap, int psm, OcrOptions options)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"runeshapepricechecker-ocr-{Guid.NewGuid():N}.png");
        try
        {
            bitmap.Save(filePath, ImageFormat.Png);
            return ExecuteTesseract(filePath, psm, options);
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

            var filePrefix = $"ocr-debug-{now:yyyyMMdd-HHmmss-fff}";
            var rawPath = Path.Combine(directory, $"{filePrefix}-raw-{captureMethod}.png");
            var processedPath = Path.Combine(directory, $"{filePrefix}-proc.png");

            rawImage.Save(rawPath, ImageFormat.Png);
            processedImage.Save(processedPath, ImageFormat.Png);

            logger.LogInformation(
                "Saved OCR debug images. Method={Method} Region=X={X} Y={Y} W={W} H={H} Raw={RawPath} Processed={ProcessedPath}. Row images will be saved with prefix {Prefix}.",
                captureMethod,
                region.X,
                region.Y,
                region.Width,
                region.Height,
                rawPath,
                processedPath,
                filePrefix);

            return new DebugCaptureContext(directory, filePrefix);
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
            return Path.Combine(Path.GetTempPath(), "RuneshapePriceChecker", "ocr-debug");
        }

        return Path.IsPathRooted(options.DebugImageDirectory)
            ? options.DebugImageDirectory
            : Path.Combine(AppContext.BaseDirectory, options.DebugImageDirectory);
    }

    private string ExecuteTesseract(string imagePath, int psm, OcrOptions options)
    {
        if (!IsExecutableAvailable(options.TesseractExePath))
        {
            throw new FileNotFoundException(
                $"Tesseract executable was not found. Configure OCR:TesseractExePath or install tesseract and add it to PATH. Current value: '{options.TesseractExePath}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.TesseractExePath,
            Arguments = BuildTesseractArguments(imagePath, options.Language, psm),
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

        var quantityMatch = LeadingQuantity.Match(normalized);
        if (quantityMatch.Success)
        {
            var rawQuantity = quantityMatch.Groups["quantity"].Value;
            var name = quantityMatch.Groups["name"].Value.Trim();
            var quantity = rawQuantity switch
            {
                "i" or "I" or "l" or "L" or "|" => 1,
                _ when int.TryParse(rawQuantity, out var parsed) && parsed > 0 => parsed,
                _ => 1
            };

            if (!string.IsNullOrWhiteSpace(name))
            {
                normalized = $"{quantity}x {name}";
            }
        }

        return normalized.Trim(' ', '-', '\'', ',');
    }

    private static string BuildTesseractArguments(string imagePath, string language, int psm)
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
        return args.ToString();
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
    }
}
