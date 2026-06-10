using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.OCR;

public sealed class OcrLeagueWindowReader(
    IOptionsMonitor<OcrOptions> options,
    IOptionsMonitor<AppOptions> appOptions,
    IPoe2WindowResolutionProvider windowResolutionProvider,
    ILogger<OcrLeagueWindowReader> logger,
    ILoggerFactory loggerFactory,
    DashboardService dashboard) : ILeagueWindowReader
{
    private sealed record DebugCaptureContext(string DirectoryPath);

    private readonly IOptionsMonitor<OcrOptions> _options = options;
    private readonly IOptionsMonitor<AppOptions> _appOptions = appOptions;
    private readonly OcrCaptureStrategy _captureStrategy = new(loggerFactory.CreateLogger<OcrCaptureStrategy>());
    private readonly TesseractEngineManager _engineManager = new(loggerFactory.CreateLogger<TesseractEngineManager>());

    [Flags]
    private enum OcrLogState
    {
        None = 0,
        TesseractUnavailable = 1 << 0,
        WindowContextLogged = 1 << 1,
        ForegroundWindowLogged = 1 << 2,
        DebugDirectoryLogged = 1 << 3,
        TesseractExecutionConfirmed = 1 << 4
    }

    private sealed record OcrRunContext(string CaptureMethod, int[] RowYPositions);

    private OcrLogState _logState;
    private OcrRunContext _runContext = new(string.Empty, []);
    private bool _lastInterfaceDetected = true;
    private DateTimeOffset _lastDebugImageSavedAtUtc = DateTimeOffset.MinValue;
    private readonly ListDetector _listDetector = new();

    private string ResolveStatusLine()
    {
        var prefix = LosslessScaling.IsRunning ? "LS+" : "";
        var method = _runContext.CaptureMethod.Length > 0 ? _runContext.CaptureMethod : "none";
        return $"{prefix}{method}";
    }

    private LeagueWindowSnapshot CreateEmptySnapshot(DateTimeOffset capturedAt)
    {
        return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt, _runContext.RowYPositions, InterfaceDetected: _lastInterfaceDetected, CaptureMethod: ResolveStatusLine());
    }

    public LeagueWindowSnapshot ReadSnapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;

        if (_logState.HasFlag(OcrLogState.TesseractUnavailable))
        {
            return CreateEmptySnapshot(capturedAt);
        }

        try
        {
            var rawText = CaptureAndRecognize(out var attemptedRecognition);
            if (!attemptedRecognition)
            {
                return CreateEmptySnapshot(capturedAt);
            }

            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug && !_logState.HasFlag(OcrLogState.TesseractExecutionConfirmed))
            {
                _logState |= OcrLogState.TesseractExecutionConfirmed;
                logger.LogDebug("OCR engine confirmed: tesseract executed successfully.");
            }

            var lines = OcrTextPostProcessor.ExtractLikelyItemNames(rawText);
            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
            {
                var yPositions = _runContext.RowYPositions;
                var items = lines.Count == 0
                    ? "<none>"
                    : string.Join(" | ", lines.Select((line, i) =>
                        i < yPositions.Length
                            ? $"{line} @Y={yPositions[i]}"
                            : line));
                logger.LogDebug("OCR detected {Count} items: {Items}", lines.Count, items);
            }

            dashboard.SetStatus("Scanning league panel", "green");
            return new LeagueWindowSnapshot(lines, capturedAt, _runContext.RowYPositions, InterfaceDetected: true, CaptureMethod: ResolveStatusLine());
        }
        catch (FileNotFoundException ex)
        {
            _logState |= OcrLogState.TesseractUnavailable;
            logger.LogWarning(
                "OCR disabled: {Reason} Install Tesseract from https://github.com/UB-Mannheim/tesseract/wiki then restart RuneshapePriceChecker.",
                ex.Message);
            return new LeagueWindowSnapshot(Array.Empty<string>(), capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logState |= OcrLogState.TesseractUnavailable;
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
            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug && !_logState.HasFlag(OcrLogState.ForegroundWindowLogged))
            {
                _logState |= OcrLogState.ForegroundWindowLogged;
                logger.LogDebug("OCR paused: waiting for Path of Exile 2 to be the active foreground window.");
            }

            dashboard.SetStatus("Waiting for PoE2 window", "amber");
            return string.Empty;
        }

        _logState &= ~OcrLogState.ForegroundWindowLogged;

        if (options.UseWindowClientCapture && windowResolutionProvider.CurrentWindowCaptureContext is null)
        {
            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug && !_logState.HasFlag(OcrLogState.WindowContextLogged))
            {
                _logState |= OcrLogState.WindowContextLogged;
                logger.LogDebug("OCR warm-up: waiting for PoE2 window capture context before first scan.");
            }

            return string.Empty;
        }

        _logState &= ~OcrLogState.WindowContextLogged;

        var region = ResolveCaptureRegion();
        ValidateRegion(region);

        var captureResult = _captureStrategy.Capture(region, windowResolutionProvider.CurrentWindowCaptureContext, options);
        using var capturedBitmap = captureResult.Bitmap;
        var captureMethod = captureResult.Method;

        if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug &&
            !string.Equals(_runContext.CaptureMethod, captureMethod, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("OCR capture source active: {CaptureMethod}.", captureMethod);
        }

        _runContext = _runContext with { CaptureMethod = captureMethod };

        if (!TryDetectPanelOpen(capturedBitmap, options, region))
        {
            return string.Empty;
        }

        var debugContext = options.SaveDebugImages
            ? TryStartDebugCapture(capturedBitmap, region, captureMethod)
            : null;

        attemptedRecognition = true;
        var engine = _engineManager.GetEngine(options);
        var result = OcrImagePreprocessor.Process(capturedBitmap, engine, options, debugContext?.DirectoryPath);
        _runContext = _runContext with { RowYPositions = result.RowYPositions };
        return result.Text;
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

    private bool TryDetectPanelOpen(Bitmap capturedBitmap, OcrOptions options, OcrCaptureRegion region)
    {
        bool panelOpen = _listDetector.Update(capturedBitmap, out var diag);

        if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
        {
            var pxFormat = capturedBitmap.PixelFormat.ToString();
            logger.LogDebug(
                "Panel check: region=({RegX},{RegY} {RegW}x{RegH}) scanX={ScanX0}-{ScanX1} scanY={ScanY} fmt={Fmt} open={Open}",
                region.X, region.Y, region.Width, region.Height,
                region.X + (int)(region.Width * ListDetector.LeftFraction),
                region.X + (int)(region.Width * ListDetector.RightFraction),
                region.Y + (int)(region.Height * ListDetector.TopRowFraction),
                pxFormat, diag.PanelOpen);
            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Trace)
            {
                logger.LogTrace(
                    "Panel check diagnostics: bri={Brightness} black={Black}/{Total} minSum={MinSum}",
                    diag.AvgBrightness, diag.BlackCount, diag.TotalCount, diag.MinSum);
            }
        }

        if (!panelOpen)
        {
            if (options.SaveDebugImages)
            {
                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "ocr-debug");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "panel-check-fail.png");
                    OcrCaptureStrategy.SaveBitmapWithOverwrite(capturedBitmap, path);
                }
                catch { }
            }

            _lastInterfaceDetected = false;
            dashboard.SetStatus("Waiting for league panel", "amber");
            return false;
        }

        dashboard.SetStatus("Scanning league panel", "green");
        _lastInterfaceDetected = true;
        return true;
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
            if (!_logState.HasFlag(OcrLogState.DebugDirectoryLogged))
            {
                _logState |= OcrLogState.DebugDirectoryLogged;
                logger.LogInformation("OCR debug image output enabled. Directory: {Path}", Path.GetFullPath(directory));
            }

            var rawPath = Path.Combine(directory, "raw.png");

            OcrCaptureStrategy.SaveBitmapWithOverwrite(rawImage, rawPath);

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
            if (!_logState.HasFlag(OcrLogState.DebugDirectoryLogged))
            {
                _logState |= OcrLogState.DebugDirectoryLogged;
                logger.LogInformation("OCR debug image output enabled. Directory: {Path}", Path.GetFullPath(directory));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create OCR debug image directory: {Path}", directory);
        }
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

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

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
