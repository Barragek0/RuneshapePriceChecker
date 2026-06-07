using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.OCR;

public sealed class OcrCaptureBoundsOverlayService(
    IPoe2WindowResolutionProvider windowResolutionProvider,
    IOptionsMonitor<OcrOptions> options,
    ILogger<OcrCaptureBoundsOverlayService> logger) : BackgroundService
{
    private readonly IOptionsMonitor<OcrOptions> _options = options;
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

            var intervalMs = Math.Max(100, options.CaptureBoundsOverlayIntervalMs);
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

        if (_forceHidden)
        {
            overlay.SafeHide();
            return;
        }

        var frame = new Rectangle(region.X, region.Y, region.Width, region.Height);
        var panelWidth = (windowResolutionProvider.CurrentResolutionProfile?.CaptureOffsetX ?? 255) - 57;

        overlay.SafeShowFrame(frame, true, panelWidth);
        RefreshAnchorRegions(overlay);
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
    private bool _forceHidden;
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
                Monitor.Wait(_bannerSync);
        }
    }

    private BannerForm? GetBannerForm()
    {
        lock (_bannerSync) { return _bannerForm; }
    }

    public void SetDebugText(IReadOnlyList<string> lines, IReadOnlyList<int>? rowYPositions = null, bool interfaceDetected = true)
    {
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
                var region = windowResolutionProvider.CurrentCaptureRegion;
                if (region is not null)
                {
                    var panelWidth = (windowResolutionProvider.CurrentResolutionProfile?.CaptureOffsetX ?? 255) - 57;
                    overlay.SafeShowFrame(new Rectangle(region.X, region.Y, region.Width, region.Height), _options.CurrentValue.DebugOverlay, panelWidth);
                }
            }
        }

        RefreshAnchorRegions(overlay);
        overlay.SetDebugLines(lines.ToArray(), rowYPositions?.ToArray());
    }

    private void RefreshAnchorRegions(BoundsOverlayForm overlay)
    {
        var region = windowResolutionProvider.CurrentCaptureRegion;
        if (region is null) return;

        var options = _options.CurrentValue;
        var w = region.Width;
        var h = region.Height;

        var leftX = options.LeaguePanelAnchorFractionX > 0f
            ? (int)(w * Math.Clamp(options.LeaguePanelAnchorFractionX, 0f, 1f))
            : Math.Clamp(options.LeaguePanelAnchorSampleX, 0, w - 1);

        var sampleY = options.LeaguePanelAnchorFractionY > 0f
            ? (int)(h * Math.Clamp(options.LeaguePanelAnchorFractionY, 0f, 1f))
            : Math.Clamp(options.LeaguePanelAnchorSampleY, 0, h - 1);

        var sampleRadiusX = options.LeaguePanelAnchorSampleRadiusFraction > 0f
            ? Math.Clamp((int)(h * options.LeaguePanelAnchorSampleRadiusFraction), 2, 20)
            : Math.Clamp(options.LeaguePanelAnchorSampleRadiusPx, 2, 20);

        var sampleRadiusY = options.LeaguePanelAnchorSampleRadiusYFraction > 0f
            ? Math.Clamp((int)(h * options.LeaguePanelAnchorSampleRadiusYFraction), 2, 20)
            : Math.Clamp(options.LeaguePanelAnchorSampleRadiusYPx, 2, 20);

        var rightX = w - 1 - leftX;

        overlay.SetAnchorRegions(leftX, rightX, sampleY, sampleRadiusX, sampleRadiusY);
    }

    private sealed class BoundsOverlayForm : Form
    {
        private static readonly Color TransparencyChroma = Color.FromArgb(1, 2, 3);
        private int _textPanelWidth = 198;
        private const int BgPadding = 30;
        private Rectangle _frame;
        private string[] _debugLines = [];
        private int[] _debugRowY = [];
        private bool _showDebugOverlay;
        private int _anchorLeftX;
        private int _anchorRightX;
        private int _anchorY;
        private int _anchorRadiusX;
        private int _anchorRadiusY;
        private volatile bool _isHidden = true;

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

            var boxX = _showDebugOverlay ? _textPanelWidth + BgPadding : 0;
            var boxWidth = Width - boxX;

            if (!_showDebugOverlay)
                return;

            var bgWidth = boxX;
            using var bgBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            e.Graphics.FillRectangle(bgBrush, 0, 0, bgWidth, Height);

            using var pen = new Pen(Color.Red, 3);
            e.Graphics.DrawRectangle(pen, boxX + 1, 1, Width - boxX - 3, Height - 3);

            if (_anchorRadiusX > 0 && _anchorRadiusY > 0)
            {
                using var anchorPen = new Pen(Color.Red, 2);
                var anchorW = _anchorRadiusX * 2 + 1;
                var anchorH = _anchorRadiusY * 2 + 1;
                var borderInset = 2;
                var anchorLeft = boxX + 1 + borderInset;
                var anchorTop = 1 + borderInset + Math.Max(0, _anchorY - _anchorRadiusY);
                var redBoxInnerWidth = Width - boxX - 3;
                var anchorRight = anchorLeft + redBoxInnerWidth - borderInset - anchorW;
                e.Graphics.DrawRectangle(anchorPen, anchorLeft, anchorTop, anchorW, anchorH);
                e.Graphics.DrawRectangle(anchorPen, anchorRight, anchorTop, anchorW, anchorH);
            }

            var lines = _debugLines;
            var rowY = _debugRowY;
            if (lines.Length == 0)
                return;

            const float defaultFontSizePx = 18f;
            const float minFontSizePx = 10f;
            const int maxTextWidthMargin = 8;
            var maxTextWidth = _textPanelWidth - maxTextWidthMargin;

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

                var defaultTextWidth = e.Graphics.MeasureString(lines[i], defaultFont, PointF.Empty, StringFormat.GenericTypographic).Width;
                var scale = defaultTextWidth > maxTextWidth ? maxTextWidth / defaultTextWidth : 1f;
                var effectiveFontSizePx = Math.Max(minFontSizePx, defaultFontSizePx * scale);

                Font lineFont;
                if (Math.Abs(effectiveFontSizePx - defaultFontSizePx) < 0.5f)
                {
                    lineFont = defaultFont;
                }
                else
                {
                    lineFont = new Font("Segoe UI", effectiveFontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
                }

                var lineFontSize = e.Graphics.DpiY * lineFont.SizeInPoints / 72f;
                var textSize = e.Graphics.MeasureString(lines[i], lineFont, PointF.Empty, StringFormat.GenericTypographic);
                var x = boxX - textSize.Width - 8;
                var scaledLineHeight = lineFont.GetHeight(e.Graphics);
                var yOffset = (defaultFont.GetHeight(e.Graphics) - scaledLineHeight) / 2f;
                var adjustedY = y + yOffset;

                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddString(lines[i], lineFont.FontFamily, (int)lineFont.Style, lineFontSize, new PointF(x, adjustedY), StringFormat.GenericTypographic);
                e.Graphics.DrawPath(outlinePen, path);
                e.Graphics.FillPath(fillBrush, path);

                if (lineFont != defaultFont)
                {
                    lineFont.Dispose();
                }
            }
        }

        public void SetDebugLines(string[] lines, int[]? rowY = null)
        {
            _debugLines = lines;
            _debugRowY = rowY ?? [];
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SetAnchorRegions(int leftX, int rightX, int y, int radiusX, int radiusY)
        {
            _anchorLeftX = leftX;
            _anchorRightX = rightX;
            _anchorY = y;
            _anchorRadiusX = radiusX;
            _anchorRadiusY = radiusY;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SafeShowFrame(Rectangle frame, bool showOverlay, int panelWidth = 198)
        {
            _isHidden = false;

            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<Rectangle, bool, int>(SafeShowFrame), frame, showOverlay, panelWidth);
                return;
            }

            _showDebugOverlay = showOverlay;
            _textPanelWidth = panelWidth;
            var totalLeft = showOverlay ? _textPanelWidth + BgPadding : 0;
            var fullFrame = new Rectangle(
                frame.X - totalLeft,
                frame.Y,
                frame.Width + totalLeft,
                frame.Height);
            if (_frame != frame || Bounds != fullFrame)
            {
                _frame = frame;
                Bounds = fullFrame;
                Invalidate();
            }

            PinTopMost();

            if (!Visible)
            {
                Show();
                PinTopMost();
            }
        }

        public void SafeHide()
        {
            if (IsDisposed || _isHidden) return;

            _isHidden = true;

            if (InvokeRequired)
            {
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

    private sealed class BannerForm : Form
    {
        private static readonly Color TransparencyChroma = Color.FromArgb(1, 2, 3);
        private string? _message;
        private volatile bool _isHidden = true;

        public BannerForm()
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

        public void SetMessage(string? message)
        {
            _message = message;
            if (!IsDisposed && Visible)
                Invalidate();
        }

        public void SafeShow(int x, int y)
        {
            _isHidden = false;

            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, int>(SafeShow), x, y);
                return;
            }

            Bounds = new Rectangle(x, y, 400, 50);
            PinTopMost();
            Invalidate();

            if (!Visible)
            {
                Show();
                PinTopMost();
            }
        }

        public void SafeHide()
        {
            if (IsDisposed || _isHidden) return;

            _isHidden = true;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(SafeHide));
                return;
            }

            Hide();
            Bounds = new Rectangle(-32000, -32000, 1, 1);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PinTopMost();
        }

        private void PinTopMost()
        {
            if (!IsHandleCreated) return;
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                Left, Top, Width, Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOSENDCHANGING);
        }

        private static class NativeMethods
        {
            public static readonly IntPtr HWND_TOPMOST = new(-1);
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_NOOWNERZORDER = 0x0200;
            public const uint SWP_NOSENDCHANGING = 0x0400;

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(
                IntPtr hWnd, IntPtr hWndInsertAfter,
                int x, int y, int cx, int cy, uint uFlags);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (string.IsNullOrWhiteSpace(_message)) return;

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            const float fontSizePx = 16f;
            using var font = new Font("Segoe UI", fontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
            var emSize = e.Graphics.DpiY * font.SizeInPoints / 72f;
            var lines = _message.Split('\n');
            var lineHeight = font.GetHeight(e.Graphics);

            using var outlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            using var fillBrush = new SolidBrush(Color.Red);

            var totalTextHeight = lines.Length * lineHeight;
            var startY = (50 - totalTextHeight) / 2f;

            for (var li = 0; li < lines.Length; li++)
            {
                var line = lines[li];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var textSize = e.Graphics.MeasureString(line, font, PointF.Empty, StringFormat.GenericTypographic);
                var x = Math.Max(0f, (400 - textSize.Width) / 2f);
                var y = startY + (li * lineHeight);

                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddString(line, font.FontFamily, (int)font.Style, emSize,
                    new PointF(x, y), StringFormat.GenericTypographic);
                e.Graphics.DrawPath(outlinePen, path);
                e.Graphics.FillPath(fillBrush, path);
            }
        }
    }
}
