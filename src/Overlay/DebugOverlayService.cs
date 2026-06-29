using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;

namespace RuneshapePriceChecker.Overlay;

public sealed class DebugOverlayService(
    IPoe2WindowResolutionProvider windowResolutionProvider,
    IOptionsMonitor<OcrOptions> ocrOptions,
    IOptionsMonitor<WindowOptions> windowOptions,
    DashboardService dashboard,
    IOptionsMonitor<AppOptions> appOptions,
    ILogger<DebugOverlayService> logger) : BackgroundService
{
    private readonly IOptionsMonitor<OcrOptions> _options = ocrOptions;
    private readonly IOptionsMonitor<WindowOptions> _windowOptions = windowOptions;
    private readonly IOptionsMonitor<AppOptions> _appOptions = appOptions;
    private Thread? _overlayThread;
    private BoundsOverlayForm? _overlayForm;
    private readonly object _overlaySync = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            try
            {
                if (options.DebugOverlay)
                {
                    EnsureOverlayThreadStarted();
                    if (_frameDirty)
                    {
                        _frameDirty = false;
                        RefreshOverlayFrame(options);
                    }
                }
                else CloseOverlay();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh OCR bounds overlay: {Context} (region={R} forceHidden={FH} setup={S})",
                    ErrorContext.FromException(ex),
                    windowResolutionProvider.CurrentCaptureRegion?.ToString() ?? "null", _forceHidden, _setupInProgress);
            }

            await Task.Delay(16, stoppingToken).ConfigureAwait(false);
        }

        CloseOverlay();
    }

    private void RefreshOverlayFrame(OcrOptions options)
    {
        var overlay = GetOverlayForm();
        if (overlay is null) return;
        if (!options.DebugOverlay)
        {
            overlay.SafeHide();
            return;
        }

        var region = windowResolutionProvider.CurrentCaptureRegion;
        if (region is null)
        {
            overlay.SafeHide();
            return;
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            overlay.SafeHide();
            return;
        }

        if (!options.HideDebugOverlayWhenInterfaceNotDetected)
        {
            _forceHidden = false;
            _wasHidden = false;
        }

        if (_forceHidden || _setupInProgress)
        {
            overlay.SafeHide();
            return;
        }

        if (_appOptions.CurrentValue.AllOverlaysDisabled)
        {
            overlay.SafeHide();
            return;
        }

        var frame = new Rectangle(region.X, region.Y, region.Width, region.Height);
        var scaleFactor = PricingOverlayRenderer.ComputeOverlayScale(windowResolutionProvider, _options.CurrentValue.OverlayScale);
        overlay.SetScanFractions(options.PanelLeftFraction, options.PanelRightFraction, options.PanelTopRowFraction);
        overlay.SafeShowFrame(frame, true, scaleFactor: scaleFactor);
    }

    private void EnsureOverlayThreadStarted()
    {
        lock (_overlaySync)
        {
            if (_overlayThread is { IsAlive: true }) return;
            _overlayThread = new Thread(() =>
            {
                using var form = new BoundsOverlayForm();
                lock (_overlaySync) _overlayForm = form;
                Application.Run(form);

                lock (_overlaySync) _overlayForm = null;
            })
            {
                IsBackground = true,
                Name = "RuneshapePriceChecker-OcrBoundsOverlay"
            };

            _overlayThread.SetApartmentState(ApartmentState.STA);
            _overlayThread.Start();
        }
    }

    private BoundsOverlayForm? GetOverlayForm()
    {
        lock (_overlaySync) return _overlayForm;
    }

    private void CloseOverlay()
    {
        var overlay = GetOverlayForm();
        overlay?.SafeClose();
    }

    private bool _wasHidden;
    private volatile bool _forceHidden;
    private volatile bool _setupInProgress;
    private volatile bool _frameDirty;
    private volatile bool _setupComplete; // in-memory guard until IOptionsMonitor picks up the file change
    public bool IsSetupInProgress => _setupInProgress;

    public void ForceHide()
    {
        if (_forceHidden)
            return;

        _forceHidden = true;
        GetOverlayForm()?.SafeHide();
    }

    public bool NeedsInitialSetup()
    {
        return !_setupComplete && !_windowOptions.CurrentValue.InitialSetupComplete;
    }

    public void RunInitialSetup()
    {
        if (_setupInProgress) return;
        _setupInProgress = true;
        _setupComplete = false;

        logger.LogInformation("RunInitialSetup: starting initial setup flow");
        var region = windowResolutionProvider.CurrentCaptureRegion;
        Rectangle gameBounds;

        if (region is { } r)
        {
            var ctx = windowResolutionProvider.CurrentWindowCaptureContext;
            if (ctx is not null) gameBounds = new Rectangle(ctx.ClientX, ctx.ClientY, ctx.ClientWidth, ctx.ClientHeight);
            else
            {
                var screen = Screen.FromPoint(new Point(r.X, r.Y));
                gameBounds = screen.Bounds;
            }
        }
        else
        {
            var screen = Screen.PrimaryScreen;
            if (screen is null)
            {
                logger.LogError("Setup: no screen available.");
                _setupInProgress = false;
                return;
            }
            gameBounds = screen.Bounds;
            r = new OcrCaptureRegion(gameBounds.X + 100, gameBounds.Y + 100, 400, 500);
            logger.LogInformation("Setup: PoE2 not detected, using primary screen bounds as fallback.");
        }

        var initialRect = new Rectangle(r.X, r.Y, r.Width, r.Height);

        ForceHide();

        var setupThread = new Thread(() =>
        {
            RunSetupFlow(initialRect, gameBounds);
        })
        {
            IsBackground = true,
            Name = "RuneshapePriceChecker-Setup"
        };
        setupThread.SetApartmentState(ApartmentState.STA);
        setupThread.Start();
    }

    private void RunSetupFlow(Rectangle initialRect, Rectangle gameBounds)
    {
        logger.LogInformation("RunSetupFlow: entering setup loop");
        while (true)
        {
            using var continueClicked = new ManualResetEventSlim(false);
            dashboard.SetOnSetupContinue(continueClicked.Set);
            logger.LogInformation("RunSetupFlow: showing Continue prompt in Dashboard");
            dashboard.ShowSetupPrompt();

            logger.LogInformation("RunSetupFlow: waiting for user to click Continue...");
            continueClicked.Wait();
            logger.LogInformation("RunSetupFlow: user clicked Continue");

            dashboard.HideSetupPrompt();

            using var overlayForm = new SetupOverlayForm(initialRect, gameBounds);
            var goBack = false;

            overlayForm.SetupConfirmed += SaveCustomOffsets;
            overlayForm.GoBackClicked += () => goBack = true;
            overlayForm.Disposed += (_, _) =>
            {
                _setupInProgress = false;
                _forceHidden = false;
            };

            Application.Run(overlayForm);

            dashboard.BringToFront();

            if (!goBack) break;
        }
    }

    private void SaveCustomOffsets(Rectangle rect)
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
            if (!File.Exists(configPath)) return;

            var json = File.ReadAllText(configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            if (root is null) return;

            var ctx = windowResolutionProvider.CurrentWindowCaptureContext;
            if (ctx is null)
            {
                logger.LogError("Setup: cannot save custom offsets — window capture context is no longer available.");
                return;
            }

            var relX = rect.X - ctx.ClientX;
            var relY = rect.Y - ctx.ClientY;

            var windowNode = root["Window"] as JsonObject ?? [];
            windowNode["CustomOffsetX"] = relX;
            windowNode["CustomOffsetY"] = relY;
            windowNode["CustomWidth"] = rect.Width;
            windowNode["CustomHeight"] = rect.Height;
            windowNode["InitialSetupComplete"] = true;
            root["Window"] = windowNode;

            File.WriteAllText(configPath, root.ToJsonString(new() { WriteIndented = true }) + Environment.NewLine, Encoding.UTF8);

            _setupComplete = true;
            logger.LogInformation("SaveCustomOffsets: setup confirmed, offsets saved (X={RelX} Y={RelY} W={W} H={H})", relX, relY, rect.Width, rect.Height);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save custom offsets: {Context} (had {Count} screens)", ErrorContext.FromException(ex), Screen.AllScreens.Length);
        }
    }

    public void SetDebugText(IReadOnlyList<string> lines, IReadOnlyList<int>? rowYPositions = null, bool interfaceDetected = true, string? statusLine = null, Rectangle? cropBounds = null, IReadOnlyList<Rectangle>? retryRegions = null, IReadOnlyList<string>? translatedLines = null, IReadOnlyList<Rectangle>? rejectedRegions = null)
    {
        if (_setupInProgress) return;

        var overlay = GetOverlayForm();
        if (overlay is null) return;

        if (_options.CurrentValue.HideDebugOverlayWhenInterfaceNotDetected)
        {
            if (!interfaceDetected)
            {
                if (!_wasHidden) { _wasHidden = true; logger.LogTrace("Debug overlay HIDDEN: interface not detected."); }
                _forceHidden = true;
                overlay.SafeHide();
                return;
            }

            _forceHidden = false;
            if (_wasHidden)
            {
                _wasHidden = false;
                logger.LogTrace("Debug overlay SHOWN: interface detected.");
            }
        }
        else
        {
            _forceHidden = false;
            _wasHidden = false;
        }

        _frameDirty = true;
        overlay.SetStatusLine(statusLine);
        overlay.SetDebugLines(lines, rowYPositions);
        if (translatedLines is not null)
            overlay.SetTranslatedLines(translatedLines);
        overlay.SetCropBounds(cropBounds);
        overlay.SetRetryRegions(retryRegions ?? []);
        overlay.SetRejectedRegions(rejectedRegions ?? []);

    }

    private sealed class BoundsOverlayForm : OverlayFormBase
    {
        protected override bool ClickThrough => true;

        private const float BaseFontSizePx = 14f;
        private const int BaseTextPanelWidth = 400;
        private const int BaseDebugGap = 120;
        private const int BaseDefaultLineHeight = 16;
        private const int BgPadding = 30;

        private int _textPanelWidth = BaseTextPanelWidth;
        private int _debugGap = BaseDebugGap;
        private int _defaultLineHeight = BaseDefaultLineHeight;
        private float _scaleFactor = 1f;
        private double _leftFraction = LeaguePanelDetector.DefaultLeftFraction;
        private double _rightFraction = LeaguePanelDetector.DefaultRightFraction;
        private double _topRowFraction = LeaguePanelDetector.DefaultTopRowFraction;
        private Rectangle _frame;
        private IReadOnlyList<string> _debugLines = [];
        private IReadOnlyList<string> _debugTranslatedLines = [];
        private IReadOnlyList<int> _debugRowY = [];
        private IReadOnlyList<Rectangle> _retryRegions = [];
        private IReadOnlyList<Rectangle> _rejectedRegions = [];
        private bool _showDebugOverlay;
        private string? _statusLine;
        private Rectangle? _cropBounds;
        private Font _statusFont = new("Segoe UI", BaseFontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
        private Font _debugFont = new("Segoe UI", BaseFontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
        private static readonly Pen BoxPen = new(Color.Red, 3);
        private static readonly Pen CropPen = new(Color.FromArgb(200, 0, 255, 0), 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        private static readonly Pen RetryPen = new(Color.FromArgb(220, 200, 50, 220), 2f);
        private static readonly Pen RejectedPen = new(Color.FromArgb(200, 220, 120, 30), 2f);
        private static readonly Pen ScanPen = new(Color.FromArgb(220, 255, 255, 0), 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 1 || Height <= 1)
                return;

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var debugWidth = _showDebugOverlay ? _textPanelWidth + BgPadding : 0;
            var debugGap = _showDebugOverlay ? _debugGap : 0;
            var boxWidth = Width - debugWidth - debugGap;
            var debugX = boxWidth + debugGap;

            if (!_showDebugOverlay)
                return;

            e.Graphics.DrawRectangle(BoxPen, 0, 1, boxWidth - 1, _frame.Height - 1);

            // Content-aware crop region (green dashed)
            if (_cropBounds is { } crop && crop.Width > 0 && crop.Height > 0) e.Graphics.DrawRectangle(CropPen, crop.X, crop.Y, crop.Width - 1, crop.Height - 1);
            // Per-line retry regions (purple dashed boxes)
            foreach (var retry in _retryRegions)
            {
                if (retry.Width > 0 && retry.Height > 0)
                    e.Graphics.DrawRectangle(RetryPen, retry.X, retry.Y, retry.Width - 1, retry.Height - 1);
            }
            // Rejected rune-icon rows (black dashed boxes)
            foreach (var rejected in _rejectedRegions)
            {
                if (rejected.Width > 0 && rejected.Height > 0)
                    e.Graphics.DrawRectangle(RejectedPen, rejected.X, rejected.Y, rejected.Width - 1, rejected.Height - 1);
            }

            var scanLeft = (int)(boxWidth * _leftFraction);
            // Left vertical yellow dashed line — marks the text-column boundary
            // used for row detection. Full height so the cutoff is always visible.
            e.Graphics.DrawLine(ScanPen, scanLeft, 0, scanLeft, _frame.Height);

            var lines = _debugLines;
            var rowY = _debugRowY;

            if (_statusLine is { } status)
            {
                var displayText = _scaleFactor > 1.01f
                    ? $"{status}  |  Scale ×{_scaleFactor:F2}"
                    : status;
                var statusSize = TextRenderer.MeasureText(e.Graphics, displayText, _statusFont);
                var statusX = (boxWidth - statusSize.Width) / 2;
                var statusY = _frame.Height + 4;
                TextRenderer.DrawText(e.Graphics, displayText, _statusFont,
                    new Point(statusX, statusY), Color.FromArgb(220, 255, 255, 100), Color.Black,
                    TextFormatFlags.NoPadding);
            }

            if (lines.Count > 0)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    var rowTop = i < rowY.Count ? rowY[i] : 6 + (i * _defaultLineHeight);
                    rowTop = Math.Clamp(rowTop, 0, Height - _defaultLineHeight);
                    var x = debugX + 78;

                    // Look up the matching purple box height to center text vertically
                    var rowH = _defaultLineHeight;
                    if (i < rowY.Count)
                    {
                        foreach (var r in _retryRegions)
                        {
                            if (r.Y == rowY[i])
                            {
                                rowH = r.Height;
                                break;
                            }
                        }
                    }
                    var textSize = TextRenderer.MeasureText(e.Graphics, lines[i], _debugFont);
                    var y = rowTop + ((rowH - textSize.Height) / 2);
                    y = Math.Clamp(y, 0, Height - textSize.Height);

                    TextRenderer.DrawText(e.Graphics, lines[i], _debugFont,
                        new Point(x, y), Color.Red, Color.Black,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);

                    // Translated text in purple beneath the OCR-detected line
                    if (i < _debugTranslatedLines.Count)
                    {
                        var translated = _debugTranslatedLines[i];
                        if (!string.IsNullOrWhiteSpace(translated) &&
                            !string.Equals(translated, lines[i], StringComparison.OrdinalIgnoreCase))
                        {
                            var ty = rowTop + rowH + 2;
                            ty = Math.Clamp(ty, 0, Height - _defaultLineHeight);
                            TextRenderer.DrawText(e.Graphics, translated, _debugFont,
                                new Point(x, ty), Color.Purple, Color.Black,
                                TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                        }
                    }
                }
            }

        }

        public void SetDebugLines(IReadOnlyList<string> lines, IReadOnlyList<int>? rowY = null)
        {
            _debugLines = lines;
            _debugRowY = rowY ?? [];
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetTranslatedLines(IReadOnlyList<string> lines)
        {
            _debugTranslatedLines = lines;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetCropBounds(Rectangle? crop)
        {
            _cropBounds = crop;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetRetryRegions(IReadOnlyList<Rectangle> regions)
        {
            _retryRegions = regions;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetRejectedRegions(IReadOnlyList<Rectangle> regions)
        {
            _rejectedRegions = regions;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetStatusLine(string? status)
        {
            _statusLine = status;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetScanFractions(double left, double right, double topRow)
        {
            _leftFraction = left;
            _rightFraction = right;
            _topRowFraction = topRow;
        }

        public void SafeShowFrame(Rectangle frame, bool showOverlay, int panelWidth = 400, float scaleFactor = 1f)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                IsHidden = false;
                _ = BeginInvoke(new Action<Rectangle, bool, int, float>(SafeShowFrame), frame, showOverlay, panelWidth, scaleFactor);
                return;
            }

            IsHidden = false;
            ApplyScaleFactor(scaleFactor);

            _showDebugOverlay = showOverlay;
            _textPanelWidth = panelWidth;
            var extraWidth = showOverlay ? _textPanelWidth + BgPadding + _debugGap : 0;
            var statusHeight = _statusLine is not null ? (int)Math.Round(30 * scaleFactor) : 0;
            var fullFrame = new Rectangle(
                frame.X,
                frame.Y,
                frame.Width + extraWidth,
                frame.Height + statusHeight);

            _frame = frame;
            Bounds = fullFrame;
            Invalidate();

            PinTopMost();

            if (!Visible)
            {
                Show();
                PinTopMost();
            }
        }

        private void ApplyScaleFactor(float scaleFactor)
        {
            if (Math.Abs(_scaleFactor - scaleFactor) < 0.01f)
                return;

            _scaleFactor = scaleFactor;
            var newSize = (float)Math.Round(BaseFontSizePx * scaleFactor);
            var oldStatusFont = _statusFont;
            var oldDebugFont = _debugFont;
            _statusFont = new Font("Segoe UI", newSize, FontStyle.Bold, GraphicsUnit.Pixel);
            _debugFont = new Font("Segoe UI", newSize, FontStyle.Bold, GraphicsUnit.Pixel);
            oldStatusFont.Dispose();
            oldDebugFont.Dispose();

            _textPanelWidth = (int)Math.Round(BaseTextPanelWidth * scaleFactor);
            _debugGap = (int)Math.Round(BaseDebugGap * scaleFactor);
            _defaultLineHeight = (int)Math.Round(BaseDefaultLineHeight * scaleFactor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _statusFont.Dispose();
                _debugFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
