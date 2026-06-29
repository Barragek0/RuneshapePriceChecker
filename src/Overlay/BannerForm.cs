namespace RuneshapePriceChecker.Overlay;

internal sealed class BannerForm : OverlayFormBase
{
    protected override bool ClickThrough => true;

    private string? _message;
    private Color _lineColorOverride;

    public void SetMessage(string? message, Color? lineColor = null)
    {
        // Marshal to the UI thread to avoid a race between SetMessage (called from
        // the worker thread) and OnPaint (UI thread).  Without this, the UI thread
        // can read _message between the null check and the .Split('\n') call, causing
        // a crash that silently terminates the process.
        if (InvokeRequired)
        {
            _ = BeginInvoke(new Action<string?, Color?>(SetMessage), message, lineColor);
            return;
        }

        _message = message;
        if (lineColor.HasValue)
            _lineColorOverride = lineColor.Value;
        if (!IsDisposed && Visible)
            Invalidate();
    }

    public void SafeShow(int x, int y)
    {
        SafeShow(new Rectangle(x, y, 400, 50));
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

        const float fontSizePx = 21f;
        using var baseFont = new Font("Segoe UI", fontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
        var lineHeight = baseFont.GetHeight(e.Graphics);
        var rawLines = _message.Split('\n');

        using var outlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
        {
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        // Parse lines: each line may start with \0RRGGBB to specify a non-default color
        var lines = new List<(string Text, Color Color)>(rawLines.Length);
        using var defaultBrush = new SolidBrush(Color.Red);
        foreach (var rawLine in rawLines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            if (rawLine.Length > 7 && rawLine[0] == '\0')
            {
                var colorArgb = rawLine.Substring(1, 6);
                var text = rawLine[7..];
                if (int.TryParse(colorArgb, System.Globalization.NumberStyles.HexNumber, null, out var argb))
                    lines.Add((text, Color.FromArgb(255, Color.FromArgb(argb))));
                else
                    lines.Add((text, _lineColorOverride != default ? _lineColorOverride : Color.Red));
            }
            else
            {
                lines.Add((rawLine, _lineColorOverride != default ? _lineColorOverride : Color.Red));
            }
        }

        var totalTextHeight = lines.Count * lineHeight;
        var startY = (50 - totalTextHeight) / 2f;

        for (var li = 0; li < lines.Count; li++)
        {
            var (line, color) = lines[li];
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Warning icon line: render the ⚠ at a much larger size, then the message
            // text at base size with a thinner outline. Lines without an icon render normally.
            var isIconLine = line.Contains('\u26A0');
            if (isIconLine)
            {
                // Split on "⚠=" — render the icon large, the message at base size
                var eqIdx = line.IndexOf("\u26A0=", StringComparison.Ordinal);
                if (eqIdx >= 0)
                {
                    var iconPart = line[..(eqIdx + 1)];  // includes ⚠
                    var msgPart = line[(eqIdx + 2)..];     // after =

                    // Measure the icon and message separately
                    using var iconFont = new Font(baseFont.FontFamily, baseFont.Size * 1.35f, baseFont.Style, baseFont.Unit);
                    var iconSize = e.Graphics.MeasureString(iconPart, iconFont, PointF.Empty, StringFormat.GenericTypographic);
                    var msgSize = e.Graphics.MeasureString(msgPart, baseFont, PointF.Empty, StringFormat.GenericTypographic);
                    var totalW = iconSize.Width + msgSize.Width;
                    var baseX = Math.Max(0f, (400 - totalW) / 2f);
                    var lineY = startY + (li * lineHeight);

                    // Icon: slightly offset for baseline
                    var icY = lineY - (baseFont.Size * 0.14f);
                    var iconEm = e.Graphics.DpiY * iconFont.SizeInPoints / 72f;
                    using var iconPath = new System.Drawing.Drawing2D.GraphicsPath();
                    iconPath.AddString(iconPart, iconFont.FontFamily, (int)iconFont.Style, iconEm,
                        new PointF(baseX, icY), StringFormat.GenericTypographic);
                    using var iconPen = new Pen(Color.FromArgb(255, 0, 0, 0), 1.0f)
                    {
                        LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                        StartCap = System.Drawing.Drawing2D.LineCap.Round,
                        EndCap = System.Drawing.Drawing2D.LineCap.Round
                    };
                    e.Graphics.DrawPath(iconPen, iconPath);
                    using var iconBrush = new SolidBrush(color);
                    e.Graphics.FillPath(iconBrush, iconPath);

                    // Message text at base size
                    var msgEm = e.Graphics.DpiY * baseFont.SizeInPoints / 72f;
                    using var msgPath = new System.Drawing.Drawing2D.GraphicsPath();
                    msgPath.AddString(msgPart, baseFont.FontFamily, (int)baseFont.Style, msgEm,
                        new PointF(baseX + iconSize.Width, lineY), StringFormat.GenericTypographic);
                    using var msgPen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
                    {
                        LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                        StartCap = System.Drawing.Drawing2D.LineCap.Round,
                        EndCap = System.Drawing.Drawing2D.LineCap.Round
                    };
                    e.Graphics.DrawPath(msgPen, msgPath);
                    using var msgBrush = new SolidBrush(color);
                    e.Graphics.FillPath(msgBrush, msgPath);

                    continue;
                }
            }

            // Non-icon line: render as before
            var textSize = e.Graphics.MeasureString(line, baseFont, PointF.Empty, StringFormat.GenericTypographic);
            var x = Math.Max(0f, (400 - textSize.Width) / 2f);
            var y = startY + (li * lineHeight);

            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddString(line, baseFont.FontFamily, (int)baseFont.Style,
                e.Graphics.DpiY * baseFont.SizeInPoints / 72f,
                new PointF(x, y), StringFormat.GenericTypographic);
            using var linePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.2f)
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            e.Graphics.DrawPath(linePen, path);
            using var fillBrush = new SolidBrush(color);
            e.Graphics.FillPath(fillBrush, path);
        }
    }
}
