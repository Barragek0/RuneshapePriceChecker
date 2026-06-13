namespace RuneshapePriceChecker.Overlay;

internal sealed class BannerForm : OverlayFormBase
{
    private string? _message;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000020;  // WS_EX_TRANSPARENT (click-through, display only)
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
        base.SafeShow(new Rectangle(x, y, 400, 50));
    }

    public override void SafeHide()
    {
        base.SafeHide();
        Bounds = new Rectangle(-32000, -32000, 1, 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (string.IsNullOrWhiteSpace(_message)) return;

        ConfigureGraphics(e.Graphics, fullQuality: false);

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
