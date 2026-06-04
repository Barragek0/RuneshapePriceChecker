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
    ILogger<OcrLeagueWindowReader> logger) : ILeagueWindowReader
{
    private sealed record DebugCaptureContext(string DirectoryPath);

    private readonly IOptionsMonitor<OcrOptions> _options = options;
    private readonly IOptionsMonitor<AppOptions> _appOptions = appOptions;
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NonNameChars = new("[^-A-Za-z0-9'� ]+", RegexOptions.Compiled);
    private bool _tesseractUnavailable;
    private bool _windowCaptureUnavailableLogged;
    private bool _waitingForWindowContextLogged;
    private bool _waitingForForegroundWindowLogged;
    private bool _waitingForLeagueListingPanelLogged;
    private int[] _lastRowYPositions = [];
    private bool _lastInterfaceDetected = true;
    private bool _debugImageDirectoryLogged;
    private bool _tesseractExecutionConfirmedLogged;
    private string _lastCaptureMethod = string.Empty;
    private DateTimeOffset _lastDebugImageSavedAtUtc = DateTimeOffset.MinValue;

    public LeagueWindowSnapshot ReadSnapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;

        if (_tesseractUnavailable)
        {
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }

        try
        {
            var rawText = CaptureAndRecognize(out var attemptedRecognition);
            if (!attemptedRecognition)
            {
                return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt, InterfaceDetected: _lastInterfaceDetected);
            }

            if (_appOptions.CurrentValue.DebugLogging && !_tesseractExecutionConfirmedLogged)
            {
                _tesseractExecutionConfirmedLogged = true;
                logger.LogDebug("OCR engine confirmed: tesseract executed successfully.");
            }

            var lines = ExtractLikelyItemNames(rawText);
            if (_appOptions.CurrentValue.DebugLogging)
            {
                var yPositions = _lastRowYPositions;
                var items = lines.Count == 0
                    ? "<none>"
                    : string.Join(" | ", lines.Select((line, i) =>
                        i < yPositions.Length
                            ? $"{line} @Y={yPositions[i]}"
                            : line));
                logger.LogDebug("OCR detected {Count} items: {Items}", lines.Count, items);
            }

            return new LeagueWindowSnapshot(lines, capturedAt, _lastRowYPositions, InterfaceDetected: true);
        }
        catch (FileNotFoundException ex)
        {
            _tesseractUnavailable = true;
            logger.LogWarning(
                "OCR disabled: {Reason} Install Tesseract from https://github.com/UB-Mannheim/tesseract/wiki then restart RuneshapePriceChecker.",
                ex.Message);
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _tesseractUnavailable = true;
            logger.LogWarning(
                "OCR disabled: Tesseract not found. Install from https://github.com/UB-Mannheim/tesseract/wiki then restart RuneshapePriceChecker.");
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OCR capture/recognition failed.");
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
    }

    private string CaptureAndRecognize(out bool attemptedRecognition)
    {
        attemptedRecognition = false;
        var options = _options.CurrentValue;
        if (options.SaveDebugImages)
        {
            EnsureDebugImageDirectoryExists(options);
        }

        if (!windowResolutionProvider.IsPoe2WindowForeground || !IsPoe2ForegroundNow())
        {
            _lastInterfaceDetected = false;
            if (_appOptions.CurrentValue.DebugLogging && !_waitingForForegroundWindowLogged)
            {
                _waitingForForegroundWindowLogged = true;
                logger.LogDebug("OCR paused: waiting for Path of Exile 2 to be the active foreground window.");
            }

            return string.Empty;
        }

        _waitingForForegroundWindowLogged = false;

        if (options.UseWindowClientCapture && windowResolutionProvider.CurrentWindowCaptureContext is null)
        {
            if (_appOptions.CurrentValue.DebugLogging && !_waitingForWindowContextLogged)
            {
                _waitingForWindowContextLogged = true;
                logger.LogDebug("OCR warm-up: waiting for PoE2 window capture context before first scan.");
            }

            return string.Empty;
        }

        _waitingForWindowContextLogged = false;

        var region = ResolveCaptureRegion();
        ValidateRegion(region);

        using var capturedBitmap = CaptureBitmap(region, out var captureMethod, options);
        if (_appOptions.CurrentValue.DebugLogging &&
            !string.Equals(_lastCaptureMethod, captureMethod, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("OCR capture source active: {CaptureMethod}.", captureMethod);
        }

        _lastCaptureMethod = captureMethod;

        if (!IsLeaguePanelAnchorColorMatch(capturedBitmap, options, out var anchorSignal))
        {
            if (_appOptions.CurrentValue.DebugLogging && !_waitingForLeagueListingPanelLogged)
            {
                _waitingForLeagueListingPanelLogged = true;
                logger.LogDebug(
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

            _lastInterfaceDetected = false;
            return string.Empty;
        }

        _waitingForLeagueListingPanelLogged = false;
        _lastInterfaceDetected = true;

        var debugContext = options.SaveDebugImages
            ? TryStartDebugCapture(capturedBitmap, region, captureMethod)
            : null;

        attemptedRecognition = true;
        return ExecuteTesseractAutoLayout(capturedBitmap, debugContext, options);
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

    private static void SaveBitmapWithOverwrite(Bitmap bitmap, string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private string ExecuteTesseractAutoLayout(Bitmap bitmap, DebugCaptureContext? debugContext, OcrOptions options)
    {
        using var masked = KeepBlackAndNeighbors(bitmap);
        using var preprocessed = PreprocessForOcr(masked, options);
        using var upscaled = UpscaleForOcr(preprocessed, options.RowUpscaleFactor);
        using var bordered = AddWhiteBorder(upscaled, 2);

        if (debugContext is not null)
        {
            SaveBitmapWithOverwrite(masked, Path.Combine(debugContext.DirectoryPath, "text-extract.png"));
            SaveBitmapWithOverwrite(preprocessed, Path.Combine(debugContext.DirectoryPath, "preprocessed.png"));
        }

        var (text, lineYs) = ExecuteTesseractWithTsv(bordered, 3, options, options.RowUpscaleFactor);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();

        _lastRowYPositions = lineYs.Length > 0
            ? lineYs
            : ComputeRowPositions(preprocessed, lines.Length);

        return string.Join(Environment.NewLine, lines);
    }

    private (string Text, int[] LineYPositions) ExecuteTesseractWithTsv(
        Bitmap bitmap, int psm, OcrOptions options, int upscaleFactor)
    {
        var baseName = Path.Combine(Path.GetTempPath(), $"rpc-tsv-{Guid.NewGuid():N}");
        var imagePath = baseName + ".png";
        var tsvPath = baseName + ".tsv";
        var txtPath = baseName + ".txt";
        try
        {
            bitmap.Save(imagePath, ImageFormat.Png);

            var args = $"\"{imagePath}\" \"{baseName}\" -l {options.Language} --oem 1 --psm {psm} -c preserve_interword_spaces=1 -c tessedit_create_tsv=1";

            var startInfo = new ProcessStartInfo
            {
                FileName = options.TesseractExePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start tesseract process.");

            if (!process.WaitForExit(TimeSpan.FromSeconds(Math.Max(1, options.CommandTimeoutSeconds))))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("OCR command timed out.");
            }

            var stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Tesseract failed (exit code {process.ExitCode}): {stderr}");

            var text = File.Exists(txtPath)
                ? File.ReadAllText(txtPath)
                : ExecuteTesseractForBitmap(bitmap, psm, options);

            var lineYs = new List<int>();
            if (File.Exists(tsvPath))
            {
                foreach (var tsvLine in File.ReadLines(tsvPath).Skip(1))
                {
                    var cols = tsvLine.Split('\t');
                    if (cols.Length < 12 || cols[0] != "4") continue;
                    if (int.TryParse(cols[7], out var top) && int.TryParse(cols[9], out var h))
                        lineYs.Add((top - 12) / upscaleFactor);
                }
            }

            return (text, lineYs.ToArray());
        }
        finally
        {
            TryDelete(imagePath);
            TryDelete(txtPath);
            TryDelete(tsvPath);
        }
    }

    private static readonly int[] _emptyRowPositions = [];

    private static int[] ComputeRowPositions(Bitmap binarized, int itemCount)
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

            if (textTop < 0) return _emptyRowPositions;

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

    private static Bitmap KeepBlackAndNeighbors(Bitmap source)
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

        var sampleX = ComputeAnchorX(bitmap, options);
        var sampleY = ComputeAnchorY(bitmap, options);
        var sampleRadius = ComputeAnchorRadius(bitmap, options);

        var minX = Math.Max(0, sampleX - sampleRadius);
        var maxX = Math.Min(bitmap.Width - 1, sampleX + sampleRadius);
        var minY = Math.Max(0, sampleY - sampleRadius);
        var maxY = Math.Min(bitmap.Height - 1, sampleY + sampleRadius);

        var rgbPixels = ReadRgbPixels(bitmap);
        var tolerance = Math.Max(1, options.LeaguePanelAnchorTolerance);
        var minLuminance = options.LeaguePanelAnchorMinLuminance;
        var maxSpread = options.LeaguePanelAnchorMaxChannelSpread;
        var targetR = options.LeaguePanelAnchorTargetR;
        var targetG = options.LeaguePanelAnchorTargetG;
        var targetB = options.LeaguePanelAnchorTargetB;

        for (var y = minY; y <= maxY; y++)
        {
            var rowOffset = y * bitmap.Width;
            for (var x = minX; x <= maxX; x++)
            {
                var rgbIndex = (rowOffset + x) * 3;
                var r = rgbPixels[rgbIndex];
                var g = rgbPixels[rgbIndex + 1];
                var b = rgbPixels[rgbIndex + 2];

                var maxChannel = Math.Max(r, Math.Max(g, b));
                var minChannel = Math.Min(r, Math.Min(g, b));
                var spread = maxChannel - minChannel;
                var luminance = ((299 * r) + (587 * g) + (114 * b)) / 1000;

                var dr = r - targetR;
                var dg = g - targetG;
                var db = b - targetB;
                var distanceToTarget = Math.Sqrt((dr * dr) + (dg * dg) + (db * db));

                var isNearTargetPalette = distanceToTarget <= tolerance;
                var isLightNeutral = luminance >= minLuminance && spread <= maxSpread;

                if (isNearTargetPalette || isLightNeutral)
                {
                    signal = new LeaguePanelAnchorSignal(x, y, r, g, b, luminance, spread, distanceToTarget);
                    return true;
                }
            }
        }

        var centerRgbIndex = (sampleY * bitmap.Width + sampleX) * 3;
        var cr = rgbPixels[centerRgbIndex];
        var cg = rgbPixels[centerRgbIndex + 1];
        var cb = rgbPixels[centerRgbIndex + 2];
        var cmaxChannel = Math.Max(cr, Math.Max(cg, cb));
        var cminChannel = Math.Min(cr, Math.Min(cg, cb));
        var cspread = cmaxChannel - cminChannel;
        var cluminance = ((299 * cr) + (587 * cg) + (114 * cb)) / 1000;
        var cdr = cr - targetR;
        var cdg = cg - targetG;
        var cdb = cb - targetB;
        var cdistance = Math.Sqrt((cdr * cdr) + (cdg * cdg) + (cdb * cdb));

        signal = new LeaguePanelAnchorSignal(sampleX, sampleY, cr, cg, cb, cluminance, cspread, cdistance);
        return false;
    }

    private static int ComputeAnchorX(Bitmap bitmap, OcrOptions options)
    {
        if (options.LeaguePanelAnchorFractionX > 0f)
        {
            return (int)(bitmap.Width * Math.Clamp(options.LeaguePanelAnchorFractionX, 0f, 1f));
        }

        return Math.Clamp(options.LeaguePanelAnchorSampleX, 0, bitmap.Width - 1);
    }

    private static int ComputeAnchorY(Bitmap bitmap, OcrOptions options)
    {
        if (options.LeaguePanelAnchorFractionY > 0f)
        {
            return (int)(bitmap.Height * Math.Clamp(options.LeaguePanelAnchorFractionY, 0f, 1f));
        }

        return Math.Clamp(options.LeaguePanelAnchorSampleY, 0, bitmap.Height - 1);
    }

    private static int ComputeAnchorRadius(Bitmap bitmap, OcrOptions options)
    {
        if (options.LeaguePanelAnchorSampleRadiusFraction > 0f)
        {
            return Math.Clamp(
                (int)(bitmap.Height * options.LeaguePanelAnchorSampleRadiusFraction),
                2,
                20);
        }

        return Math.Clamp(options.LeaguePanelAnchorSampleRadiusPx, 2, 20);
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

    private DebugCaptureContext? TryStartDebugCapture(Bitmap rawImage, OcrCaptureRegion region, string captureMethod)
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

            SaveBitmapWithOverwrite(rawImage, rawPath);

            logger.LogInformation(
                "Saved OCR debug images. Method={Method} Region=X={X} Y={Y} W={W} H={H} Raw={RawPath}. Row images overwrite as N.png and backup-guard probes overwrite as Nbg.png.",
                captureMethod,
                region.X,
                region.Y,
                region.Width,
                region.Height,
                rawPath);

            return new DebugCaptureContext(directory);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save OCR debug image.");
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
            logger.LogError(ex, "Failed to create OCR debug image directory: {Path}", directory);
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

        if (!string.IsNullOrWhiteSpace(stderr)
            && _appOptions.CurrentValue.DebugLogging)
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
        var normalized = line.Replace("�", "'").Replace('`', '\'');
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
