using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Overlay;

internal sealed class BannerForm : Form
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
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            _isHidden = false;
            BeginInvoke(new Action<int, int>(SafeShow), x, y);
            return;
        }

        _isHidden = false;
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
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            if (_isHidden) return;
            _isHidden = true;
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
