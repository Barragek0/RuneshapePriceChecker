using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Overlay;

public sealed class PricingOverlayRenderer(
    IPoe2WindowResolutionProvider windowResolutionProvider,
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    IOptionsMonitor<OcrOptions> ocrOptions,
    IOptionsMonitor<AppOptions> appOptions,
    ILogger<PricingOverlayRenderer> logger) : IOverlayRenderer, IDisposable
{
    private readonly object _sync = new();
    private readonly IOptionsMonitor<OcrOptions> _ocrOptions = ocrOptions;
    private readonly IOptionsMonitor<AppOptions> _appOptions = appOptions;
    private Thread? _overlayThread;
    private PriceOverlayForm? _overlayForm;
    private string _lastContentHash = string.Empty;

    public void Render(LeagueWindowSnapshot snapshot, IReadOnlyDictionary<string, PriceQuote?> pricesByItemName)
    {
        try
        {
            if (!_appOptions.CurrentValue.PricingOverlay)
                return;

            EnsureOverlayThreadStarted();
            var overlay = GetOverlayForm();
            if (overlay is null)
            {
                logger.LogDebug("PriceOverlay: form not available, skipping render");
                return;
            }

            var captureRegion = windowResolutionProvider.CurrentCaptureRegion;
            if (captureRegion is null)
            {
                _lastContentHash = string.Empty;
                overlay.SafeHide();
                return;
            }

            var itemCount = snapshot.ItemNames.Count;
            if (itemCount == 0)
            {
                _lastContentHash = string.Empty;
                overlay.SafeHide();
                return;
            }

            // Skip render when content unchanged (same items + same prices)
            var hash = BuildContentHash(snapshot, pricesByItemName);
            if (hash == _lastContentHash)
                return;
            _lastContentHash = hash;

            List<Rectangle> rows;
            if (snapshot.RowYPositions is { Count: > 0 } positions && positions.Count == itemCount)
            {
                rows = new List<Rectangle>(itemCount);
                const int rowH = 24;
                for (var i = 0; i < itemCount; i++)
                    rows.Add(new Rectangle(0, positions[i], captureRegion.Width, rowH));
            }
            else
            {
                var rowH = captureRegion.Height / itemCount;
                rows = new List<Rectangle>(itemCount);
                for (var i = 0; i < itemCount; i++)
                    rows.Add(new Rectangle(0, i * rowH, captureRegion.Width, rowH));
            }

            var pricing = pricingOptions.CurrentValue;
            if (pricing.AutoPriceThresholds)
            {
                // Calculate dynamic thresholds based on the display values of items in this scan.
                // We parse each item's label the same way GetPriceColor will (via
                // TryParseDisplayedChaosEquivalent) so thresholds are in display-currency units.
                var maxPrice = 0m;
                foreach (var kvp in pricesByItemName)
                {
                    if (kvp.Value is null || kvp.Value.IsRange) continue;
                    if (TryParseDisplayedChaosEquivalent(kvp.Value.Label, pricing, out var displayValue))
                    {
                        if (displayValue > maxPrice)
                            maxPrice = displayValue;
                    }
                    else if (kvp.Value.RepresentativeChaosValue > maxPrice)
                    {
                        maxPrice = kvp.Value.RepresentativeChaosValue;
                    }
                }
                if (maxPrice > 0m)
                {
                    pricing = new PricingCacheOptions
                    {
                        AutoPriceThresholds = true,
                        RedThreshold = maxPrice * 0.1m,
                        OrangeThreshold = maxPrice * 0.3m,
                        GreenThreshold = maxPrice * 0.7m,
                        DisplayCurrency = pricing.DisplayCurrency,
                        League = pricing.League,
                        PricingSource = pricing.PricingSource
                    };
                }
            }
            var entries = BuildEntries(snapshot, pricesByItemName, rows, pricing);

            // Scale font proportionally to window height (1080p = scale 1.0, 4k = scale 2.0)
            var scaleFactor = ComputeOverlayScale(windowResolutionProvider, _appOptions);
            overlay.SafeShow(captureRegion, entries, scaleFactor);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to render price overlay: {Context} ({Count} items)", ErrorContext.FromException(ex), snapshot.ItemNames.Count);
        }
    }

    private static string BuildContentHash(LeagueWindowSnapshot snapshot,
        IReadOnlyDictionary<string, PriceQuote?> pricesByItemName)
    {
        // Quick hash of items, prices, and row Y positions — avoid full structural comparison.
        // Row positions are included so scrolling triggers a re-render even when items/prices are the same.
        var hash = new System.Text.StringBuilder();
        var positions = snapshot.RowYPositions;
        for (var i = 0; i < snapshot.ItemNames.Count; i++)
        {
            _ = hash.Append(snapshot.ItemNames[i]);
            _ = hash.Append(i < positions?.Count ? positions[i] : i);
            if (pricesByItemName.TryGetValue(snapshot.ItemNames[i], out var quote) && quote is not null)
                _ = hash.Append(quote.Label);
        }
        return hash.ToString();
    }

    private static List<OverlayRowEntry> BuildEntries(
        LeagueWindowSnapshot snapshot,
        IReadOnlyDictionary<string, PriceQuote?> pricesByItemName,
        List<Rectangle> rows,
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

            var segments = BuildTextSegments(quote, pricing);
            entries.Add(new OverlayRowEntry(row.Y, row.Height, segments));
        }

        return entries;
    }

    private static IReadOnlyList<OverlayTextSegment> BuildTextSegments(PriceQuote quote, PricingCacheOptions pricing)
    {
        var fallbackColor = TryParseDisplayedChaosEquivalent(quote.Label, pricing, out var parsedDisplayValue)
            ? GetPriceColor(parsedDisplayValue, pricing)
            : GetPriceColor(quote.RepresentativeChaosValue, pricing);

        if (!quote.IsRange)
        {
            return [new OverlayTextSegment(quote.Label, fallbackColor, GetDivineGlowStrength(quote.Label))];
        }

        // The separator must stay as " -", otherwise it looks weird sometimes
        const string separator = " -";
        var splitIndex = quote.Label.IndexOf(separator, StringComparison.Ordinal);
        if (splitIndex < 0)
        {
            return [new OverlayTextSegment(quote.Label, fallbackColor, GetDivineGlowStrength(quote.Label))];
        }

        var leftText = quote.Label[..splitIndex];
        var rightText = quote.Label[(splitIndex + separator.Length)..];

        var leftColor = TryParseDisplayedChaosEquivalent(leftText, pricing, out var leftChaos)
            ? GetPriceColor(leftChaos, pricing)
            : fallbackColor;

        var rightColor = TryParseDisplayedChaosEquivalent(rightText, pricing, out var rightChaos)
            ? GetPriceColor(rightChaos, pricing)
            : fallbackColor;

        return
        [
            new OverlayTextSegment(leftText, leftColor, GetDivineGlowStrength(leftText)),
            new OverlayTextSegment(separator, Color.White, 0f),
            new OverlayTextSegment(rightText, rightColor, GetDivineGlowStrength(rightText))
        ];
    }

    private static float GetDivineGlowStrength(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0f;
        }

        var trimmed = text.Trim();
        if (!trimmed.EndsWith("d", StringComparison.OrdinalIgnoreCase))
        {
            return 0f;
        }

        var numericPart = trimmed[..^1];
        if (!decimal.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var divineValue))
        {
            return 0f;
        }

        if (divineValue <= 0m)
        {
            return 0f;
        }

        var clamped = decimal.Min(100m, decimal.Max(1m, divineValue));
        var normalized = (float)((clamped - 1m) / 99m);
        return 0.62f + (normalized * 0.35f);
    }

    private static bool TryParseDisplayedChaosEquivalent(string formattedAmount, PricingCacheOptions pricing, out decimal chaosEquivalent)
    {
        chaosEquivalent = 0m;
        if (string.IsNullOrWhiteSpace(formattedAmount))
        {
            return false;
        }

        var trimmed = formattedAmount.Trim();

        if (trimmed.EndsWith("ex", StringComparison.OrdinalIgnoreCase))
        {
            var valueText = trimmed[..^2].Trim();
            if (valueText.StartsWith('<'))
            {
                valueText = valueText[1..];
            }

            if (decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var exaltValue))
            {
                // In exalt display mode, thresholds are tuned for the displayed unit value.
                chaosEquivalent = Math.Max(0m, exaltValue);
                return true;
            }

            return false;
        }

        if (trimmed.EndsWith("c", StringComparison.OrdinalIgnoreCase))
        {
            var valueText = trimmed[..^1].Trim();
            if (valueText.StartsWith('<'))
            {
                valueText = valueText[1..];
            }

            if (decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var chaosValue))
            {
                chaosEquivalent = Math.Max(0m, chaosValue);
                return true;
            }

            return false;
        }

        if (trimmed.EndsWith("d", StringComparison.OrdinalIgnoreCase))
        {
            var valueText = trimmed[..^1].Trim();
            if (valueText.StartsWith('<'))
            {
                valueText = valueText[1..];
            }

            if (decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var divineValue))
            {
                // Any divine-denominated price should be at or above the green threshold.
                chaosEquivalent = Math.Max(pricing.GreenThreshold, Math.Max(0m, divineValue));
                return true;
            }

            return false;
        }

        return false;
    }

    private static Color GetPriceColor(decimal chaosValue, PricingCacheOptions pricing)
    {
        if (chaosValue < 0m)
        {
            return Color.FromArgb(255, 220, 60, 60);
        }

        var chaos = Math.Max(0m, chaosValue);
        var redThreshold = pricing.RedThreshold;
        var orangeThreshold = pricing.OrangeThreshold;
        var greenThreshold = pricing.GreenThreshold;

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

            logger.LogDebug("PriceOverlay: starting overlay thread");
            _overlayThread = new Thread(() =>
            {
                logger.LogDebug("PriceOverlay: thread entered, creating form");
                using var form = new PriceOverlayForm();
                var _ = form.Handle;
                logger.LogDebug("PriceOverlay: form handle created, pulsing");
                lock (_sync)
                {
                    _overlayForm = form;
                    Monitor.PulseAll(_sync);
                }

                logger.LogDebug("PriceOverlay: entering Application.Run");
                Application.Run(form);
                logger.LogDebug("PriceOverlay: Application.Run returned");

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

            logger.LogDebug("PriceOverlay: waiting for form creation");
            while (_overlayForm is null)
            {
                if (!Monitor.Wait(_sync, TimeSpan.FromSeconds(5)))
                {
                    logger.LogWarning("Price overlay form creation timed out; overlay will be unavailable.");
                    return;
                }
            }
            logger.LogDebug("PriceOverlay: form created, thread ready");
        }
    }

    private PriceOverlayForm? GetOverlayForm()
    {
        lock (_sync)
        {
            if (_overlayForm is { IsDisposed: true })
                _overlayForm = null;
            return _overlayForm;
        }
    }

    public void Dispose()
    {
        var overlay = GetOverlayForm();
        overlay?.SafeClose();
    }

    private sealed record OverlayTextSegment(string Text, Color Color, float GlowStrength);
    private sealed record OverlayRowEntry(int RowY, int RowHeight, IReadOnlyList<OverlayTextSegment> Segments);

    public static float ComputeOverlayScale(IPoe2WindowResolutionProvider resolutionProvider, IOptionsMonitor<AppOptions> appOptions)
    {
        var overrideScale = appOptions.CurrentValue.OverlayScale;
        if (overrideScale.HasValue)
            return Math.Max(0.25f, Math.Min(4f, overrideScale.Value));

        var ctx = resolutionProvider.CurrentWindowCaptureContext;
        return ctx is not null ? Math.Max(0.5f, ctx.ClientHeight / 1080f) : 1f;
    }

    private sealed class PriceOverlayForm : Form
    {
        private static readonly Color TransparencyChroma = Color.FromArgb(1, 2, 3);
        private const float BaseFontSizePx = 21f;
        private const int BaseOverlayWidth = 220;
        private Font _font = new("Segoe UI", BaseFontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
        private float _scaleFactor = 1f;
        private readonly object _stateSync = new();
        private IReadOnlyList<OverlayRowEntry> _entries = [];
        private OcrCaptureRegion _captureRegion = new(0, 0, 1, 1);
        private volatile bool _isHidden = true;

        public PriceOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = TransparencyChroma;
            TransparencyKey = TransparencyChroma;
            DoubleBuffered = true;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            Cursor = Cursors.Default;
        }

        protected override bool ShowWithoutActivation => true;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0020 && Cursor is not null)
            {
                _ = SetCursor(Cursor.Handle);
                m.Result = 1;
                return;
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

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

        public void SafeShow(OcrCaptureRegion captureRegion, IReadOnlyList<OverlayRowEntry> entries, float scaleFactor = 1f)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                _isHidden = false;
                _ = BeginInvoke(new Action<OcrCaptureRegion, IReadOnlyList<OverlayRowEntry>, float>(SafeShow), captureRegion, entries, scaleFactor);
                return;
            }

            _isHidden = false;

            lock (_stateSync)
            {
                _captureRegion = captureRegion;
                _entries = entries;
            }

            ApplyScaleFactor(scaleFactor);

            var width = (int)Math.Round(BaseOverlayWidth * scaleFactor);
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

        private void ApplyScaleFactor(float scaleFactor)
        {
            if (Math.Abs(_scaleFactor - scaleFactor) < 0.01f)
                return;

            _scaleFactor = scaleFactor;
            var newSize = (float)Math.Round(BaseFontSizePx * scaleFactor);
            var oldFont = _font;
            _font = new Font("Segoe UI", newSize, FontStyle.Bold, GraphicsUnit.Pixel);
            oldFont.Dispose();
        }

        public void SafeClose()
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                _ = BeginInvoke(new Action(SafeClose));
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
                if (_isHidden) return;
                _isHidden = true;
                _ = BeginInvoke(new Action(SafeHide));
                return;
            }

            lock (_stateSync)
            {
                _entries = [];
            }

            Hide();
            Bounds = new Rectangle(-32000, -32000, 1, 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (IsDisposed) return;

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
                var x = 2f;
                foreach (var segment in entry.Segments)
                {
                    x += DrawOutlinedText(e.Graphics, segment.Text, _font, segment.Color, segment.GlowStrength, x, y);
                }
            }
        }

        private static float DrawOutlinedText(Graphics graphics, string text, Font font, Color fillColor, float glowStrength, float x, float y)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0f;
            }

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

            var glow = Math.Clamp(glowStrength, 0f, 1f);
            if (glow > 0f)
            {
                // Scale smoothly from visible (1d) to strong (100d+) without overwhelming text.
                var outerAlpha = (int)Math.Round(110 + (70 * glow));
                var innerAlpha = (int)Math.Round(130 + (80 * glow));
                var coreAlpha = (int)Math.Round(150 + (80 * glow));

                var outerWidth = 5.5f + (3.2f * glow);
                var innerWidth = 3.5f + (2.2f * glow);
                var coreWidth = 2.0f + (1.4f * glow);

                using var outerGlowPen = new Pen(Color.FromArgb(outerAlpha, 196, 136, 28), outerWidth)
                {
                    LineJoin = LineJoin.Round,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                using var innerGlowPen = new Pen(Color.FromArgb(innerAlpha, 235, 178, 52), innerWidth)
                {
                    LineJoin = LineJoin.Round,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };

                using var coreGlowPen = new Pen(Color.FromArgb(coreAlpha, 255, 223, 120), coreWidth)
                {
                    LineJoin = LineJoin.Round,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };

                graphics.DrawPath(outerGlowPen, path);
                graphics.DrawPath(innerGlowPen, path);
                graphics.DrawPath(coreGlowPen, path);
            }

            using var fillBrush = new SolidBrush(fillColor);

            graphics.DrawPath(outlinePen, path);
            graphics.FillPath(fillBrush, path);

            return graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
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

            _ = NativeMethods.SetWindowPos(
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
