using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RuneshapePriceChecker.Overlay;

public sealed class ConsoleOverlayRenderer(
    IPoe2WindowResolutionProvider windowResolutionProvider,
    IOptionsMonitor<OcrOptions> options,
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    ILogger<ConsoleOverlayRenderer> logger) : IOverlayRenderer, IDisposable
{
    private readonly object _sync = new();
    private Thread? _overlayThread;
    private PriceOverlayForm? _overlayForm;

    public void Render(LeagueWindowSnapshot snapshot, IReadOnlyDictionary<string, PriceQuote?> pricesByItemName)
    {
        try
        {
            EnsureOverlayThreadStarted();
            var overlay = GetOverlayForm();
            if (overlay is null)
            {
                return;
            }

            var captureRegion = windowResolutionProvider.CurrentCaptureRegion;
            if (captureRegion is null)
            {
                overlay.SafeHide();
                return;
            }

            var profile = windowResolutionProvider.CurrentResolutionProfile;
            var rowTextHeight = profile?.RowTextHeight ?? options.CurrentValue.RowTextHeight;
            var rowGapHeight = profile?.RowGapHeight ?? options.CurrentValue.RowGapHeight;

            var rows = OcrRowLayout.BuildRowRectangles(
                captureRegion.Width,
                captureRegion.Height,
                options.CurrentValue.OcrRowCount,
                options.CurrentValue.UseFixedRowGeometry,
                options.CurrentValue.RowStartOffsetY,
                rowTextHeight,
                rowGapHeight);

            var entries = BuildEntries(snapshot, pricesByItemName, rows, pricingOptions.CurrentValue);

            overlay.SafeShow(captureRegion, entries);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to render price overlay.");
        }
    }

    private static IReadOnlyList<OverlayRowEntry> BuildEntries(
        LeagueWindowSnapshot snapshot,
        IReadOnlyDictionary<string, PriceQuote?> pricesByItemName,
        IReadOnlyList<Rectangle> rows,
        PricingCacheOptions pricing)
    {
        var count = Math.Min(snapshot.ItemNames.Count, rows.Count);
        var entries = new List<OverlayRowEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var itemName = snapshot.ItemNames[i];
            var row = rows[i];
            var quote = pricesByItemName.TryGetValue(itemName, out var value) ? value : null;
            if (quote is null)
            {
                continue;
            }

            var label = quote.Label;
            var color = GetPriceColor(quote.RepresentativeChaosValue, pricing);
            entries.Add(new OverlayRowEntry(row.Y, row.Height, label, color));
        }

        return entries;
    }

    private static Color GetPriceColor(decimal chaosValue, PricingCacheOptions pricing)
    {
        var chaos = Math.Max(0m, chaosValue);
        var redThreshold = pricing.RedThresholdChaos;
        var orangeThreshold = pricing.OrangeThresholdChaos;
        var greenThreshold = pricing.GreenThresholdChaos;

        var red = Color.FromArgb(255, 255, 72, 72);
        var orange = Color.FromArgb(255, 255, 196, 54);
        var green = Color.FromArgb(255, 88, 255, 122);

        if (chaos <= redThreshold)
        {
            return red;
        }

        if (chaos < orangeThreshold)
        {
            var tRedToOrange = (double)((chaos - redThreshold) / (orangeThreshold - redThreshold));
            return LerpColor(red, orange, tRedToOrange);
        }

        if (chaos < greenThreshold)
        {
            var tOrangeToGreen = (double)((chaos - orangeThreshold) / (greenThreshold - orangeThreshold));
            return LerpColor(orange, green, tOrangeToGreen);
        }

        return green;
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0d, 1d);
        var r = (int)Math.Round(a.R + ((b.R - a.R) * t));
        var g = (int)Math.Round(a.G + ((b.G - a.G) * t));
        var bl = (int)Math.Round(a.B + ((b.B - a.B) * t));
        return Color.FromArgb(255, r, g, bl);
    }

    private void EnsureOverlayThreadStarted()
    {
        lock (_sync)
        {
            if (_overlayThread is { IsAlive: true })
            {
                return;
            }

            _overlayThread = new Thread(() =>
            {
                using var form = new PriceOverlayForm();
                lock (_sync)
                {
                    _overlayForm = form;
                }

                Application.Run(form);

                lock (_sync)
                {
                    _overlayForm = null;
                }
            })
            {
                IsBackground = true,
                Name = "RuneshapePriceChecker-PriceOverlay"
            };

            _overlayThread.SetApartmentState(ApartmentState.STA);
            _overlayThread.Start();
        }
    }

    private PriceOverlayForm? GetOverlayForm()
    {
        lock (_sync)
        {
            return _overlayForm;
        }
    }

    public void Dispose()
    {
        var overlay = GetOverlayForm();
        overlay?.SafeClose();
    }

    private sealed record OverlayRowEntry(int RowY, int RowHeight, string Text, Color Color);

    private sealed class PriceOverlayForm : Form
    {
        private static readonly Color TransparencyChroma = Color.FromArgb(1, 2, 3);
        private readonly Font _font = new("Segoe UI", 21f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly object _stateSync = new();
        private IReadOnlyList<OverlayRowEntry> _entries = [];
        private OcrCaptureRegion _captureRegion = new(0, 0, 1, 1);

        public PriceOverlayForm()
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
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        public void SafeShow(OcrCaptureRegion captureRegion, IReadOnlyList<OverlayRowEntry> entries)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<OcrCaptureRegion, IReadOnlyList<OverlayRowEntry>>(SafeShow), captureRegion, entries);
                return;
            }

            lock (_stateSync)
            {
                _captureRegion = captureRegion;
                _entries = entries;
            }

            var width = 220;
            var x = captureRegion.X + captureRegion.Width + 5;
            var y = captureRegion.Y;
            Bounds = new Rectangle(x, y, width, Math.Max(1, captureRegion.Height));
            PinTopMost();
            Invalidate();

            if (!Visible)
            {
                Show();
                PinTopMost();
            }
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            IReadOnlyList<OverlayRowEntry> entries;
            lock (_stateSync)
            {
                entries = _entries;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            foreach (var entry in entries)
            {
                var y = Math.Max(0, entry.RowY + ((entry.RowHeight - (int)_font.GetHeight(e.Graphics)) / 2));
                DrawOutlinedText(e.Graphics, entry.Text, _font, entry.Color, 2f, y);
            }
        }

        private static void DrawOutlinedText(Graphics graphics, string text, Font font, Color fillColor, float x, float y)
        {
            using var path = new GraphicsPath();
            path.AddString(
                text,
                font.FontFamily,
                (int)font.Style,
                graphics.DpiY * font.SizeInPoints / 72f,
                new PointF(x, y),
                StringFormat.GenericTypographic);

            using var outlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var fillBrush = new SolidBrush(fillColor);

            graphics.DrawPath(outlinePen, path);
            graphics.FillPath(fillBrush, path);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _font.Dispose();
            }

            base.Dispose(disposing);
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
