using System.Drawing;
using System.Runtime.InteropServices;
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
    private bool _rowFitWarningLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            try
            {
                if (options.ShowCaptureBoundsOverlay)
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
                logger.LogWarning(ex, "Failed to refresh OCR bounds overlay.");
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

        if (!options.ShowCaptureBoundsOverlay)
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

        var frame = new Rectangle(region.X, region.Y, region.Width, region.Height);
        var profile = windowResolutionProvider.CurrentResolutionProfile;
        var rowTextHeight = profile?.RowTextHeight ?? options.RowTextHeight;
        var rowGapHeight = profile?.RowGapHeight ?? options.RowGapHeight;
        var rowCount = Math.Max(1, options.OcrRowCount);
        var rows = OcrRowLayout.BuildRowRectangles(
            frame.Width,
            frame.Height,
            rowCount,
            options.UseFixedRowGeometry,
            options.RowStartOffsetY,
            rowTextHeight,
            rowGapHeight);

        if (rows.Count < rowCount)
        {
            if (!_rowFitWarningLogged)
            {
                _rowFitWarningLogged = true;
                logger.LogWarning(
                    "OCR row geometry only fits {VisibleRows}/{RequestedRows} rows in current capture height {Height}. Increase capture height or reduce row geometry.",
                    rows.Count,
                    rowCount,
                    frame.Height);
            }
        }
        else
        {
            _rowFitWarningLogged = false;
        }

        overlay.SafeShowFrame(
            frame,
            rowCount,
            options.UseFixedRowGeometry,
            options.RowStartOffsetY,
            rowTextHeight,
            rowGapHeight);
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

    private sealed class BoundsOverlayForm : Form
    {
        private Rectangle _frame;
        private int _rowCount = 1;
        private bool _useFixedRowGeometry;
        private int _rowStartOffsetY;
        private int _rowTextHeight = 1;
        private int _rowGapHeight;

        public BoundsOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 1 || Height <= 1)
            {
                return;
            }

            var rows = OcrRowLayout.BuildRowRectangles(
                Width,
                Height,
                _rowCount,
                _useFixedRowGeometry,
                _rowStartOffsetY,
                _rowTextHeight,
                _rowGapHeight);

            var cursorY = 0;
            foreach (var row in rows)
            {
                if (row.Y > cursorY)
                {
                    DrawMutedBand(e.Graphics, 0, cursorY, Width, row.Y - cursorY);
                }

                cursorY = Math.Max(cursorY, row.Bottom);
            }

            if (cursorY < Height)
            {
                DrawMutedBand(e.Graphics, 0, cursorY, Width, Height - cursorY);
            }

            using var pen = new Pen(Color.Red, 3);
            e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);

            if (_rowCount <= 1)
            {
                return;
            }

            using var rowPen = new Pen(Color.Red, 1);
            foreach (var row in rows)
            {
                var top = Math.Clamp(row.Top, 1, Height - 2);
                var bottom = Math.Clamp(row.Bottom, 1, Height - 2);

                if (top > 1 && top < Height - 1)
                {
                    e.Graphics.DrawLine(rowPen, 2, top, Width - 3, top);
                }

                if (bottom > 1 && bottom < Height - 1)
                {
                    e.Graphics.DrawLine(rowPen, 2, bottom, Width - 3, bottom);
                }
            }
        }

        public void SafeShowFrame(
            Rectangle frame,
            int rowCount,
            bool useFixedRowGeometry,
            int rowStartOffsetY,
            int rowTextHeight,
            int rowGapHeight)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(
                    new Action<Rectangle, int, bool, int, int, int>(SafeShowFrame),
                    frame,
                    rowCount,
                    useFixedRowGeometry,
                    rowStartOffsetY,
                    rowTextHeight,
                    rowGapHeight);
                return;
            }

            var clampedRowCount = Math.Max(1, rowCount);
            var clampedRowTextHeight = Math.Max(1, rowTextHeight);
            var clampedRowGap = Math.Max(0, rowGapHeight);
            var clampedRowStart = Math.Max(0, rowStartOffsetY);

            if (_frame != frame ||
                _rowCount != clampedRowCount ||
                _useFixedRowGeometry != useFixedRowGeometry ||
                _rowStartOffsetY != clampedRowStart ||
                _rowTextHeight != clampedRowTextHeight ||
                _rowGapHeight != clampedRowGap)
            {
                _frame = frame;
                _rowCount = clampedRowCount;
                _useFixedRowGeometry = useFixedRowGeometry;
                _rowStartOffsetY = clampedRowStart;
                _rowTextHeight = clampedRowTextHeight;
                _rowGapHeight = clampedRowGap;
                Bounds = frame;
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
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(SafeHide));
                return;
            }

            Hide();
        }

        public void SafeClose()
        {
            if (IsDisposed)
            {
                return;
            }

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
        }
    }
}
