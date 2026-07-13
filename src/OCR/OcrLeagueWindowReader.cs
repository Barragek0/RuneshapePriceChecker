using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
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
    private string? _activeOcrLanguage;
    private string? _detectedLanguage; // from Poe2ConfigFile, overrides OcrOptions.Language

    public OcrLeagueWindowReader(
        IOptionsMonitor<OcrOptions> options,
        IOptionsMonitor<AppOptions> appOptions,
        IPoe2WindowResolutionProvider windowResolutionProvider,
        ILogger<OcrLeagueWindowReader> logger,
        ILoggerFactory loggerFactory,
        DashboardService dashboard,
        DebugMetricsCollector metrics)
    {
        _options = options;
        _appOptions = appOptions;
        _windowResolutionProvider = windowResolutionProvider;
        _logger = logger;
        _dashboard = dashboard;
        _metrics = metrics;
        _captureStrategy = new OcrCaptureStrategy(loggerFactory.CreateLogger<OcrCaptureStrategy>());
        _engineManager = new TesseractEngineManager(loggerFactory.CreateLogger<TesseractEngineManager>());
        _listDetector = new LeaguePanelDetector(options, loggerFactory.CreateLogger<LeaguePanelDetector>());

        var effectiveBackend = ResolveEffectiveOcrBackend(_options.CurrentValue.OcrBackend);
        if (!string.Equals(effectiveBackend, _options.CurrentValue.OcrBackend, StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("OCR backend falling back from {Configured} to {Effective} (Windows build too old).",
                _options.CurrentValue.OcrBackend, effectiveBackend);
        else
            _logger.LogInformation("OCR backend: {Backend}", effectiveBackend);
        var poe2Lang = Poe2ConfigFile.Language;
        _detectedLanguage = poe2Lang is not null && ItemNameTranslator.IsLanguageSupported(poe2Lang) ? poe2Lang : null;
        _logger.LogDebug("PoE2 game language detected: {Detected} (configured default: {Configured})",
            poe2Lang ?? "(none)", _options.CurrentValue.Language);
        _metrics.OcrBackend = effectiveBackend;
        _metrics.Language = _detectedLanguage ?? _options.CurrentValue.Language;
        _metrics.OcrEngineMode = _options.CurrentValue.OcrEngineMode.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Seed debug-image tracking fields so that the first OnChange callback
        // can detect actual changes vs the initial values.
        var initial = _options.CurrentValue;
        _lastDebugSaveEnabled = initial.SaveDebugImages;
        _lastDebugImageInterval = initial.DebugImageIntervalSeconds;
        _lastDebugImageDirectory = initial.DebugImageDirectory;

        // Listen for PoE2 config file changes (language, brightness, etc.)
        Poe2ConfigFile.ConfigChanged += OnPoe2ConfigChanged;

        _ = _options.OnChange((updated, __) =>
        {
            var effective = ResolveEffectiveOcrBackend(updated.OcrBackend);
            // Only keep Tesseract engine loaded when Tesseract is the selected backend
            if (!string.Equals(effective, "windows", StringComparison.OrdinalIgnoreCase))
                _engineManager.GetEngine(updated);
            else
                _engineManager.DisposeEngine();

            if (!string.Equals(_activeOcrBackend, effective, StringComparison.OrdinalIgnoreCase))
            {
                var previous = _activeOcrBackend;
                _activeOcrBackend = null; // force engine re-init on next cycle
                _lastOcrText = "";        // force non-cached snapshot
                _metrics.OcrBackend = effective; // update so dashboard toggles slot breakdown
                if (previous is not null) // only log actual switches, not initial load
                    _logger.LogInformation("OCR backend changed to: {Backend}", effective);
            }
            // Always invalidate the frame-differencing cache when any OCR option changes
            // (tolerance, thresholds, binarization, etc.) so the new value takes effect
            // on the next cycle rather than returning stale cached text.
            _lastOcrText = "";
            _lastOcrOptions = null;
            _runContext = _runContext with { FrameHash = 0 };

            // When debug-image settings change (SaveDebugImages, interval, directory),
            // reset the debug capture gate so the change takes effect immediately.
            if (updated.SaveDebugImages != _lastDebugSaveEnabled ||
                updated.DebugImageIntervalSeconds != _lastDebugImageInterval ||
                !string.Equals(updated.DebugImageDirectory, _lastDebugImageDirectory, StringComparison.Ordinal))
            {
                _lastDebugImageSavedAtUtc = DateTimeOffset.MinValue;
                _lastDebugFrameHash = 0;
                _lastDebugSaveEnabled = updated.SaveDebugImages;
                _lastDebugImageInterval = updated.DebugImageIntervalSeconds;
                _lastDebugImageDirectory = updated.DebugImageDirectory;
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
        TesseractExecutionConfirmed = 1 << 4,
        UnsupportedLanguage = 1 << 5
    }

    private sealed record OcrRunContext(string CaptureMethod, int[] RowYPositions, long FrameHash);

    private OcrLogState _logState;
    private OcrRunContext _runContext = new(string.Empty, [], 0);
    private string _lastOcrText = "";
    private OcrOptions? _lastOcrOptions;           // options used for _lastOcrText
    private int[] _lastOcrRowYPositions = [];
    private Rectangle? _lastCropBounds;
    private LeagueWindowSnapshot? _lastSnapshot;
    private bool _lastInterfaceDetected = true;
    private DateTimeOffset _lastDebugImageSavedAtUtc = DateTimeOffset.MinValue;
    private DateTime _lastPerfMetricsLogAt = DateTime.MinValue;
    private long _lastDebugFrameHash;
    private bool _lastDebugSaveEnabled;
    private int _lastDebugImageInterval;
    private string _lastDebugImageDirectory = string.Empty;
    private readonly LeaguePanelDetector _listDetector;
    private readonly OcrPerfTiming _perf = new();
    private readonly DebugMetricsCollector _metrics;

    // Last captured and preprocessed bitmaps, kept for per-row OCR cropping.
    private Bitmap? _lastCaptureBitmap;
    private Bitmap? _lastPreprocessedBitmap;
    private readonly List<Rectangle> _retryRegions = [];
    private readonly List<Rectangle> _rejectedRegions = [];
    private int[] _lastOcrRowHeights = [];
    private string[]? _lastRowTexts;

    public void Warmup()
    {
        var rawBackend = _options.CurrentValue.OcrBackend;
        var backend = ResolveEffectiveOcrBackend(rawBackend);
        _logger.LogInformation("Warmup: configured='{Raw}' effective='{Eff}' windowsOcrSupported={Sup}",
            rawBackend, backend, _windowsOcrSupported);

        if (string.Equals(backend, "windows", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Warmup: initializing Windows OCR engine");
            EnsureWindowsOcrEngine();
        }
        else
        {
            _logger.LogDebug("Warmup: initializing Tesseract engine");
            _ = _engineManager.GetEngine(_options.CurrentValue);
        }
    }

    private string ResolveStatusLine()
    {
        var prefix = LosslessScaling.IsRunning ? "LS+" : "";
        var method = _runContext.CaptureMethod.Length > 0 ? _runContext.CaptureMethod : "none";
        var engine = _activeOcrBackend ?? "unknown";
        return $"{prefix}{method} ({engine})";
    }

    private LeagueWindowSnapshot CreateEmptySnapshot(DateTimeOffset capturedAt)
    {
        return new LeagueWindowSnapshot([], capturedAt, [], InterfaceDetected: _lastInterfaceDetected, CaptureMethod: ResolveStatusLine());
    }

    public LeagueWindowSnapshot ReadSnapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;


        if (_logState.HasFlag(OcrLogState.TesseractUnavailable))
        {
            return CreateEmptySnapshot(capturedAt);
        }

        var currentLang = _detectedLanguage ?? _options.CurrentValue.Language;
        if (!ItemNameTranslator.IsLanguageSupported(currentLang))
        {
            if (!_logState.HasFlag(OcrLogState.UnsupportedLanguage))
            {
                _logState |= OcrLogState.UnsupportedLanguage;
                _logger.LogWarning("OCR disabled — language '{Lang}' is not supported", currentLang);
            }
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
                return _lastSnapshot;

            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug && !_logState.HasFlag(OcrLogState.TesseractExecutionConfirmed))
            {
                _logState |= OcrLogState.TesseractExecutionConfirmed;
                _logger.LogDebug("OCR engine confirmed: tesseract executed successfully.");
            }

            var (lines, matchedYPositions) = _lastRowTexts is not null
                ? OcrTextPostProcessor.ExtractFromRowTexts(
                    _lastRowTexts, _runContext.RowYPositions, _detectedLanguage)
                : OcrTextPostProcessor.ExtractWithYPositions(
                    rawText, _runContext.RowYPositions, _detectedLanguage);

            // Draw purple boxes matching each row's individual content bounds.
            // Clear stale regions from previous cycles first so old positions don't
            // accumulate (they were never cleared, causing duplicate offset boxes).
            _retryRegions.Clear();
            _rejectedRegions.Clear();
            var validRowMask = new bool[_lastOcrRowYPositions.Length > 0 ? _lastOcrRowYPositions.Length : 0];
            if (_lastOcrRowYPositions.Length > 0 && _lastPreprocessedBitmap is not null)
            {
                var pbmp = _lastPreprocessedBitmap;
                var rect = new Rectangle(0, 0, pbmp.Width, pbmp.Height);
                // Only keep rows whose content extends to the rightmost 15% of
                // the capture region — narrow clusters near the left are rune
                // icons, not real text.
                var rightEdgeThreshold = (int)(pbmp.Width * 0.85);
                var data = pbmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var stride = data.Stride;
                    var bytes = new byte[Math.Abs(stride) * pbmp.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

                    // Text column starts at PanelLeftFraction — don't scan left of it
                    var textColStart = (int)(pbmp.Width * _options.CurrentValue.PanelLeftFraction);

                    for (var i = 0; i < _lastOcrRowYPositions.Length; i++)
                    {
                        var y = _lastOcrRowYPositions[i];
                        var h = i < _lastOcrRowHeights.Length ? _lastOcrRowHeights[i] : 16;
                        var yEnd = Math.Min(y + h, pbmp.Height);

                        // Find leftmost and rightmost dark pixel within this row band,
                        // but only within the text column (right of the yellow dashed line)
                        var minX = pbmp.Width;
                        var maxX = 0;
                        for (var ry = y; ry < yEnd; ry++)
                        {
                            var rowOffset = ry * stride;
                            for (var rx = textColStart; rx < pbmp.Width; rx++)
                            {
                                if (bytes[rowOffset + (rx * 3)] < 128)
                                {
                                    if (rx < minX) minX = rx;
                                    if (rx > maxX) maxX = rx;
                                }
                            }
                        }

                        if (maxX >= minX)
                        {
                            const int margin = 4;
                            var boxX = Math.Max(0, minX - margin);
                            var boxW = Math.Min(pbmp.Width - boxX, maxX - minX + (margin * 2));
                            var right = boxX + boxW;
                            var box = new Rectangle(boxX, y, boxW, h);

                            // Only keep rows whose content reaches the rightmost
                            // portion of the capture region — real text spans most
                            // of the width, rune icons don't.
                            if (right >= rightEdgeThreshold)
                            {
                                validRowMask[i] = true;
                                _retryRegions.Add(box);
                            }
                            else
                            {
                                _rejectedRegions.Add(box);
                                if (_logger.IsEnabled(LogLevel.Trace))
                                    _logger.LogTrace(
                                        "Row {Row} @Y={Y} rejected: right edge {Right} < threshold {Threshold} (cluster too narrow for text)",
                                        i, y, right, rightEdgeThreshold);
                            }
                        }
                        else
                        {
                            // Fallback: use full width (always valid)
                            validRowMask[i] = true;
                            _retryRegions.Add(new Rectangle(0, y, pbmp.Width, h));
                        }
                    }
                }
                finally
                {
                    pbmp.UnlockBits(data);
                }

                if (_logger.IsEnabled(LogLevel.Debug) && _rejectedRegions.Count > 0)
                {
                    _logger.LogDebug(
                        "Row filtering: {Kept} kept, {Rejected} rejected (right edge < {Threshold}px)",
                        _retryRegions.Count, _rejectedRegions.Count, rightEdgeThreshold);
                }

                // Filter lines/matchedYPositions to match the kept rows.
                // Since ExtractFromRowTexts now returns correctly-paired data
                // (each line keeps its original Y position), we map each line
                // back to its original row index so the right-edge rejection
                // mask (indexed by original row index) is applied correctly.
                if (validRowMask.Length > 0)
                {
                    var keptLines = new List<string>(lines.Length);
                    var keptY = new List<int>(matchedYPositions.Length);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var y = matchedYPositions[i];
                        var origIndex = Array.IndexOf(_lastOcrRowYPositions, y);
                        if (origIndex >= 0 && origIndex < validRowMask.Length && validRowMask[origIndex])
                        {
                            keptLines.Add(lines[i]);
                            keptY.Add(matchedYPositions[i]);
                        }
                    }
                    lines = [.. keptLines];
                    matchedYPositions = [.. keptY];
                }

                _lastCropBounds = null; // hide green content-bounds box
            }

            if (_appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
            {
                var items = lines.Length == 0
                    ? "<none>"
                    : string.Join(" | ", BuildItemDebugStrings(lines, matchedYPositions));
                _logger.LogDebug("OCR detected {Count} items: {Items}", lines.Length, items);
            }

            _dashboard.SetStatus("Scanning league panel", "green");

            _lastSnapshot = new LeagueWindowSnapshot(lines, capturedAt, matchedYPositions, InterfaceDetected: true, CaptureMethod: ResolveStatusLine(), CropBounds: _lastCropBounds, RetryRegions: _retryRegions.Count > 0 ? [.. _retryRegions] : null, RejectedRegions: _rejectedRegions.Count > 0 ? [.. _rejectedRegions] : null);
            _metrics.ItemsDetected = lines.Length;
            _metrics.InterfaceDetected = true;
            return _lastSnapshot;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logState |= OcrLogState.TesseractUnavailable;
            _logger.LogWarning(
                "OCR disabled: Tesseract not found. Install from https://github.com/UB-Mannheim/tesseract/wiki then restart RuneshapePriceChecker.");
            return new LeagueWindowSnapshot([], capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or DllNotFoundException)
        {
            _logState |= OcrLogState.TesseractUnavailable;
            _logger.LogWarning(
                "OCR disabled: {Reason} Install Tesseract from https://github.com/UB-Mannheim/tesseract/wiki then restart RuneshapePriceChecker.",
                ex.Message);
            return new LeagueWindowSnapshot([], capturedAt, InterfaceDetected: _lastInterfaceDetected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR pipeline threw unhandled exception: {Context} (lang={Lang} backend={Backend} capture={Method})",
                ErrorContext.FromException(ex),
                _detectedLanguage ?? _options.CurrentValue.Language, _activeOcrBackend, _runContext.CaptureMethod);
            return new LeagueWindowSnapshot([], capturedAt, InterfaceDetected: false);
        }
    }

    private string CaptureAndRecognize(out bool attemptedRecognition, out bool fromCache)
    {
        attemptedRecognition = false;
        fromCache = false;
        _perf.ResetCycleSlotMs();
        var t0 = OcrPerfTiming.RecordStart(OcrPerfTiming.Slot.Total);
        var options = _options.CurrentValue;
        if (_detectedLanguage is not null)
        {
            if (ItemNameTranslator.IsLanguageSupported(_detectedLanguage))
            {
                if (!string.Equals(options.Language, _detectedLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "OCR language mismatch: configured={Configured}, detected={Detected}. Using detected language.",
                        options.Language, _detectedLanguage);
                }
                options.Language = _detectedLanguage;
            }
            else
            {
                _logger.LogDebug(
                    "OCR language: detected='{Detected}' is not supported — keeping configured default '{Lang}'.",
                    _detectedLanguage, options.Language);
            }
        }
        else
        {
            _logger.LogTrace("OCR language: using configured default '{Lang}' (no detected language override).", options.Language);
        }
        if (options.SaveDebugImages)
            EnsureDebugImageDirectoryExists(options);

        // Use the cached foreground state from Poe2WindowResolutionService
        // (updated every 1 second) instead of an extra per-cycle Win32 call.
        if (!_windowResolutionProvider.IsPoe2WindowForeground)
        {
            _lastInterfaceDetected = false;
            if (!_logState.HasFlag(OcrLogState.ForegroundWindowLogged))
            {
                _logState |= OcrLogState.ForegroundWindowLogged;
                _logger.LogDebug("OCR paused: waiting for Path of Exile 2 to be the active foreground window.");
            }

            _dashboard.SetStatus("Waiting for PoE2 window", "amber");
            _metrics.IsPoe2Foreground = false;
            _metrics.InterfaceDetected = false;
            return string.Empty;
        }

        if (_logState.HasFlag(OcrLogState.ForegroundWindowLogged))
        {
            _logState &= ~OcrLogState.ForegroundWindowLogged;
            _logger.LogDebug("PoE2 foreground confirmed; OCR scanning is active.");
        }

        if (_windowResolutionProvider.CurrentWindowCaptureContext is null)
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
        if (region is null)
        {
            _metrics.InterfaceDetected = false;
            return string.Empty;
        }
        ValidateRegion(region);
        _metrics.RegionInfo = $"{region.Width}x{region.Height}";

        // Pre-capture panel check: capture only the scan rectangle area (~40K px)
        // instead of the full region (266K px) to check if the panel is still closed.
        // Uses a direct CopyFromScreen to bypass the capture strategy's fallback chain.
        var opts = _options.CurrentValue;
        var scanRect = new OcrCaptureRegion(
            region.X + (int)(region.Width * opts.PanelLeftFraction),
            region.Y,
            (int)(region.Width * (opts.PanelRightFraction - opts.PanelLeftFraction)),
            (int)(region.Height * opts.PanelTopRowFraction));
        using (var preCapturePerf = _perf.Measure(OcrPerfTiming.Slot.AnchorCheck))
        {
            using var scanBitmap = CaptureDesktopRegionDirect(scanRect);
            if (scanBitmap is null || !TryDetectPanelOpen(scanBitmap, options, region))
            {
                _metrics.AnchorCheckFails++;
                _metrics.InterfaceDetected = false;
                return string.Empty;
            }
            _metrics.AnchorCheckPasses++;
        }

        CaptureResult captureResult;
        string captureMethod;
        using (_perf.Measure(OcrPerfTiming.Slot.Capture))
        {
            captureResult = _captureStrategy.Capture(region, _windowResolutionProvider.CurrentWindowCaptureContext, options);
            captureMethod = captureResult.Method;
        }

        // Track capture mode failures: if user selected a specific mode but we got a different one, it failed.
        // If they match, clear any prior failure so transient issues don't permanently disable a mode.
        var configuredMode = options.CaptureMode?.ToLowerInvariant() ?? "printwindow";
        var actualPrefix = captureMethod switch
        {
            string m when m.Contains("bitblt") => "bitblt",
            string m when m.Contains("printwindow") => "printwindow",
            _ => "desktop"
        };
        if (!string.Equals(configuredMode, actualPrefix, StringComparison.OrdinalIgnoreCase))
            _ = _metrics.FailedCaptureModes.TryAdd(configuredMode, 0);
        else
            _ = _metrics.FailedCaptureModes.TryRemove(configuredMode, out _);
        using var capturedBitmap = captureResult.Bitmap;

        // Keep a clone for per-line retry OCR (used when lines fail to price)
        _lastCaptureBitmap?.Dispose();
        _lastCaptureBitmap = new Bitmap(capturedBitmap);
        _lastPreprocessedBitmap?.Dispose();
        _lastPreprocessedBitmap = null; // Will be set after preprocessing below

        if (!string.Equals(_runContext.CaptureMethod, captureMethod, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogTrace("OCR capture source active: {CaptureMethod}.", captureMethod);
        }

        _runContext = _runContext with { CaptureMethod = captureMethod };

        // Panel was already confirmed open by the pre-capture check above.
        _metrics.InterfaceDetected = true;

        using (_perf.Measure(OcrPerfTiming.Slot.FrameHash))
        {
            if (TrySkipOcrViaFrameDifferencing(capturedBitmap, options))
            {
                attemptedRecognition = true;
                fromCache = true;
                _runContext = _runContext with { RowYPositions = _lastOcrRowYPositions };
                _perf.RecordEnd(OcrPerfTiming.Slot.Total, t0);
                _perf.RecordEnd(OcrPerfTiming.Slot.CacheHit, t0);
                var totalMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                _metrics.RecordCycle(totalMs, fromCache: true, isFullOcr: false);
                _metrics.CaptureMethod = _runContext.CaptureMethod;
                _metrics.IsPoe2Foreground = true;
                _metrics.InterfaceDetected = true;
                // Record per-cycle slot data so the breakdown stays in sync with Avg Duration
                var cacheCycleSlots = _perf.GetCycleSlotMs();
                for (var s = 0; s < cacheCycleSlots.Length; s++)
                    _metrics.RecordSlotDuration(s, cacheCycleSlots[s]);
                TryLogPerfMetrics(options);
                return _lastOcrText;
            }
        }

        var debugContext = options.SaveDebugImages
            ? TryStartDebugCapture(capturedBitmap, region, captureMethod)
            : null;

        attemptedRecognition = true;
#pragma warning disable CA2000 // Ownership transferred; disposed at method exit via explicit Dispose calls
        Bitmap masked;
        using (_perf.Measure(OcrPerfTiming.Slot.KeepBlack))
            masked = OcrImagePreprocessor.KeepBlackAndNeighbors(capturedBitmap, _logger);
        Bitmap preprocessed;
        using (_perf.Measure(OcrPerfTiming.Slot.Preprocess))
            preprocessed = OcrImagePreprocessor.PreprocessForOcr(masked, options, _logger);
#pragma warning restore CA2000
        _lastPreprocessedBitmap?.Dispose();
        _lastPreprocessedBitmap = new Bitmap(preprocessed);
        Rectangle? crop;
        using (_perf.Measure(OcrPerfTiming.Slot.PostProcess))
            crop = OcrImagePreprocessor.FindContentBounds(preprocessed);

        // Shift the left edge of the scan region from the icon column to the
        // panel text column, so rune icons don't inflate row widths and confuse
        // the row detection.  The left yellow dashed line in the debug overlay marks
        // this boundary.
        if (crop.HasValue && preprocessed.Width > 0)
        {
            var textColX = (int)(preprocessed.Width * options.PanelLeftFraction);
            var newX = Math.Max(crop.Value.X, textColX);
            crop = new Rectangle(newX, crop.Value.Y,
                Math.Max(1, crop.Value.Right - newX), crop.Value.Height);
        }

        var debugDir = debugContext?.DirectoryPath;
        if (debugDir is not null)
        {
            OcrImagePreprocessor.SavePng(capturedBitmap, Path.Combine(debugDir, "1 Raw.png"));
            OcrImagePreprocessor.SavePng(masked, Path.Combine(debugDir, "2 Mask.png"));
            OcrImagePreprocessor.SavePng(preprocessed, Path.Combine(debugDir, "3 Preprocessed.png"));
            if (crop.HasValue)
            {
                using var croppedDbg = OcrImagePreprocessor.CropBitmap(preprocessed, crop.Value);
                OcrImagePreprocessor.SavePng(croppedDbg, Path.Combine(debugDir, "4 Cropped.png"));
            }
        }

        int[] rowYs, rowHeights;
        using (_perf.Measure(OcrPerfTiming.Slot.PostProcess))
            (rowYs, rowHeights) = OcrPipeline.DetectRowPositions(preprocessed, crop);
        _lastOcrRowHeights = rowHeights;
        _lastOcrRowYPositions = rowYs;
        _lastCropBounds = crop;
        _runContext = _runContext with { RowYPositions = rowYs };

        if (rowYs.Length == 0)
        {
            _lastOcrText = string.Empty;
            _lastOcrOptions = options;
            _lastRowTexts = [];
        }
        else
        {
            var rowTexts = new string[rowYs.Length];

            if (IsWindowsOcrEnabled(options))
            {
                EnsureWindowsOcrEngine();
                using (_perf.Measure(OcrPerfTiming.Slot.Recognize))
                {
                    for (var i = 0; i < rowYs.Length; i++)
                    {
                        var rowBitmap = OcrPipeline.PrepareRowBitmap(preprocessed, crop, rowYs[i], rowHeights[i]);
                        try
                        {
                            var rawText = _windowsOcrEngine!.Recognize(rowBitmap, out _, 3, null);
                            var lines = OcrImagePreprocessor.SplitAndTrim(rawText);
                            var cleaned = lines.Length > 0
                                ? (OcrTextPostProcessor.ExtractLikelyItemNames(lines[0], _detectedLanguage) is { Count: > 0 } cl ? cl[0] : lines[0])
                                : string.Empty;
                            _logger.LogTrace("OCR: row {Row} raw='{Raw}' cleaned='{Clean}' lang={Lang}", i, rawText, cleaned, _detectedLanguage);
                            rowTexts[i] = cleaned;
                        }
                        finally
                        {
                            rowBitmap.Dispose();
                        }

                        if (debugDir is not null)
                            OcrPipeline.SaveRowDebugImage(preprocessed, crop, rowYs[i], rowHeights[i], i, debugDir);
                    }
                }
            }
            else
            {
                _activeOcrBackend = "tesseract";
                _metrics.OcrBackend = "tesseract";
                var engine = _engineManager.GetEngine(options)!;
                engine.SetPageSegMode(7); // PSM_SINGLE_LINE

                using (_perf.Measure(OcrPerfTiming.Slot.Recognize))
                {
                    for (var i = 0; i < rowYs.Length; i++)
                    {
                        string? text;
                        try
                        {
                            var rowBitmap = OcrPipeline.PrepareRowBitmap(preprocessed, crop, rowYs[i], rowHeights[i]);
                            try
                            {
                                text = engine.RecognizeSingleLine(rowBitmap);
                            }
                            finally
                            {
                                rowBitmap.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Per-row Tesseract recognition failed for row {Row}/{Total}: {Context}", i + 1, rowYs.Length, ErrorContext.FromException(ex));
                            text = null;
                        }
                        var tessCleaned = text ?? string.Empty;
                        _logger.LogTrace("OCR: row {Row} raw='{Raw}' cleaned='{Clean}' lang={Lang} backend=tesseract", i, text, tessCleaned, _detectedLanguage);
                        rowTexts[i] = tessCleaned;

                        if (debugDir is not null)
                            OcrPipeline.SaveRowDebugImage(preprocessed, crop, rowYs[i], rowHeights[i], i, debugDir);
                    }
                }
            }

            if (debugDir is not null)
                OcrPipeline.SaveRowOverlayDebugImage(preprocessed, crop, rowYs, rowHeights, debugDir);

            string joined;
            using (_perf.Measure(OcrPerfTiming.Slot.PostProcess))
                joined = string.Join(Environment.NewLine, rowTexts);
            _lastOcrText = joined;
            _lastOcrOptions = options;
            _lastRowTexts = rowTexts;
        }

        _perf.RecordEnd(OcrPerfTiming.Slot.Total, t0);
        var fullOcrMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _metrics.RecordCycle(fullOcrMs, fromCache: false, isFullOcr: true);
        _metrics.CaptureMethod = _runContext.CaptureMethod;
        _metrics.IsPoe2Foreground = true;
        _metrics.InterfaceDetected = true;
        var cycleSlots = _perf.GetCycleSlotMs();
        for (var s = 0; s < cycleSlots.Length; s++)
            _metrics.RecordSlotDuration(s, cycleSlots[s]);
        TryLogPerfMetrics(options);

        _retryRegions.Clear();
        _rejectedRegions.Clear();
        masked.Dispose();
        preprocessed.Dispose();
        return _lastOcrText;
    }

    // (Per-row OCR pipeline moved to OcrPipeline.cs)

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

    private void OnPoe2ConfigChanged()
    {
        var rawLang = Poe2ConfigFile.Language;
        var detectedLang = rawLang is not null && ItemNameTranslator.IsLanguageSupported(rawLang) ? rawLang : null;
        if (detectedLang is null && _detectedLanguage is null) return; // still unsupported
        var effective = _detectedLanguage ?? _options.CurrentValue.Language;
        if (string.Equals(detectedLang, effective, StringComparison.OrdinalIgnoreCase))
            return; // no change

        _logger.LogInformation("PoE2 config language changed from '{Old}' to '{New}' — reinitializing OCR engine.",
            effective, detectedLang);

        _detectedLanguage = detectedLang;
        _activeOcrLanguage = null; // force engine re-init on next cycle
        _lastOcrText = "";
        _lastOcrOptions = null;
        _logState &= ~OcrLogState.UnsupportedLanguage; // re-evaluate on next cycle
    }

    private void EnsureWindowsOcrEngine()
    {
        var lang = _detectedLanguage ?? _options.CurrentValue.Language;
        var backend = ResolveEffectiveOcrBackend(_options.CurrentValue.OcrBackend);
        if (_activeOcrBackend == backend && string.Equals(_activeOcrLanguage, lang, StringComparison.OrdinalIgnoreCase) && _windowsOcrEngine is not null)
        {
            // Periodically retry engine creation (every ~15s) so that if the user
            // installed the language pack after we created a fallback engine, we
            // pick it up on the next retry without requiring an app restart.
            if (_lastWindowsOcrRetryUtc is null ||
                (DateTime.UtcNow - _lastWindowsOcrRetryUtc.Value).TotalSeconds >= 15)
            {
                _lastWindowsOcrRetryUtc = DateTime.UtcNow;
                TryRecreateWindowsOcrEngine(lang, backend);
            }
            return;
        }

        _windowsOcrEngine?.Dispose();
        _windowsOcrEngine = new WindowsOcrEngine(lang, _logger);
        _activeOcrBackend = backend;
        _activeOcrLanguage = lang;
        _metrics.OcrBackend = backend;
        _logger.LogInformation("OCR backend: Windows.Media.Ocr ({Lang})", lang);
    }

    private DateTime? _lastWindowsOcrRetryUtc;

    private void TryRecreateWindowsOcrEngine(string lang, string backend)
    {
        try
        {
            var newEngine = new WindowsOcrEngine(lang, _logger);
            _windowsOcrEngine?.Dispose();
            _windowsOcrEngine = newEngine;
            _activeOcrBackend = backend;
            _activeOcrLanguage = lang;
            _logger.LogInformation("OCR backend reinitialized with exact language pack for '{Lang}'", lang);
        }
        catch
        {
            // Pack still not available — keep the existing fallback engine
        }
    }

    private bool TrySkipOcrViaFrameDifferencing(Bitmap bitmap, OcrOptions currentOptions)
    {
        if (_options.CurrentValue.BypassOcrCache)
            return false;

        // When the options object reference changes (meaning IOptionsMonitor created
        // a new instance after a config reload), invalidate the cache so new values
        // (tolerance, thresholds, etc.) take effect immediately.
        if (!ReferenceEquals(_lastOcrOptions, currentOptions))
            return false;

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
                    Marshal.Copy(data.Scan0 + (y * stride), rowBytes, 0, stride);
                    for (var x = 0; x < data.Width; x += stepX)
                    {
                        var idx = x * bpp;
                        if (idx + 2 < stride)
                            hash = (hash * 31) + rowBytes[idx] + rowBytes[idx + 1] + rowBytes[idx + 2];
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

    private bool TryDetectPanelOpen(Bitmap capturedBitmap, OcrOptions options, OcrCaptureRegion region)
    {
        bool panelOpen = _listDetector.Update(capturedBitmap, out var diag);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var pxFormat = capturedBitmap.PixelFormat.ToString();
            _logger.LogDebug(
                "Panel check: region=({RegX},{RegY} {RegW}x{RegH}) scanX={ScanX0}-{ScanX1} scanY={ScanY} fmt={Fmt} open={Open}",
                region.X, region.Y, region.Width, region.Height,
                region.X + (int)(region.Width * _options.CurrentValue.PanelLeftFraction),
                region.X + (int)(region.Width * _options.CurrentValue.PanelRightFraction),
                region.Y + (int)(region.Height * _options.CurrentValue.PanelTopRowFraction),
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
                    var dir = Path.Combine(AppContext.BaseDirectory, "ocr", GetDebugImageSubdir(options));
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

    private static Bitmap? CaptureDesktopRegionDirect(OcrCaptureRegion region)
    {
        try
        {
            var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private DebugCaptureContext? TryStartDebugCapture(Bitmap rawImage, OcrCaptureRegion region, string captureMethod)
    {
        var options = _options.CurrentValue;
        // Allow the configured interval, but never more than once per second.
        var minIntervalSeconds = Math.Max(1, options.DebugImageIntervalSeconds);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastDebugImageSavedAtUtc < TimeSpan.FromSeconds(minIntervalSeconds))
            return null;

        // Only save when the frame content has actually changed (different hash).
        var frameHash = ComputeFastFrameHash(rawImage);
        if (frameHash == _lastDebugFrameHash)
            return null;
        _lastDebugFrameHash = frameHash;

        _lastDebugImageSavedAtUtc = now;

        var directory = ResolveDebugImageDirectory(options);

        try
        {
            _ = Directory.CreateDirectory(directory);
            if (!_logState.HasFlag(OcrLogState.DebugDirectoryLogged))
            {
                _logState |= OcrLogState.DebugDirectoryLogged;
                _logger.LogInformation("OCR debug image output enabled. Directory: {Path}", SanitizePathForLog(Path.GetFullPath(directory)));
            }

            var rawPath = Path.Combine(directory, "raw.png");

            OcrCaptureStrategy.SaveBitmapWithOverwrite(rawImage, rawPath);

            _logger.LogInformation(
                "Saved OCR debug images. Method={Method} Region=X={X} Y={Y} W={W} H={H}",
                captureMethod,
                region.X,
                region.Y,
                region.Width,
                region.Height);

            return new DebugCaptureContext(directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save OCR debug image to {Dir}: {Context}", directory, ErrorContext.FromException(ex));
            return null;
        }
    }

    private static string GetDebugImageSubdir(OcrOptions options)
    {
        var isWindows = string.Equals(options.OcrBackend, "windows", StringComparison.OrdinalIgnoreCase);
        return isWindows ? Path.Combine("windows", "images") : Path.Combine("tesseract", "images");
    }

    private static string SanitizePathForLog(string path)
    {
        // Current user's LocalAppData — %LOCALAPPDATA% is always accurate
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData) && path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
            return "%LOCALAPPDATA%" + path[localAppData.Length..];

        // Current user's profile — %USERPROFILE% is accurate here
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path[userProfile.Length..];

        // Path under a different user's profile — show relative portion only
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var profilesDir = systemDrive is not null
            ? Path.Combine(systemDrive, "Users")
            : @"C:\Users";
        if (path.StartsWith(profilesDir, StringComparison.OrdinalIgnoreCase))
        {
            var afterProfiles = path[(profilesDir.Length + 1)..];
            var firstSep = afterProfiles.IndexOf('\\');
            if (firstSep > 0)
                return afterProfiles[(firstSep + 1)..];
        }

        return path;
    }

    private static string ResolveDebugImageDirectory(OcrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DebugImageDirectory))
        {
            return Path.Combine(AppContext.BaseDirectory, "ocr", GetDebugImageSubdir(options));
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
                _logger.LogInformation("OCR debug image output enabled. Directory: {Path}", SanitizePathForLog(Path.GetFullPath(directory)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OCR debug image directory {Path}: {Context}", SanitizePathForLog(directory), ErrorContext.FromException(ex));
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
        _windowsOcrEngine?.Dispose();
        _lastCaptureBitmap?.Dispose();
        _lastPreprocessedBitmap?.Dispose();
        _engineManager.Dispose();
    }

    private void TryLogPerfMetrics(OcrOptions options)
    {
        var interval = options.PerfMetricsInterval;
        if (interval <= 0) return;
        var now = DateTime.UtcNow;
        if ((now - _lastPerfMetricsLogAt).TotalSeconds < interval) return;
        _lastPerfMetricsLogAt = now;

        var snap = _metrics.GetSnapshot();
        // Write to a per-instance temp file so the performance script can read it
        // (the app is WinExe with no console, so stdout is unavailable).
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "rpc-perf-metrics.txt");
            var line = $"[Perf] OcrBackend={snap.OcrBackend} " +
                $"Avg={snap.AverageScanDurationMs:F1}ms " +
                $"Uncached={snap.AverageUncachedDurationMs:F1}ms " +
                $"Cached={snap.AverageCachedDurationMs:F1}ms " +
                $"CacheRate={snap.CacheHitRate:F1}% " +
                $"Scans/s={snap.ScansPerSecond:F1}" +
                Environment.NewLine;
            File.AppendAllText(path, line);
        }
        catch
        {
            // Best-effort; benchmarking will miss a sample but can continue.
        }
    }

    private static string[] BuildItemDebugStrings(string[] lines, int[] yPositions)
    {
        var result = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            result[i] = i < yPositions.Length ? $"{lines[i]} @Y={yPositions[i]}" : lines[i];
        return result;
    }


}
