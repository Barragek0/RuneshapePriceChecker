using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Overlay;

internal class OverlayFormBase : Form
{
    protected static readonly Color TransparencyChroma = Color.FromArgb(1, 2, 3);
    internal volatile bool IsHidden;

    protected OverlayFormBase()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = TransparencyChroma;
        TransparencyKey = TransparencyChroma;
        DoubleBuffered = true;
        Cursor = Cursors.Default;
    }

    protected override bool ShowWithoutActivation => true;

    private const int WM_SETCURSOR = 0x0020;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SETCURSOR)
        {
            if (Cursor is not null)
            {
                _ = SetCursor(Cursor.Handle);
                m.Result = 1;
                return;
            }
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
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00080000;  // WS_EX_LAYERED
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected void SafeShow(Rectangle bounds)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            IsHidden = false;
            _ = BeginInvoke(new Action<Rectangle>(SafeShow), bounds);
            return;
        }

        IsHidden = false;
        Bounds = bounds;
        PinTopMost();
        Invalidate();

        if (!Visible)
        {
            Show();
            PinTopMost();
        }
    }

    public virtual void SafeHide()
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            if (IsHidden) return;
            IsHidden = true;
            _ = BeginInvoke(new Action(SafeHide));
            return;
        }

        Hide();
    }

    public void SafeClose()
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            _ = BeginInvoke(new Action(SafeClose));
            return;
        }

        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PinTopMost();
    }

    protected void PinTopMost()
    {
        if (!IsHandleCreated) return;
        _ = NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            Left, Top, Width, Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_NOSENDCHANGING);
    }

    internal static void ConfigureGraphics(Graphics g, bool fullQuality = true)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (fullQuality)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
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
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);
    }
}
