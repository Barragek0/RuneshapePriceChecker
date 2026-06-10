using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Overlay;

public sealed class SetupPromptForm : Form
{
    private readonly Rectangle _captureRect;
    private int _formOriginX;
    private int _formOriginY;
    private Rectangle _screenBounds;
    private bool _continueWasClicked;

    public bool ContinueWasClicked => _continueWasClicked;

    public SetupPromptForm(Rectangle initialRect)
    {
        _captureRect = initialRect;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        var screen = Screen.FromPoint(new Point(initialRect.X, initialRect.Y));
        _formOriginX = screen.Bounds.X;
        _formOriginY = screen.Bounds.Y;
        _screenBounds = screen.Bounds;

        var panelW = 480;
        var panelH = 140;
        var panelX = _formOriginX + 40;
        var panelY = _formOriginY + 40;
        Bounds = new Rectangle(panelX, panelY, panelW, panelH);

        BuildControls();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 | 0x00080000 | 0x00000020 | 0x08000000;
            return cp;
        }
    }

    private void BuildControls()
    {
        BackColor = Color.FromArgb(28, 32, 40);
        TransparencyKey = Color.FromArgb(1, 2, 3);

        var messageLabel = new Label
        {
            Text = "Open the league panel interface in the game,\r\nthen press Continue to set up the overlay.",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(28, 32, 40),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 70
        };
        Controls.Add(messageLabel);

        var continueBtn = new Button
        {
            Text = "Continue",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 211, 153),
            ForeColor = Color.White,
            Size = new Size(160, 36),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        continueBtn.FlatAppearance.BorderSize = 2;
        continueBtn.FlatAppearance.BorderColor = Color.Black;
        continueBtn.Location = new Point((ClientSize.Width - continueBtn.Width) / 2, ClientSize.Height - continueBtn.Height - 16);
        continueBtn.Click += (_, _) =>
        {
            _continueWasClicked = true;
            Close();
        };
        Controls.Add(continueBtn);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PinTopMost();
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

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_WINDOWPOSCHANGED = 0x0047;
        const int HTCLIENT = 1;

        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HTCLIENT;
            return;
        }

        if (m.Msg == WM_WINDOWPOSCHANGED)
        {
            base.WndProc(ref m);
            var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            if ((wp.flags & SWP_NOZORDER) == 0)
                PinTopMost();
            return;
        }

        base.WndProc(ref m);
    }

    private const uint SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
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
