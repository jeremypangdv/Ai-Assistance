using System.Drawing.Drawing2D;

namespace LaunchBuddy;

// Small round always-on-top button parked at the screen edge; left-click opens the compact chat, drag to move it.
internal sealed class FloatingButton : Form
{
    private const int Diameter = 56;
    private const int EdgeMargin = 20;
    private const int DragThreshold = 4;
    private Point _pressScreen;
    private Point _pressLocation;
    private bool _pressed;
    private bool _dragging;
    private bool _hover;

    public event EventHandler? Clicked;

    public FloatingButton(ContextMenuStrip menu)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(Diameter, Diameter);
        BackColor = Theme.Accent;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        ContextMenuStrip = menu;
        Text = "LaunchBuddy";

        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, Diameter, Diameter);
        Region = new Region(path);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Diameter - EdgeMargin, area.Bottom - Diameter - EdgeMargin);
    }

    // Keep it out of Alt+Tab.
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var fill = new SolidBrush(_hover ? ControlPaint.Light(Theme.Accent, 0.25f) : Theme.Accent))
            graphics.FillEllipse(fill, 0, 0, Diameter, Diameter);

        // E8BD is the "Message" glyph in Segoe MDL2 Assets (present on Windows 10/11).
        using var glyphFont = new Font("Segoe MDL2 Assets", 20F);
        TextRenderer.DrawText(graphics, "", glyphFont, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _pressed = true;
            _dragging = false;
            _pressScreen = Cursor.Position;
            _pressLocation = Location;
        }
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_pressed)
        {
            var dx = Cursor.Position.X - _pressScreen.X;
            var dy = Cursor.Position.Y - _pressScreen.Y;
            if (_dragging || Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold)
            {
                _dragging = true;
                Location = new Point(_pressLocation.X + dx, _pressLocation.Y + dy);
            }
        }
        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left && _pressed)
        {
            _pressed = false;
            if (_dragging)
                KeepOnScreen();
            else
                Clicked?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseUp(eventArgs);
    }

    private void KeepOnScreen()
    {
        var area = Screen.FromPoint(Center()).WorkingArea;
        Location = new Point(
            Math.Clamp(Left, area.Left, area.Right - Width),
            Math.Clamp(Top, area.Top, area.Bottom - Height));
    }

    private Point Center() => new(Left + Width / 2, Top + Height / 2);
}
