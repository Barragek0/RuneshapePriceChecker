using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    private readonly Dictionary<string, OcrResolutionProfile> _profiles;
    private string[] _profileKeys = [];
    private int _currentIndex;
    private float _userScale = 1f;
    private Bitmap? _screenshot;
    private readonly OpenFileDialog _openDialog = new() { Filter = "PNG Images|*.png|JPEG Images|*.jpg;*.jpeg|All Files|*.*" };

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
        "N/A", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A"
    ];

    public VisualizerForm()
    {
        _profiles = OcrResolutionProfiles.All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        _profileKeys = [.. _profiles.Keys.OrderBy(k => k)];

        Text = "Resolution Visualizer — O to load screenshot, ← → cycle, +/- zoom, R reset";
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
            case Keys.D:
                _currentIndex = (_currentIndex + 1) % _profileKeys.Length;
                Invalidate();
                break;
            case Keys.Left:
            case Keys.A:
                _currentIndex = (_currentIndex - 1 + _profileKeys.Length) % _profileKeys.Length;
                Invalidate();
                break;
            case Keys.Oemplus:
            case Keys.Add:
                _userScale = Math.Min(3f, _userScale + 0.1f);
                UpdateFormSize();
                Invalidate();
                break;
            case Keys.OemMinus:
            case Keys.Subtract:
                _userScale = Math.Max(0.1f, _userScale - 0.1f);
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
                    try
                    {
                        _screenshot = new Bitmap(_openDialog.FileName);
                    }
                    catch
                    {
                        _screenshot = null;
                    }

                    UpdateFormSize();
                    Invalidate();
                }

                break;
        }
    }

    private void UpdateFormSize()
    {
        if (_profileKeys.Length == 0) return;
        var profile = _profiles[_profileKeys[_currentIndex]];
        var w = (int)(profile.WindowWidth * _userScale) + 40;
        var h = (int)(profile.WindowHeight * _userScale) + 80;
        var screen = Screen.FromControl(this).WorkingArea;
        w = Math.Min(w, screen.Width - 100);
        h = Math.Min(h, screen.Height - 100);
        ClientSize = new Size(w, h);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (_profileKeys.Length == 0) return;
        var profile = _profiles[_profileKeys[_currentIndex]];
        var status = profile.Confirmed ? "Confirmed" : "Untested";
        Text = $"Resolution Visualizer — {profile.Key} ({status}) — Zoom: {_userScale:F1}x — ← → cycle, +/- zoom, O load screenshot";
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

        var profile = _profiles[_profileKeys[_currentIndex]];
        var gameW = profile.WindowWidth;
        var gameH = profile.WindowHeight;

        var offsetX = (ClientSize.Width - (gameW * _userScale)) / 2f;
        var offsetY = 20f;

        g.TranslateTransform(offsetX, offsetY);
        g.ScaleTransform(_userScale, _userScale);

        DrawGameWindow(g, gameW, gameH);

        g.ResetTransform();
        g.TranslateTransform(offsetX, offsetY);
        g.ScaleTransform(_userScale, _userScale);

        DrawOverlayElements(g, profile);
        DrawInfoBar(g, profile, gameH);
    }

    private void DrawGameWindow(Graphics g, int w, int h)
    {
        if (_screenshot is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
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
            g.DrawString("Path of Exile 2  —  Borderless Window  (O to load screenshot)", titleFont, titleBrush, 8, 6);

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
        var panelWidth = profile.CaptureOffsetX - 57;

        using var dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        g.FillRectangle(dimBrush, cx - panelWidth - 30, cy, panelWidth + 30, ch);
        g.FillRectangle(dimBrush, cx + cw + 5, cy, 220, ch);

        using var boxPen = new Pen(Color.Red, 3);
        g.DrawRectangle(boxPen, cx, cy, cw, ch);

        DrawDebugText(g, cx, cy, ch, panelWidth);
        DrawPriceText(g, cx + cw + 5, cy, ch);
    }

    private static void DrawDebugText(Graphics g, int boxX, int boxY, int boxH, int panelWidth)
    {
        const float fontSizePx = 18f;
        const float minSizePx = 10f;
        var maxWidth = panelWidth - 8;

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
            var text = SamplePrices[i];
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

    private static void DrawPriceText(Graphics g, int px, int py, int boxH)
    {
        using var font = new Font("Segoe UI", 21f, FontStyle.Bold, GraphicsUnit.Pixel);
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

            var y = py + (i * rowH) + ((rowH - (int)font.GetHeight(g)) / 2);
            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, emSize, new PointF(px + 2, y), StringFormat.GenericTypographic);
            g.DrawPath(outlinePen, path);
            g.FillPath(grayBrush, path);
        }
    }

    private void DrawInfoBar(Graphics g, OcrResolutionProfile profile, int gameH)
    {
        var status = profile.Confirmed ? "CONFIRMED" : "UNTESTED";
        var y = gameH + 12;
        using var font = new Font("Consolas", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(200, 200, 200));
        using var statusBrush = new SolidBrush(profile.Confirmed
            ? Color.FromArgb(88, 255, 122)
            : Color.FromArgb(255, 196, 54));

        var info = $"Profile: {profile.Key}  |  Capture: X={profile.CaptureOffsetX} Y={profile.CaptureOffsetY} W={profile.CaptureWidth} H={profile.CaptureHeight}  |  Status: ";
        g.DrawString(info, font, brush, 0, y);
        var infoW = g.MeasureString(info, font).Width;
        g.DrawString(status, font, statusBrush, infoW, y);

        var nav = "← → cycle  |  +/- zoom  |  R reset  |  O load 1080p screenshot";
        g.DrawString(nav, font, brush, 0, y + 18);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _screenshot?.Dispose();
        base.Dispose(disposing);
    }
}
