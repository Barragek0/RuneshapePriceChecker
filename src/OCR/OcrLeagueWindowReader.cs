using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.OCR;

public sealed class OcrLeagueWindowReader : ILeagueWindowReader, IDisposable
{
    private sealed record DebugCaptureContext(string DirectoryPath);

    private readonly IOptionsMonitor<OcrOptions> _options;
    private readonly IOptionsMonitor<AppOptions> _appOptions;
    private readonly IPoe2WindowResolutionProvider _windowResolutionProvider;
    private readonly ILogger<OcrLeagueWindowReader> _logger;
    private readonly DashboardService _dashboard;
    private readonly OcrCaptureStrategy _captureStrategy;
    private readonly TesseractEngineManager _engineManager;
    private WindowsOcrEngine? _windowsOcrEngine;
    private string? _activeOcrBackend;

    public OcrLeagueWindowReader(
        IOptionsMonitor<OcrOptions> options,
        IOptionsMonitor<AppOptions> appOptions,
        IPoe2WindowResolutionProvider windowResolutionProvider,
        ILogger<OcrLeagueWindowReader> logger,
        ILoggerFactory loggerFactory,
        DashboardService dashboard)
    {
        _options = options;
        _appOptions = appOptions;
        _windowResolutionProvider = windowResolutionProvider;
        _logger = logger;
        _dashboard = dashboard;
        _captureStrategy = new OcrCaptureStrategy(loggerFactory.CreateLogger<OcrCaptureStrategy>());
        _engineManager = new TesseractEngineManager(loggerFactory.CreateLogger<TesseractEngineManager>());

        var effectiveBackend = ResolveEffectiveOcrBackend(_options.CurrentValue.OcrBackend);
        if (!_windowsOcrSupported && string.Equals(_options.CurrentValue.OcrBackend, "windows", StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("Windows build {Build} < 17763 — OCR backend falling back to Tesseract.", Environment.OSVersion.Version.Build);
        else
            _logger.LogInformation("OCR backend: {Backend}", effectiveBackend);

        _ = _options.OnChange((updated, __) =>
        {
            _ = _engineManager.GetEngine(updated);
            var effective = ResolveEffectiveOcrBackend(updated.OcrBackend);
            if (!string.Equals(_activeOcrBackend, effective, StringComparison.OrdinalIgnoreCase))
            {
                var previous = _activeOcrBackend;
                _activeOcrBackend = null; // force engine re-init on next cycle
                _lastOcrText = "";        // force non-cached snapshot
                if (previous is not null) // only log actual switches, not initial load
                    _logger.LogInformation("OCR backend changed to: {Backend}", effective);
            }
        });
    }

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

    private sealed record OcrRunContext(string CaptureMethod, int[] RowYPositions, long FrameHash);

    private OcrLogState _logState;
    private OcrRunContext _runContext = new(string.Empty, [], 0);
    private string _lastOcrText = "";
    private int[] _lastOcrRowYPositions = [];
    private Rectangle? _lastCropBounds;
    private LeagueWindowSnapshot? _lastSnapshot;
    private bool _lastInterfaceDetected = true;
    private DateTimeOffset _lastDebugImageSavedAtUtc = DateTimeOffset.MinValue;
    private readonly LeaguePanelDetector _listDetector = new();
    private readonly OcrPerfTiming _perf = new();

    public void Warmup()
    {
        _ = _engineManager.GetEngine(_options.CurrentValue);
    }

    private string ResolveStatusLine()
    {
        var prefix = LosslessScaling.IsRunning ? "LS+" : "";
        var method = _runContext.CaptureMethod.Length > 0 ? _runContext.CaptureMethod : "none";
        return $"{prefix}{method}";
    }

    private LeagueWindowSnapshot CreateEmptySnapshot(DateTimeOffset capturedAt)
    {
        return new LeagueWindowSnapshot([], capturedAt, _runContext.RowYPositions, InterfaceDetected: _lastInterfaceDetected, CaptureMethod: ResolveStatusLine());
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
            var rawText = CaptureAndRecognize(out var attemptedRecognition, out var fromCache);
            if (!attemptedRecognition)
            {
                _lastSnapshot = null;
                return CreateEmptySnapshot(capturedAt);
            }

            if (fromCache && _lastSnapshot is not null)
            {
                return _lastSnapshot;
            }

            if (!fromCache && _appOptions.CurrentValue.LogLevel <= LogLevel.Debug && !_logState.HasFlag(OcrLogState.TesseractExecutionConfirmed))
            {
                _logState |= OcrLogState.TesseractExecutionConfirmed;
                _logger.LogDebug("OCR engine confirmed: tesseract executed successfully.");
            }

            var lines = OcrTextPostProcessor.ExtractLikelyItemNames(rawText);
            if (!fromCache && _appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
            {
                var yPositions = _runContext.RowYPositions;
                var items = lines.Count == 0
                    ? "<none>"
                    : string.Join(" | ", BuildItemDebugStrings(lines, yPositions));
                _logger.LogDebug("OCR detected {Count} items: {Items}", lines.Count, items);
            }

            _dashboard.SetStatus("Scanning league panel", "green");
            _lastSnapshot = new LeagueWindowSnapshot(lines, capturedAt, _runContext.RowYPositions, InterfaceDetected: true, CaptureMethod: ResolveStatusLine(), CropBounds: _lastCropBounds);
            return _lastSnapshot;
        }
        catch (FileNotFoundException ex)
        {
            _logState |= OcrLogState.TesseractUnavailable;
            _logger.LogWarning(
                "OCR disabled: {Reason} Install Tesseract from https://github.com/UB-Mannheim/tesseract/wiki then restart RuneshapePriceChecker.",
                ex.Message);
            return new LeagueWindowSnapshot([], capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logState |= OcrLogState.TesseractUnavailable;
            _logger.LogWarning(
                "OCR disabled: Tesseract not found. Install from https://github.com/UB-Mannheim/tesseract/wiki then restart RuneshapePriceChecker.");
            return new LeagueWindowSnapshot([], capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR capture/recognition failed.");
            return new LeagueWindowSnapshot([], capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
    }

    private string CaptureAndRecognize(out bool attemptedRecognition, out bool fromCache)
    {
        attemptedRecognition = false;
        fromCache = false;
        var t0 = _perf.RecordStart(OcrPerfTiming.Slot.Total);
        var options = _options.CurrentValue;
        if (options.SaveDebugImages)
        {
            EnsureDebugImageDirectoryExists(options);
        }

        if (!_windowResolutionProvider.IsPoe2WindowForeground || !IsPoe2ForegroundNow())
        {
            _lastInterfaceDetected = false;
            if (!_logState.HasFlag(OcrLogState.ForegroundWindowLogged))
            {
                _logState |= OcrLogState.ForegroundWindowLogged;
                _logger.LogInformation("OCR paused: waiting for Path of Exile 2 to be the active foreground window.");
            }

            _dashboard.SetStatus("Waiting for PoE2 window", "amber");
            return string.Empty;
        }

        if (_logState.HasFlag(OcrLogState.ForegroundWindowLogged))
        {
            _logState &= ~OcrLogState.ForegroundWindowLogged;
            _logger.LogInformation("PoE2 foreground confirmed; OCR scanning is active.");
        }

        if (options.UseWindowClientCapture && _windowResolutionProvider.CurrentWindowCaptureContext is null)
        {
            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug && !_logState.HasFlag(OcrLogState.WindowContextLogged))
            {
                _logState |= OcrLogState.WindowContextLogged;
                _logger.LogDebug("OCR warm-up: waiting for PoE2 window capture context before first scan.");
            }

            return string.Empty;
        }

        _logState &= ~OcrLogState.WindowContextLogged;

        var region = ResolveCaptureRegion();
        ValidateRegion(region);

        using var _ = _perf.Measure(OcrPerfTiming.Slot.Capture);
        var captureResult = _captureStrategy.Capture(region, _windowResolutionProvider.CurrentWindowCaptureContext, options);
        using var capturedBitmap = captureResult.Bitmap;
        var captureMethod = captureResult.Method;

        if (!string.Equals(_runContext.CaptureMethod, captureMethod, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("OCR capture source active: {CaptureMethod}.", captureMethod);
        }

        _runContext = _runContext with { CaptureMethod = captureMethod };

        using (_perf.Measure(OcrPerfTiming.Slot.AnchorCheck))
        {
            if (!TryDetectPanelOpen(capturedBitmap, options, region))
            {
                _runContext = _runContext with { FrameHash = 0 };
                return string.Empty;
            }
        }

        using (_perf.Measure(OcrPerfTiming.Slot.FrameHash))
        {
            if (TrySkipOcrViaFrameDifferencing(capturedBitmap))
            {
                attemptedRecognition = true;
                fromCache = true;
                _runContext = _runContext with { RowYPositions = _lastOcrRowYPositions };
                _perf.RecordEnd(OcrPerfTiming.Slot.CacheHit, t0);
                LogPerfIfDue();
                return _lastOcrText;
            }
        }

        var debugContext = options.SaveDebugImages
            ? TryStartDebugCapture(capturedBitmap, region, captureMethod)
            : null;

        attemptedRecognition = true;

        if (IsWindowsOcrEnabled(options))
        {
            EnsureWindowsOcrEngine();
            using var masked = OcrImagePreprocessor.KeepBlackAndNeighbors(capturedBitmap);
            using var preprocessed = OcrImagePreprocessor.PreprocessForOcr(masked, options);
            var crop = OcrImagePreprocessor.FindContentBounds(preprocessed);
            using var cropped = crop.HasValue
                ? OcrImagePreprocessor.CropBitmap(preprocessed, crop.Value)
                : (Bitmap)preprocessed.Clone();

            var rawText = _windowsOcrEngine!.Recognize(cropped, out var rowYs, 1, _perf);
            if (crop.HasValue)
            {
                var cropY = crop.Value.Y;
                for (var i = 0; i < rowYs.Length; i++) rowYs[i] += cropY;
            }

            var lines = OcrImagePreprocessor.SplitAndTrim(rawText);
            _lastOcrText = string.Join(Environment.NewLine, lines);
            _lastOcrRowYPositions = rowYs;
            _lastCropBounds = crop;
            _runContext = _runContext with { RowYPositions = rowYs };
        }
        else
        {
            var engine = _engineManager.GetEngine(options);
            var result = OcrImagePreprocessor.Process(capturedBitmap, engine, options, debugContext?.DirectoryPath, _perf);
            _lastOcrText = result.Text;
            _lastOcrRowYPositions = result.RowYPositions;
            _lastCropBounds = result.CropBounds;
            _runContext = _runContext with { RowYPositions = result.RowYPositions };
        }

        _perf.RecordEnd(OcrPerfTiming.Slot.Total, t0);
        LogPerfFullOcrIfDue();
        return _lastOcrText;
    }

    private static bool IsWindowsOcrEnabled(OcrOptions options)
    {
        return string.Equals(options.OcrBackend, "windows", StringComparison.OrdinalIgnoreCase) && _windowsOcrSupported;
    }

    private static readonly bool _windowsOcrSupported = Environment.OSVersion.Version.Build >= 17763;

    private static string ResolveEffectiveOcrBackend(string configuredBackend)
    {
        if (string.Equals(configuredBackend, "windows", StringComparison.OrdinalIgnoreCase) && !_windowsOcrSupported)
            return "tesseract"; // fallback: Windows build too old for WinRT OCR
        return configuredBackend;
    }

    private void EnsureWindowsOcrEngine()
    {
        var backend = ResolveEffectiveOcrBackend(_options.CurrentValue.OcrBackend);
        if (_activeOcrBackend == backend && _windowsOcrEngine is not null)
            return;

        _windowsOcrEngine?.Dispose();
        _windowsOcrEngine = new WindowsOcrEngine();
        _activeOcrBackend = backend;
        _logger.LogInformation("OCR backend: Windows.Media.Ocr");
    }

    private void LogPerfIfDue()
    {
        if (_perf.ShouldLog() && _logger.IsEnabled(LogLevel.Debug))
        {
            var report = _perf.GetAndResetReport();
            if (report.Length > "OCR perf (avg us): ".Length)
                _logger.LogDebug("{Report}", report);
        }
    }

    private void LogPerfFullOcrIfDue()
    {
        if (_perf.ShouldLogFullOcr() && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("{Report}", _perf.GetAndResetReport());
    }

    private bool TrySkipOcrViaFrameDifferencing(Bitmap bitmap)
    {
        var hash = ComputeFastFrameHash(bitmap);
        if (hash == 0) return false;
        if (hash == _runContext.FrameHash && _lastOcrText.Length > 0)
            return true;
        _runContext = _runContext with { FrameHash = hash };
        return false;
    }

    private static long ComputeFastFrameHash(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data;
        try { data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat); }
        catch { return 0; }

        try
        {
            var bpp = Image.GetPixelFormatSize(bitmap.PixelFormat) / 8;
            if (bpp < 3) return 0;
            var stride = Math.Abs(data.Stride);
            var rowBytes = new byte[stride];

            unchecked
            {
                long hash = 17;
                const int stepX = 8;
                const int stepY = 4;
                for (var y = 0; y < data.Height; y += stepY)
                {
                    Marshal.Copy(data.Scan0 + y * stride, rowBytes, 0, stride);
                    for (var x = 0; x < data.Width; x += stepX)
                    {
                        var idx = x * bpp;
                        if (idx + 2 < stride)
                            hash = hash * 31 + rowBytes[idx] + rowBytes[idx + 1] + rowBytes[idx + 2];
                    }
                }
                return hash;
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private bool IsPoe2ForegroundNow()
    {
        var context = _windowResolutionProvider.CurrentWindowCaptureContext;
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

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var pxFormat = capturedBitmap.PixelFormat.ToString();
            _logger.LogDebug(
                "Panel check: region=({RegX},{RegY} {RegW}x{RegH}) scanX={ScanX0}-{ScanX1} scanY={ScanY} fmt={Fmt} open={Open}",
                region.X, region.Y, region.Width, region.Height,
                region.X + (int)(region.Width * LeaguePanelDetector.LeftFraction),
                region.X + (int)(region.Width * LeaguePanelDetector.RightFraction),
                region.Y + (int)(region.Height * LeaguePanelDetector.TopRowFraction),
                pxFormat, diag.PanelOpen);
        }
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "Panel check diagnostics: bri={Brightness} black={Black}/{Total} minSum={MinSum}",
                diag.AvgBrightness, diag.BlackCount, diag.TotalCount, diag.MinSum);
        }

        if (!panelOpen)
        {
            if (options.SaveDebugImages)
            {
                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "ocr-debug");
                    _ = Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "panel-check-fail.png");
                    OcrCaptureStrategy.SaveBitmapWithOverwrite(capturedBitmap, path);
                }
                catch { }
            }

            _lastInterfaceDetected = false;
            _dashboard.SetStatus("Waiting for league panel", "amber");
            return false;
        }

        _dashboard.SetStatus("Scanning league panel", "green");
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
            _ = Directory.CreateDirectory(directory);
            if (!_logState.HasFlag(OcrLogState.DebugDirectoryLogged))
            {
                _logState |= OcrLogState.DebugDirectoryLogged;
                _logger.LogInformation("OCR debug image output enabled. Directory: {Path}", Path.GetFullPath(directory));
            }

            var rawPath = Path.Combine(directory, "raw.png");

            OcrCaptureStrategy.SaveBitmapWithOverwrite(rawImage, rawPath);

            _logger.LogInformation(
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
            _logger.LogError(ex, "Failed to save OCR debug image.");
            return null;
        }
    }

    private static string ResolveDebugImageDirectory(OcrOptions options)
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
            _ = Directory.CreateDirectory(directory);
            if (!_logState.HasFlag(OcrLogState.DebugDirectoryLogged))
            {
                _logState |= OcrLogState.DebugDirectoryLogged;
                _logger.LogInformation("OCR debug image output enabled. Directory: {Path}", Path.GetFullPath(directory));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OCR debug image directory: {Path}", directory);
        }
    }

    private OcrCaptureRegion ResolveCaptureRegion()
    {
        var region = _windowResolutionProvider.CurrentCaptureRegion ?? throw new InvalidOperationException("No OCR capture region is available. Add/update the current resolution in OcrResolutionProfiles.");
        return region;
    }

    private static void ValidateRegion(OcrCaptureRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new InvalidOperationException("OCR capture region must have positive width and height.");
        }
    }

    public void Dispose()
    {
        _engineManager.Dispose();
    }

    private static string[] BuildItemDebugStrings(IReadOnlyList<string> lines, int[] yPositions)
    {
        var result = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            result[i] = i < yPositions.Length ? $"{lines[i]} @Y={yPositions[i]}" : lines[i];
        return result;
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
