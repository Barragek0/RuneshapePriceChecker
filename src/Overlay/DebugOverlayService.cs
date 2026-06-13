using System.Runtime.InteropServices;
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
    ILogger<DebugOverlayService> logger) : BackgroundService
{
    private readonly IOptionsMonitor<OcrOptions> _options = ocrOptions;
    private readonly IOptionsMonitor<WindowOptions> _windowOptions = windowOptions;
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
                    RefreshOverlayFrame(options);
                }
                else
                {
                    CloseOverlay();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh OCR bounds overlay.");
            }

            var intervalMs = Math.Max(100, OcrConstants.CaptureBoundsOverlayIntervalMs);
            await Task.Delay(TimeSpan.FromMilliseconds(intervalMs), stoppingToken).ConfigureAwait(false);
        }

        CloseOverlay();
    }

    private void RefreshOverlayFrame(OcrOptions options)
    {
        var overlay = GetOverlayForm();
        if (overlay is null)
        {
            return;
        }

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

        var frame = new Rectangle(region.X, region.Y, region.Width, region.Height);
        overlay.SafeShowFrame(frame, true);
    }

    private void EnsureOverlayThreadStarted()
    {
        lock (_overlaySync)
        {
            if (_overlayThread is { IsAlive: true })
            {
                return;
            }

            _overlayThread = new Thread(() =>
            {
                using var form = new BoundsOverlayForm();
                lock (_overlaySync)
                {
                    _overlayForm = form;
                }

                Application.Run(form);

                lock (_overlaySync)
                {
                    _overlayForm = null;
                }
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
        lock (_overlaySync)
        {
            return _overlayForm;
        }
    }

    private void CloseOverlay()
    {
        var overlay = GetOverlayForm();
        overlay?.SafeClose();
    }

    private bool _wasHidden;
    private volatile bool _forceHidden;
    private volatile bool _setupInProgress;
    public bool IsSetupInProgress => _setupInProgress;
    private Thread? _bannerThread;
    private BannerForm? _bannerForm;
    private readonly object _bannerSync = new();

    public void ForceHide()
    {
        if (_forceHidden)
            return;

        _forceHidden = true;
        GetOverlayForm()?.SafeHide();
    }

    public bool NeedsInitialSetup()
    {
        return !_windowOptions.CurrentValue.InitialSetupComplete;
    }

    public void RunInitialSetup()
    {
        if (_setupInProgress) return;
        _setupInProgress = true;

        logger.LogInformation("RunInitialSetup: starting initial setup flow");
        var region = windowResolutionProvider.CurrentCaptureRegion;
        Rectangle gameBounds;

        if (region is { } r)
        {
            var ctx = windowResolutionProvider.CurrentWindowCaptureContext;
            if (ctx is not null)
            {
                gameBounds = new Rectangle(ctx.ClientX, ctx.ClientY, ctx.ClientWidth, ctx.ClientHeight);
            }
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
            var continueClicked = new ManualResetEventSlim(false);
            dashboard.SetOnSetupContinue(() => continueClicked.Set());
            logger.LogInformation("RunSetupFlow: showing Continue prompt in Dashboard");
            dashboard.ShowSetupPrompt();

            logger.LogInformation("RunSetupFlow: waiting for user to click Continue...");
            continueClicked.Wait();
            logger.LogInformation("RunSetupFlow: user clicked Continue");

            dashboard.HideSetupPrompt();

            using var overlayForm = new SetupOverlayForm(initialRect, gameBounds);
            var goBack = false;

            overlayForm.SetupConfirmed += rect =>
            {
                SaveCustomOffsets(rect);
            };
            overlayForm.GoBackClicked += () => goBack = true;
            overlayForm.Disposed += (_, _) =>
            {
                _setupInProgress = false;
                _forceHidden = false;
            };

            Application.Run(overlayForm);

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

            var windowNode = root["Window"] as JsonObject ?? new JsonObject();
            windowNode["CustomOffsetX"] = relX;
            windowNode["CustomOffsetY"] = relY;
            windowNode["CustomWidth"] = rect.Width;
            windowNode["CustomHeight"] = rect.Height;
            windowNode["InitialSetupComplete"] = true;
            root["Window"] = windowNode;

            File.WriteAllText(configPath, root.ToJsonString(new() { WriteIndented = true }) + Environment.NewLine, Encoding.UTF8);

            logger.LogInformation("SaveCustomOffsets: setup confirmed, offsets saved (X={RelX} Y={RelY} W={W} H={H})", relX, relY, rect.Width, rect.Height);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save custom offsets.");
        }
    }

    public void SetBannerMessage(string? message)
    {
        if (!_options.CurrentValue.ShowBanner)
            return;

        if (string.IsNullOrWhiteSpace(message))
        {
            var form = GetBannerForm();
            if (form is { IsDisposed: false, Visible: true })
            {
                logger.LogDebug("Banner hidden (no unpriceable items).");
            }
            form?.SafeHide();
            return;
        }

        EnsureBannerThreadStarted();
        var banner = GetBannerForm();
        if (banner is null)
        {
            logger.LogWarning("Banner form is null after thread start.");
            return;
        }

        var region = windowResolutionProvider.CurrentCaptureRegion;
        if (region is null)
        {
            logger.LogDebug("Banner hidden (no capture region).");
            banner.SafeHide();
            return;
        }

        var x = region.X - 55;
        var y = region.Y - 60;
        banner.SetMessage(message);
        banner.SafeShow(x, y);

        if (!banner.Visible)
        {
            logger.LogDebug("Banner shown at ({X},{Y}): {Message}", x, y, message);
        }
    }

    private void EnsureBannerThreadStarted()
    {
        lock (_bannerSync)
        {
            if (_bannerThread is { IsAlive: true }) return;

            _bannerThread = new Thread(() =>
            {
                using var form = new BannerForm();
                var _ = form.Handle;
                lock (_bannerSync) { _bannerForm = form; Monitor.PulseAll(_bannerSync); }
                Application.Run(form);
                lock (_bannerSync) { _bannerForm = null; }
            })
            {
                IsBackground = true,
                Name = "RuneshapePriceChecker-Banner"
            };
            _bannerThread.SetApartmentState(ApartmentState.STA);
            _bannerThread.Start();

            while (_bannerForm is null)
            {
                if (!Monitor.Wait(_bannerSync, TimeSpan.FromSeconds(5)))
                {
                    logger.LogWarning("Banner form creation timed out; banner will be unavailable.");
                    return;
                }
            }
        }
    }

    private BannerForm? GetBannerForm()
    {
        lock (_bannerSync) { return _bannerForm; }
    }

    public void SetDebugText(IReadOnlyList<string> lines, IReadOnlyList<int>? rowYPositions = null, bool interfaceDetected = true, string? statusLine = null)
    {
        if (_setupInProgress) return;

        var overlay = GetOverlayForm();
        if (overlay is null) return;

        if (_options.CurrentValue.HideDebugOverlayWhenInterfaceNotDetected)
        {
            if (!interfaceDetected)
            {
                if (!_wasHidden) { _wasHidden = true; logger.LogInformation("Debug overlay HIDDEN: interface not detected."); }
                _forceHidden = true;
                overlay.SafeHide();
                return;
            }

            _forceHidden = false;
            if (_wasHidden)
            {
                _wasHidden = false;
                logger.LogInformation("Debug overlay SHOWN: interface detected.");
            }
        }
        else
        {
            _forceHidden = false;
            _wasHidden = false;
        }

        overlay.SetStatusLine(statusLine);
        overlay.SetDebugLines(lines.ToArray(), rowYPositions?.ToArray());
    }

    private sealed class BoundsOverlayForm : Form
    {
        private static readonly Color TransparencyChroma = Color.FromArgb(1, 2, 3);
        private int _textPanelWidth = 400;
        private const int DebugGap = 120;
        private const int BgPadding = 30;
        private Rectangle _frame;
        private string[] _debugLines = [];
        private int[] _debugRowY = [];
        private bool _showDebugOverlay;
        private volatile bool _isHidden = true;
        private string? _statusLine;

        public BoundsOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = TransparencyChroma;
            TransparencyKey = TransparencyChroma;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;
                cp.ExStyle |= 0x00080000;
                cp.ExStyle |= 0x00000020;
                cp.ExStyle |= 0x08000000;
                return cp;
            }
        }

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
            var debugGap = _showDebugOverlay ? DebugGap : 0;
            var boxWidth = Width - debugWidth - debugGap;
            var debugX = boxWidth + debugGap;

            if (!_showDebugOverlay)
                return;

            using var pen = new Pen(Color.Red, 3);
            e.Graphics.DrawRectangle(pen, 0, 1, boxWidth - 1, _frame.Height - 1);

            var scanLeft = (int)(boxWidth * ListDetector.LeftFraction);
            var scanRight = (int)(boxWidth * ListDetector.RightFraction);
            var ry = (int)(_frame.Height * ListDetector.TopRowFraction);

            using var scanPen = new Pen(Color.FromArgb(220, 255, 255, 0), 2f)
            {
                DashStyle = System.Drawing.Drawing2D.DashStyle.Dash
            };
            e.Graphics.DrawLine(scanPen, scanLeft, 0, scanLeft, ry);
            e.Graphics.DrawLine(scanPen, scanRight, 0, scanRight, ry);

            using var dotBrush = new SolidBrush(Color.FromArgb(255, 255, 60, 60));
            e.Graphics.FillEllipse(dotBrush, scanLeft - 3, ry - 3, 7, 7);
            e.Graphics.FillEllipse(dotBrush, scanRight - 3, ry - 3, 7, 7);

            var lines = _debugLines;
            var rowY = _debugRowY;

            if (_statusLine is { } status)
            {
                using var statusFont = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var statusBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 100));
                using var statusOutline = new Pen(Color.FromArgb(220, 0, 0, 0), 2f)
                {
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round
                };
                var statusSize = e.Graphics.MeasureString(status, statusFont, PointF.Empty, StringFormat.GenericTypographic);
                var statusX = (boxWidth - (int)statusSize.Width) / 2f;
                var statusY = _frame.Height + 4f;
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddString(status, statusFont.FontFamily, (int)statusFont.Style,
                    e.Graphics.DpiY * statusFont.SizeInPoints / 72f,
                    new PointF(statusX, statusY), StringFormat.GenericTypographic);
                e.Graphics.DrawPath(statusOutline, path);
                e.Graphics.FillPath(statusBrush, path);
            }

            if (lines.Length == 0)
                return;

            const float defaultFontSizePx = 14f;
            using var defaultFont = new Font("Segoe UI", defaultFontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fillBrush = new SolidBrush(Color.Red);
            using var outlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            var defaultLineHeight = (int)defaultFont.GetHeight(e.Graphics) + 2;
            for (var i = 0; i < lines.Length; i++)
            {
                var y = (i < rowY.Length ? rowY[i] : 6 + (i * defaultLineHeight));
                y = Math.Clamp(y, 0, Height - defaultLineHeight);

                var textSize = e.Graphics.MeasureString(lines[i], defaultFont, PointF.Empty, StringFormat.GenericTypographic);
                var x = debugX + 8;
                var fontSize = e.Graphics.DpiY * defaultFont.SizeInPoints / 72f;

                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddString(lines[i], defaultFont.FontFamily, (int)defaultFont.Style, fontSize, new PointF(x, y), StringFormat.GenericTypographic);
                e.Graphics.DrawPath(outlinePen, path);
                e.Graphics.FillPath(fillBrush, path);
            }
        }

        public void SetDebugLines(string[] lines, int[]? rowY = null)
        {
            _debugLines = lines;
            _debugRowY = rowY ?? [];
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetStatusLine(string? status)
        {
            _statusLine = status;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SafeShowFrame(Rectangle frame, bool showOverlay, int panelWidth = 400)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                _isHidden = false;
                BeginInvoke(new Action<Rectangle, bool, int>(SafeShowFrame), frame, showOverlay, panelWidth);
                return;
            }

            _isHidden = false;

            _showDebugOverlay = showOverlay;
            _textPanelWidth = panelWidth;
            var extraWidth = showOverlay ? _textPanelWidth + BgPadding + DebugGap : 0;
            var statusHeight = _statusLine is not null ? 30 : 0;
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

        public void SafeHide()
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                if (_isHidden) return;
                _isHidden = true;
                BeginInvoke(new Action(SafeHide));
                return;
            }

            Hide();
        }

        public void SafeClose()
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(SafeClose));
                return;
            }

            Close();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            PinTopMost();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PinTopMost();
        }

        private void PinTopMost()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOSENDCHANGING);
        }

        private static void DrawMutedBand(Graphics graphics, int x, int y, int width, int height)
        {
            if (height <= 0 || width <= 0)
            {
                return;
            }

            using var pen = new Pen(Color.FromArgb(255, 45, 45, 45), 1);
            for (var lineY = y; lineY < y + height; lineY += 3)
            {
                graphics.DrawLine(pen, x, lineY, x + width, lineY);
            }
        }

        private static class NativeMethods
        {
            public static readonly IntPtr HWND_TOPMOST = new(-1);

            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_NOOWNERZORDER = 0x0200;
            public const uint SWP_NOSENDCHANGING = 0x0400;

            private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(
                IntPtr hWnd,
                IntPtr hWndInsertAfter,
                int x,
                int y,
                int cx,
                int cy,
                uint uFlags);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

            public static void ExcludeFromCapture(IntPtr hWnd)
            {
                SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            private const int GWLP_HWNDPARENT = -8;

            public static void SetOwner(IntPtr hWnd, IntPtr ownerHandle)
            {
                SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, ownerHandle);
            }
        }
    }
}
