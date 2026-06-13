using System.Drawing.Drawing2D;
using RuneshapePriceChecker.OCR;

namespace ResolutionVisualizer;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new VisualizerForm());
    }
}

internal sealed class VisualizerForm : Form
{
    private readonly Dictionary<string, OcrResolutionProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _profileKeys = [];
    private int _currentIndex;
    private float _userScale = 1f;
    private Bitmap? _screenshot;
    private readonly OpenFileDialog _openDialog = new() { Filter = "PNG Images|*.png|JPEG Images|*.jpg;*.jpeg|All Files|*.*" };

    private bool _showDebugOverlay = true;
    private bool _showScanZone = true;
    private bool _showPriceOverlay = true;
    private bool _showSetupOverlay;

    private static readonly string[] SampleItems =
    [
        "1x Verisium Pile",
        "1x Support Scouring Flame",
        "1x Support Runeforged Blades",
        "1x Skill Verisium Manifestations",
        "1x Skill Powered by Verisium",
        "1x Skill Remnants of Kalguur",
        "1x Skill Grim Pillars",
        "1x Skill Bitter Dead"
    ];

    private static readonly string[] SamplePrices =
    [
        "3.8c", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A"
    ];

    public VisualizerForm()
    {
        foreach (var kvp in OcrResolutionProfiles.All)
            _profiles[kvp.Key] = kvp.Value;
        _profileKeys = [.. _profiles.Keys.OrderBy(k =>
        {
            var parts = k.Split('x');
            return int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
                ? (long)w * h
                : 0L;
        })];

        Text = "Resolution Visualizer — O load screenshot, ← → cycle, +/- zoom, R reset, S=scan P=prices U=setup";
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Right:
                if (!e.Control && !e.Alt)
                {
                    _currentIndex = (_currentIndex + 1) % _profileKeys.Length;
                    UpdateFormSize();
                    Invalidate();
                }
                break;
            case Keys.Left:
                if (!e.Control && !e.Alt)
                {
                    _currentIndex = (_currentIndex - 1 + _profileKeys.Length) % _profileKeys.Length;
                    UpdateFormSize();
                    Invalidate();
                }
                break;
            case Keys.Oemplus:
            case Keys.Add:
                _userScale = Math.Min(3f, _userScale + 0.1f);
                UpdateFormSize();
                Invalidate();
                break;
            case Keys.OemMinus:
            case Keys.Subtract:
                _userScale = Math.Max(0.25f, _userScale - 0.1f);
                UpdateFormSize();
                Invalidate();
                break;
            case Keys.R:
                _userScale = 1f;
                UpdateFormSize();
                Invalidate();
                break;
            case Keys.O:
                if (_openDialog.ShowDialog(this) == DialogResult.OK)
                {
                    _screenshot?.Dispose();
                    try { _screenshot = new Bitmap(_openDialog.FileName); }
                    catch { _screenshot = null; }
                    UpdateFormSize();
                    Invalidate();
                }
                break;
            case Keys.S:
                _showScanZone = !_showScanZone;
                Invalidate();
                break;
            case Keys.P:
                _showPriceOverlay = !_showPriceOverlay;
                Invalidate();
                break;
            case Keys.U:
                _showSetupOverlay = !_showSetupOverlay;
                _showDebugOverlay = !_showSetupOverlay;
                Invalidate();
                break;
        }
    }

    private void UpdateFormSize()
    {
        if (_profileKeys.Length == 0) return;
        var key = _profileKeys[_currentIndex];
        var (gw, gh) = ParseResolution(key);
        var w = (int)(gw * _userScale) + 40;
        var h = (int)(gh * _userScale) + 100;
        var screen = Screen.FromControl(this).WorkingArea;
        w = Math.Min(w, screen.Width - 100);
        h = Math.Min(h, screen.Height - 100);
        ClientSize = new Size(w, h);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (_profileKeys.Length == 0) return;
        var key = _profileKeys[_currentIndex];
        var flags = "";
        if (_showDebugOverlay) flags += " DEBUG";
        if (_showScanZone) flags += " SCAN";
        if (_showPriceOverlay) flags += " PRICE";
        if (_showSetupOverlay) flags += " SETUP";
        Text = $"Resolution Visualizer — {key} — Zoom: {_userScale:F1}x —{flags} — ← → cycle, +/- zoom, O screenshot, S/P/U toggles";
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateFormSize();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_profileKeys.Length == 0) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var key = _profileKeys[_currentIndex];
        var profile = _profiles[key];
        var (gameW, gameH) = ParseResolution(key);

        var offsetX = (ClientSize.Width - (gameW * _userScale)) / 2f;
        var offsetY = 20f;

        g.TranslateTransform(offsetX, offsetY);
        g.ScaleTransform(_userScale, _userScale);

        DrawGameWindow(g, gameW, gameH);

        g.ResetTransform();
        g.TranslateTransform(offsetX, offsetY);
        g.ScaleTransform(_userScale, _userScale);

        DrawOverlayElements(g, profile);
        DrawInfoBar(g, key, profile, gameH);
    }

    private void DrawGameWindow(Graphics g, int w, int h)
    {
        if (_screenshot is not null)
        {
            g.DrawImage(_screenshot, 0, 0, w, h);
        }
        else
        {
            using var borderPen = new Pen(Color.FromArgb(180, 180, 180), 2);
            g.DrawRectangle(borderPen, 0, 0, w, h);

            using var titleBg = new SolidBrush(Color.FromArgb(40, 40, 40));
            g.FillRectangle(titleBg, 0, 0, w, 30);

            using var titleFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var titleBrush = new SolidBrush(Color.White);
            g.DrawString("Path of Exile 2 — Borderless Window  (O to load screenshot)", titleFont, titleBrush, 8, 6);

            DrawMockPanel(g, w, h);
        }
    }

    private static void DrawMockPanel(Graphics g, int w, int h)
    {
        using var panelBg = new SolidBrush(Color.FromArgb(18, 16, 22));
        g.FillRectangle(panelBg, 0, 30, w, h - 30);

        using var headerFont = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var headerBrush = new SolidBrush(Color.FromArgb(220, 200, 140));
        g.DrawString("Runes of Aldur — League Listing (mock)", headerFont, headerBrush, 10, 36);

        using var itemFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        var rowH = (h - 60) / Math.Max(1, SampleItems.Length);
        for (var i = 0; i < SampleItems.Length; i++)
        {
            var y = 60 + (i * rowH) + 4;
            using var brush = new SolidBrush(i % 2 == 0
                ? Color.FromArgb(200, 190, 160)
                : Color.FromArgb(140, 130, 110));
            g.DrawString(SampleItems[i], itemFont, brush, 14, y);
        }
    }

    private void DrawOverlayElements(Graphics g, OcrResolutionProfile profile)
    {
        var cx = profile.CaptureOffsetX;
        var cy = profile.CaptureOffsetY;
        var cw = profile.CaptureWidth;
        var ch = profile.CaptureHeight;

        if (_showScanZone)
            DrawScanZone(g, profile);

        if (_showSetupOverlay)
        {
            DrawSetupOverlay(g, profile);
            return;
        }

        if (_showDebugOverlay)
        {
            var panelWidth = cx - 20;
            DrawDebugPanelBackground(g, cy, ch, panelWidth);
            DrawDebugText(g, cx, cy, ch, panelWidth);
        }

        using var boxPen = new Pen(Color.Red, 3);
        g.DrawRectangle(boxPen, cx, cy, cw, ch);

        if (_showPriceOverlay)
            DrawPriceText(g, cx, cy, cw, ch);
    }

    private static void DrawScanZone(Graphics g, OcrResolutionProfile profile)
    {
        var cx = profile.CaptureOffsetX;
        var cy = profile.CaptureOffsetY;
        var cw = profile.CaptureWidth;
        var ch = profile.CaptureHeight;

        var scanLeft = cx + (int)(cw * ListDetector.LeftFraction);
        var scanRight = cx + (int)(cw * ListDetector.RightFraction);
        var scanY = cy + (int)(ch * ListDetector.TopRowFraction);

        using var scanPen = new Pen(Color.FromArgb(220, 255, 255, 0), 2f)
        {
            DashStyle = DashStyle.Dash
        };
        g.DrawLine(scanPen, scanLeft, cy, scanLeft, scanY);
        g.DrawLine(scanPen, scanRight, cy, scanRight, scanY);

        using var dotBrush = new SolidBrush(Color.FromArgb(255, 255, 60, 60));
        g.FillEllipse(dotBrush, scanLeft - 3, scanY - 3, 7, 7);
        g.FillEllipse(dotBrush, scanRight - 3, scanY - 3, 7, 7);
    }

    private static void DrawDebugPanelBackground(Graphics g, int boxY, int boxH, int panelWidth)
    {
        using var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        g.FillRectangle(dimBrush, 0, boxY, panelWidth, boxH);
    }

    private static void DrawDebugText(Graphics g, int boxX, int boxY, int boxH, int panelWidth)
    {
        const float fontSizePx = 18f;
        const float minSizePx = 10f;
        var maxWidth = panelWidth - 16;

        using var font = new Font("Segoe UI", fontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fillBrush = new SolidBrush(Color.Red);
        using var outlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var rowH = boxH / Math.Max(1, SampleItems.Length);
        for (var i = 0; i < SampleItems.Length; i++)
        {
            var text = SampleItems[i];
            if (string.IsNullOrEmpty(text)) continue;

            var y = boxY + (i * rowH);
            var textW = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
            var scale = textW > maxWidth ? maxWidth / textW : 1f;
            var effectiveSize = Math.Max(minSizePx, fontSizePx * scale);

            Font lineFont;
            if (Math.Abs(effectiveSize - fontSizePx) < 0.5f)
            {
                lineFont = font;
            }
            else
            {
                lineFont = new Font("Segoe UI", effectiveSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            var emSize = g.DpiY * lineFont.SizeInPoints / 72f;
            var finalW = g.MeasureString(text, lineFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            var x = boxX - finalW - 8;
            var yOff = (font.GetHeight(g) - lineFont.GetHeight(g)) / 2f;

            using var path = new GraphicsPath();
            path.AddString(text, lineFont.FontFamily, (int)lineFont.Style, emSize, new PointF(x, y + yOff), StringFormat.GenericTypographic);
            g.DrawPath(outlinePen, path);
            g.FillPath(fillBrush, path);

            if (lineFont != font) lineFont.Dispose();
        }
    }

    private static void DrawPriceText(Graphics g, int boxX, int boxY, int boxW, int boxH)
    {
        var px = boxX + boxW + 5;

        using var font = new Font("Segoe UI", 21f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var greenBrush = new SolidBrush(Color.FromArgb(255, 88, 255, 122));
        using var grayBrush = new SolidBrush(Color.FromArgb(255, 140, 140, 140));
        using var outlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var rowH = boxH / Math.Max(1, SamplePrices.Length);
        var emSize = g.DpiY * font.SizeInPoints / 72f;
        for (var i = 0; i < SamplePrices.Length; i++)
        {
            var text = SamplePrices[i];
            if (string.IsNullOrEmpty(text)) continue;

            var y = boxY + (i * rowH) + ((rowH - (int)font.GetHeight(g)) / 2);
            var brush = text.StartsWith("N/A", StringComparison.Ordinal) ? grayBrush : greenBrush;

            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, emSize, new PointF(px + 2, y), StringFormat.GenericTypographic);
            g.DrawPath(outlinePen, path);
            g.FillPath(brush, path);
        }
    }

    private static void DrawSetupOverlay(Graphics g, OcrResolutionProfile profile)
    {
        var cx = profile.CaptureOffsetX;
        var cy = profile.CaptureOffsetY;
        var cw = profile.CaptureWidth;
        var ch = profile.CaptureHeight;

        using var boxPen = new Pen(Color.Red, 3f);
        g.DrawRectangle(boxPen, cx, cy, cw, ch);

        DrawResizeHandles(g, cx, cy, cw, ch);

        const int topBarHeight = 80;
        var ctrlX = Math.Max(20, cx);
        var ctrlY = Math.Max(20, cy - topBarHeight - 20);

        using var ctrlBg = new SolidBrush(Color.FromArgb(28, 32, 40));
        var ctrlW = 460;
        var ctrlH = 80;
        g.FillRectangle(ctrlBg, ctrlX, ctrlY, ctrlW, ctrlH);

        using var titleFont = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.White);
        g.DrawString("Position the red box over the PoE2 item list panel", titleFont, titleBrush, ctrlX + 4, ctrlY + 4);

        using var hintFont = new Font("Segoe UI", 9f, GraphicsUnit.Pixel);
        using var hintBrush = new SolidBrush(Color.FromArgb(180, 185, 195));
        g.DrawString("Drag inside to move. Drag edges or corners to resize.", hintFont, hintBrush, ctrlX + 4, ctrlY + 28);

        using var btnBrush = new SolidBrush(Color.FromArgb(52, 211, 153));
        using var btnTextBrush = new SolidBrush(Color.White);
        using var btnFont = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        var btnW = 180;
        var btnH = 30;
        g.FillRectangle(btnBrush, ctrlX + 4, ctrlY + 50, btnW, btnH);
        using var btnBorderPen = new Pen(Color.Black, 2);
        g.DrawRectangle(btnBorderPen, ctrlX + 4, ctrlY + 50, btnW, btnH);
        var btnText = "Confirm Position";
        var btnTextSize = g.MeasureString(btnText, btnFont);
        g.DrawString(btnText, btnFont, btnTextBrush, ctrlX + 4 + (btnW - btnTextSize.Width) / 2, ctrlY + 52);

        var exampleX = Math.Max(cx + cw + 80, 500);
        var exampleY = ctrlY;
        var exampleW = 340;
        var exampleH = 260;

        using var exampleLabelFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var exampleLabelBrush = new SolidBrush(Color.FromArgb(200, 200, 210));
        g.DrawString("Example:", exampleLabelFont, exampleLabelBrush, exampleX, exampleY);

        using var exampleBgBrush = new SolidBrush(Color.FromArgb(20, 24, 30));
        using var exampleBorderPen = new Pen(Color.FromArgb(100, 100, 110), 1);
        g.FillRectangle(exampleBgBrush, exampleX, exampleY + 20, exampleW, exampleH);
        g.DrawRectangle(exampleBorderPen, exampleX, exampleY + 20, exampleW, exampleH);

        using var exampleTextFont = new Font("Segoe UI", 10f, GraphicsUnit.Pixel);
        using var exampleTextBrush = new SolidBrush(Color.FromArgb(150, 150, 160));
        var placeText = "(example.png)";
        var placeSize = g.MeasureString(placeText, exampleTextFont);
        g.DrawString(placeText, exampleTextFont, exampleTextBrush,
            exampleX + (exampleW - placeSize.Width) / 2,
            exampleY + 20 + (exampleH - placeSize.Height) / 2);
    }

    private static void DrawResizeHandles(Graphics g, int cx, int cy, int cw, int ch)
    {
        const int hs = 12;
        using var handleBrush = new SolidBrush(Color.FromArgb(220, 255, 80, 80));
        var handles = new[]
        {
            new Rectangle(cx - hs/2, cy - hs/2, hs, hs),
            new Rectangle(cx + cw - hs/2, cy - hs/2, hs, hs),
            new Rectangle(cx - hs/2, cy + ch - hs/2, hs, hs),
            new Rectangle(cx + cw - hs/2, cy + ch - hs/2, hs, hs),
            new Rectangle(cx + cw/2 - hs/2, cy - hs/2, hs, hs),
            new Rectangle(cx + cw/2 - hs/2, cy + ch - hs/2, hs, hs),
            new Rectangle(cx - hs/2, cy + ch/2 - hs/2, hs, hs),
            new Rectangle(cx + cw - hs/2, cy + ch/2 - hs/2, hs, hs),
        };
        foreach (var h in handles)
            g.FillEllipse(handleBrush, h);
    }

    private static void DrawInfoBar(Graphics g, string key, OcrResolutionProfile profile, int gameH)
    {
        var y = gameH + 12;
        using var font = new Font("Consolas", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(200, 200, 200));

        var info = $"Profile: {key}  |  Capture: X={profile.CaptureOffsetX} Y={profile.CaptureOffsetY} W={profile.CaptureWidth} H={profile.CaptureHeight}";
        g.DrawString(info, font, brush, 0, y);

        var scanInfo = $"  |  Scan: L={ListDetector.LeftFraction:F2} R={ListDetector.RightFraction:F2} TopRow={ListDetector.TopRowFraction:F2}";
        g.DrawString(scanInfo, font, brush, 0, y + 18);

        var nav = "← → cycle  |  +/- zoom  |  R reset  |  O load screenshot  |  S=scan P=prices U=setup";
        g.DrawString(nav, font, brush, 0, y + 36);
    }

    private static (int w, int h) ParseResolution(string key)
    {
        var parts = key.Split('x');
        int w = parts.Length >= 1 && int.TryParse(parts[0], out var pw) ? pw : 1920;
        int h = parts.Length >= 2 && int.TryParse(parts[1], out var ph) ? ph : 1080;
        return (w, h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _screenshot?.Dispose();
        base.Dispose(disposing);
    }
}
