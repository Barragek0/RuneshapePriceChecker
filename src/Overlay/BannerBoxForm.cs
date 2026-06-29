using System.Drawing.Drawing2D;

namespace RuneshapePriceChecker.Overlay;

internal sealed class BannerBoxForm : OverlayFormBase
{
    protected override bool ClickThrough => true;

    private const float BaseFontSizePx = 21f;
    private string? _message;
    private float _scaleFactor = 1f;

    public bool ShowOutline { get; set; } = true;

    public void SetScaleFactor(float scaleFactor)
    {
        _scaleFactor = scaleFactor;
        if (!IsDisposed && Visible)
            Invalidate();
    }

    public void SetMessage(string? message)
    {
        if (InvokeRequired)
        {
            _ = BeginInvoke(new Action<string?>(SetMessage), message);
            return;
        }

        _message = message;
        if (!IsDisposed && Visible)
            Invalidate();
    }

    public void SafeShow(int x, int y, int width, int height)
    {
        SafeShow(new Rectangle(x, y, width, height));
    }

    public override void SafeHide()
    {
        base.SafeHide();
        if (InvokeRequired)
            BeginInvoke(() => Bounds = new Rectangle(-32000, -32000, 1, 1));
        else
            Bounds = new Rectangle(-32000, -32000, 1, 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (string.IsNullOrWhiteSpace(_message)) return;

        ConfigureGraphics(e.Graphics, fullQuality: false);

        var boxHeight = ClientRectangle.Height;
        var boxWidth = ClientRectangle.Width;

        // Orange outline — only when debug overlay is enabled
        if (ShowOutline)
            using (var outlinePen = new Pen(Color.FromArgb(255, 220, 120, 30), 2f))
                e.Graphics.DrawRectangle(outlinePen, 0, 0, boxWidth - 1, boxHeight - 1);

        // Draw banner text left-aligned, sized to match the pricing overlay
        var rawLines = _message.Split('\n');
        var scaledFontSize = (float)Math.Round(BaseFontSizePx * _scaleFactor);
        using var baseFont = new Font("Segoe UI", scaledFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        var lineH = baseFont.GetHeight(e.Graphics);

        // Same outline style as pricing overlay's DrawOutlinedText
        using var textOutlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var iconOutlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 1.0f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // Calculate total text height so we can center the block vertically
        var visibleLines = 0;
        foreach (var rawLine in rawLines)
            if (!string.IsNullOrWhiteSpace(rawLine)) visibleLines++;
        if (visibleLines == 0) return;

        var totalTextHeight = visibleLines * lineH;
        var startY = (boxHeight - totalTextHeight) / 2f;
        if (startY < 2f) startY = 2f;

        var textX = 4f;
        var currentY = startY;

        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            // Parse optional \0RRGGBB color prefix (used by volume warning lines)
            var displayLine = rawLine;
            Color lineColor;
            if (displayLine.Length > 7 && displayLine[0] == '\0')
            {
                var colorArgb = displayLine.Substring(1, 6);
                displayLine = displayLine[7..];
                lineColor = int.TryParse(colorArgb, System.Globalization.NumberStyles.HexNumber, null, out var argb)
                    ? Color.FromArgb(255, Color.FromArgb(argb))
                    : Color.Red;
            }
            else
            {
                lineColor = Color.Red;
            }

            // Check if the line starts with the warning icon
            var iconIdx = displayLine.IndexOf('\u26A0');
            if (iconIdx >= 0)
            {
                // Split at "⚠=": render icon at larger size, message at base size
                var eqIdx = displayLine.IndexOf('=', iconIdx);
                string iconPart, msgPart;
                if (eqIdx >= 0)
                {
                    iconPart = displayLine[iconIdx..(eqIdx + 1)]; // includes ⚠=
                    msgPart = displayLine[(eqIdx + 1)..];         // after =
                }
                else
                {
                    iconPart = displayLine[iconIdx..]; // just ⚠
                    msgPart = "";
                }

                // Same icon sizing as pricing overlay: 1.35× base, 1.0f outline, vertical offset
                using var iconFont = new Font(baseFont.FontFamily, baseFont.Size * 1.35f, baseFont.Style, baseFont.Unit);
                var iconSize = e.Graphics.MeasureString(iconPart, iconFont, PointF.Empty, StringFormat.GenericTypographic);
                var msgSize = e.Graphics.MeasureString(msgPart, baseFont, PointF.Empty, StringFormat.GenericTypographic);

                // Shift icon baseline up to visually center with adjacent text
                var icY = currentY - (baseFont.Size * 0.14f);

                // Draw icon part with thin outline
                var iconEm = e.Graphics.DpiY * iconFont.SizeInPoints / 72f;
                using var iconPath = new GraphicsPath();
                iconPath.AddString(iconPart, iconFont.FontFamily, (int)iconFont.Style, iconEm,
                    new PointF(textX, icY), StringFormat.GenericTypographic);
                e.Graphics.DrawPath(iconOutlinePen, iconPath);
                using var iconBrush = new SolidBrush(lineColor);
                e.Graphics.FillPath(iconBrush, iconPath);

                // Draw message part at base size with 2.2f outline
                var msgEm = e.Graphics.DpiY * baseFont.SizeInPoints / 72f;
                using var msgPath = new GraphicsPath();
                msgPath.AddString(msgPart, baseFont.FontFamily, (int)baseFont.Style, msgEm,
                    new PointF(textX + iconSize.Width, currentY), StringFormat.GenericTypographic);
                e.Graphics.DrawPath(textOutlinePen, msgPath);
                using var msgBrush = new SolidBrush(lineColor);
                e.Graphics.FillPath(msgBrush, msgPath);
            }
            else
            {
                // Plain text line (unpriceable message, separator, etc.) — 2.2f outline matches pricing overlay
                var emSize = e.Graphics.DpiY * baseFont.SizeInPoints / 72f;
                using var path = new GraphicsPath();
                path.AddString(displayLine, baseFont.FontFamily, (int)baseFont.Style, emSize,
                    new PointF(textX, currentY), StringFormat.GenericTypographic);
                e.Graphics.DrawPath(textOutlinePen, path);
                using var fillBrush = new SolidBrush(lineColor);
                e.Graphics.FillPath(fillBrush, path);
            }

            currentY += lineH;
        }
    }
}
