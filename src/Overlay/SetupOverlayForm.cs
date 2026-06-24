using System.Drawing.Drawing2D;

namespace RuneshapePriceChecker.Overlay;

internal sealed class SetupOverlayForm : OverlayFormBase
{
    private const int TopBarHeight = 80;

    private Rectangle _captureRect;
    private readonly int _formOriginX;
    private readonly int _formOriginY;
    private Rectangle _screenBounds;
    private Rectangle _gameBounds;
    private bool _dragging;
    private bool _resizing;
    private Point _dragStart;
    private Rectangle _dragStartRect;
    private int _resizeEdge;
    private Button? _confirmBtn;
    private Button? _backBtn;
    private Label? _titleLabel;
    private Label? _hintLabel;
    private PictureBox? _exampleBox;
    private Label? _exampleLabel;

    public event Action<Rectangle>? SetupConfirmed;
    public event Action? GoBackClicked;

    public SetupOverlayForm(Rectangle initialRect, Rectangle gameBounds)
    {
        _captureRect = initialRect;
        _gameBounds = gameBounds;

        var screen = Screen.FromPoint(new Point(initialRect.X, initialRect.Y));
        _formOriginX = screen.Bounds.X;
        _formOriginY = screen.Bounds.Y;
        _screenBounds = screen.Bounds;
        Bounds = screen.Bounds;

        var ctrlX = Math.Max(20, initialRect.X - _formOriginX);
        var ctrlY = Math.Max(20, initialRect.Y - _formOriginY - TopBarHeight - 20);
        BuildControls(ctrlX, ctrlY);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PinTopMost();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const int HTTRANSPARENT = -1;

        if (m.Msg == WM_NCHITTEST)
        {
            var pt = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));

            var boxRect = GetBoxRect();
            var inBox = boxRect.Contains(pt) || HitTest(boxRect, pt) != 0;

            var inControls = false;
            if (_titleLabel is not null && _titleLabel.Bounds.Contains(pt)) inControls = true;
            if (_hintLabel is not null && _hintLabel.Bounds.Contains(pt)) inControls = true;
            if (_confirmBtn is not null && _confirmBtn.Bounds.Contains(pt)) inControls = true;
            if (_exampleLabel is not null && _exampleLabel.Bounds.Contains(pt)) inControls = true;
            if (_exampleBox is not null && _exampleBox.Bounds.Contains(pt)) inControls = true;

            if (inBox || inControls)
            {
                m.Result = HTCLIENT;
                return;
            }

            m.Result = HTTRANSPARENT;
            return;
        }

        base.WndProc(ref m);
    }

    private void BuildControls(int x, int y)
    {
        _titleLabel = new Label
        {
            Text = "Position the red box over the PoE2 item list panel",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(28, 32, 40),
            Location = new Point(x, y),
            AutoSize = true
        };
        Controls.Add(_titleLabel);

        _hintLabel = new Label
        {
            Text = "Drag inside to move. Drag edges or corners to resize.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(180, 185, 195),
            BackColor = Color.FromArgb(28, 32, 40),
            Location = new Point(x, y + 24),
            AutoSize = true
        };
        Controls.Add(_hintLabel);

        _confirmBtn = new Button
        {
            Text = "Confirm Position",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 211, 153),
            ForeColor = Color.White,
            Location = new Point(x, y + 50),
            Size = new Size(180, 30),
            Cursor = Cursors.Hand
        };
        _confirmBtn.FlatAppearance.BorderSize = 2;
        _confirmBtn.FlatAppearance.BorderColor = Color.Black;
        _confirmBtn.Click += (_, _) =>
        {
            SetupConfirmed?.Invoke(_captureRect);
            Close();
        };
        Controls.Add(_confirmBtn);

        var backBtn = new Button
        {
            Text = "Go Back",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(75, 85, 99),
            ForeColor = Color.White,
            Location = new Point(x + 190, y + 50),
            Size = new Size(110, 30),
            Cursor = Cursors.Hand
        };
        backBtn.FlatAppearance.BorderSize = 2;
        backBtn.FlatAppearance.BorderColor = Color.Black;
        backBtn.Click += (_, _) =>
        {
            GoBackClicked?.Invoke();
            Close();
        };
        _backBtn = backBtn;
        Controls.Add(backBtn);

        var exampleW = 340;
        var exampleH = 260;
        Image? exampleImage = null;

        try
        {
            var asm = typeof(SetupOverlayForm).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("example.png", StringComparison.OrdinalIgnoreCase));
            if (resourceName is not null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    exampleImage = Image.FromStream(stream);
                    exampleW = exampleImage.Width;
                    exampleH = exampleImage.Height;
                }
            }
        }
        catch { exampleImage?.Dispose(); exampleImage = null; }

        var exampleX = Math.Max(
            _captureRect.X - _formOriginX + _captureRect.Width + 80,
            (int)(_screenBounds.Width * 0.375));
        var exampleY = y;

        _exampleLabel = new Label
        {
            Text = "Example:",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 210),
            BackColor = Color.FromArgb(28, 32, 40),
            Location = new Point(exampleX, exampleY),
            AutoSize = true
        };
        Controls.Add(_exampleLabel);

        _exampleBox = new PictureBox
        {
            Location = new Point(exampleX, exampleY + 20),
            Size = new Size(exampleW, exampleH),
            BackColor = Color.FromArgb(20, 24, 30),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            Image = exampleImage
        };
        Controls.Add(_exampleBox);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var boxRect = GetBoxRect();

        using var boxPen = new Pen(Color.Red, 3f);
        g.DrawRectangle(boxPen, boxRect);

        DrawResizeHandles(g, boxRect);
    }

    private static void DrawResizeHandles(Graphics g, Rectangle rect)
    {
        const int hs = 12;
        using var handleBrush = new SolidBrush(Color.FromArgb(220, 255, 80, 80));
        var handles = new[]
        {
            new Rectangle(rect.Left - hs/2, rect.Top - hs/2, hs, hs),
            new Rectangle(rect.Right - hs/2, rect.Top - hs/2, hs, hs),
            new Rectangle(rect.Left - hs/2, rect.Bottom - hs/2, hs, hs),
            new Rectangle(rect.Right - hs/2, rect.Bottom - hs/2, hs, hs),
            new Rectangle(rect.Left + rect.Width/2 - hs/2, rect.Top - hs/2, hs, hs),
            new Rectangle(rect.Left + rect.Width/2 - hs/2, rect.Bottom - hs/2, hs, hs),
            new Rectangle(rect.Left - hs/2, rect.Top + rect.Height/2 - hs/2, hs, hs),
            new Rectangle(rect.Right - hs/2, rect.Top + rect.Height/2 - hs/2, hs, hs),
        };
        foreach (var h in handles)
            g.FillEllipse(handleBrush, h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var boxRect = GetBoxRect();
        _resizeEdge = HitTest(boxRect, e.Location);

        if (_resizeEdge != 0)
            _resizing = true;
        else if (boxRect.Contains(e.Location))
            _dragging = true;
        else
            return;

        _dragStart = e.Location;
        _dragStartRect = _captureRect;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var boxRect = GetBoxRect();

        if (_dragging)
        {
            var dx = e.X - _dragStart.X;
            var dy = e.Y - _dragStart.Y;
            var newX = _dragStartRect.X + dx;
            var newY = _dragStartRect.Y + dy;
            newX = Math.Clamp(newX, _gameBounds.X, _gameBounds.X + _gameBounds.Width - _captureRect.Width);
            newY = Math.Clamp(newY, _gameBounds.Y, _gameBounds.Y + _gameBounds.Height - _captureRect.Height);
            _captureRect = new Rectangle(newX, newY, _dragStartRect.Width, _dragStartRect.Height);
            RepositionControls();
            Invalidate();
            Update();
        }
        else if (_resizing)
        {
            var dx = e.X - _dragStart.X;
            var dy = e.Y - _dragStart.Y;
            var newRect = _dragStartRect;

            if ((_resizeEdge & 1) != 0)
            {
                var newX = _dragStartRect.X + dx;
                var newW = _dragStartRect.Width - dx;
                if (newX < _gameBounds.X) { newW -= _gameBounds.X - newX; newX = _gameBounds.X; }
                if (newW < 20) { newX = _dragStartRect.Right - 20; newW = 20; }
                newRect = new Rectangle(newX, newRect.Y, newW, newRect.Height);
            }
            if ((_resizeEdge & 2) != 0)
            {
                var newW = _dragStartRect.Width + dx;
                if (newRect.X + newW > _gameBounds.X + _gameBounds.Width)
                    newW = _gameBounds.X + _gameBounds.Width - newRect.X;
                if (newW < 20) newW = 20;
                newRect = new Rectangle(newRect.X, newRect.Y, newW, newRect.Height);
            }
            if ((_resizeEdge & 4) != 0)
            {
                var newY = _dragStartRect.Y + dy;
                var newH = _dragStartRect.Height - dy;
                if (newY < _gameBounds.Y) { newH -= _gameBounds.Y - newY; newY = _gameBounds.Y; }
                if (newH < 20) { newY = _dragStartRect.Bottom - 20; newH = 20; }
                newRect = new Rectangle(newRect.X, newY, newRect.Width, newH);
            }
            if ((_resizeEdge & 8) != 0)
            {
                var newH = _dragStartRect.Height + dy;
                if (newRect.Y + newH > _gameBounds.Y + _gameBounds.Height)
                    newH = _gameBounds.Y + _gameBounds.Height - newRect.Y;
                if (newH < 20) newH = 20;
                newRect = new Rectangle(newRect.X, newRect.Y, newRect.Width, newH);
            }

            _captureRect = newRect;
            RepositionControls();
            Invalidate();
            Update();
        }
        else
        {
            var edge = HitTest(boxRect, e.Location);
            Cursor = edge != 0
                ? ((edge is 1 or 2) ? Cursors.SizeWE :
                   (edge is 4 or 8) ? Cursors.SizeNS :
                   Cursors.SizeAll)
                : boxRect.Contains(e.Location)
                    ? Cursors.SizeAll
                    : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        _resizing = false;
    }

    private void RepositionControls()
    {
        var x = Math.Max(20, _captureRect.X - _formOriginX);
        var y = Math.Max(20, _captureRect.Y - _formOriginY - TopBarHeight - 20);

        // Check if each control exists before moving it, otherwise the app crashes.
        if (_titleLabel != null) _titleLabel.Location = new Point(x, y);
        if (_hintLabel != null) _hintLabel.Location = new Point(x, y + 24);
        if (_confirmBtn != null) _confirmBtn.Location = new Point(x, y + 50);
        if (_backBtn != null) _backBtn.Location = new Point(x + 190, y + 50);

        var exampleX = Math.Max(
            _captureRect.X - _formOriginX + _captureRect.Width + 80,
            (int)(_screenBounds.Width * 0.375));

        if (_exampleLabel != null) _exampleLabel.Location = new Point(exampleX, y);
        if (_exampleBox != null) _exampleBox.Location = new Point(exampleX, y + 20);
    }

    private Rectangle GetBoxRect()
    {
        return new(
        _captureRect.X - _formOriginX,
        _captureRect.Y - _formOriginY,
        _captureRect.Width,
        _captureRect.Height);
    }

    private static int HitTest(Rectangle rect, Point pt)
    {
        const int edge = 8;
        int result = 0;
        if (pt.X >= rect.Left - edge && pt.X <= rect.Left + edge) result |= 1;
        if (pt.X >= rect.Right - edge && pt.X <= rect.Right + edge) result |= 2;
        if (pt.Y >= rect.Top - edge && pt.Y <= rect.Top + edge) result |= 4;
        if (pt.Y >= rect.Bottom - edge && pt.Y <= rect.Bottom + edge) result |= 8;
        return result;
    }
}
