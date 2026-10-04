using System.Runtime.InteropServices;

namespace LaunchBuddy;

// Small status card beside the floating button for voice requests: listening, transcribing, processing,
// the Approve/Reject card and the result. It never takes focus, so a game or full-screen app keeps the keyboard.
internal sealed class VoicePopup : Form
{
    private const int PopupWidth = 380;
    private const int Pad = 14;
    private const int ContentWidth = PopupWidth - Pad * 2;
    private static readonly Color ListeningColor = Color.FromArgb(214, 64, 69);
    private static readonly Color WorkingColor = Color.FromArgb(240, 180, 60);
    private static readonly Color DoneColor = Color.FromArgb(52, 199, 89);

    private readonly Label _dot;
    private readonly Label _title;
    private readonly Label _close;
    private readonly Label _heard;
    private readonly Label _body;
    private readonly Label _approve;
    private readonly Label _reject;
    private readonly System.Windows.Forms.Timer _autoHide = new();
    private readonly System.Windows.Forms.Timer _elapsed = new() { Interval = 500 };
    private Rectangle _anchor;
    private DateTime _processingStarted;
    private string _processingTitle = "";

    public event Action? ApproveClicked;
    public event Action? RejectClicked;

    // Between a spoken command and its final result: the tray routes the hidden chat window's updates here.
    public bool IsFollowingRequest { get; private set; }

    public VoicePopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10F);
        Width = PopupWidth;
        DoubleBuffered = true;

        _dot = new Label { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 10F), Location = new Point(Pad - 2, Pad) };
        _title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5F), Location = new Point(Pad + 16, Pad - 1) };
        _close = new Label
        {
            Text = "✕",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Cursor = Cursors.Hand,
            Location = new Point(PopupWidth - Pad - 14, Pad - 2)
        };
        _close.Click += (_, _) => HidePopup();
        _heard = new Label { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), ForeColor = Theme.TextSecondary };
        _body = new Label { AutoSize = true, MaximumSize = new Size(ContentWidth, 0) };
        // Labels rather than Buttons: a Button takes focus on click, which would activate this window.
        _approve = ActionLabel("Approve", Theme.Approve);
        _approve.Click += (_, _) => ApproveClicked?.Invoke();
        _reject = ActionLabel("Reject", Theme.Raised);
        _reject.Click += (_, _) => RejectClicked?.Invoke();
        Controls.AddRange([_dot, _title, _close, _heard, _body, _approve, _reject]);

        _autoHide.Tick += (_, _) => HidePopup();
        _elapsed.Tick += (_, _) => _title.Text = $"{_processingTitle}（{(DateTime.Now - _processingStarted).TotalSeconds:0} 秒）";
        HandleCreated += (_, _) =>
        {
            var round = 2; // DWMWCP_ROUND: Windows 11 rounded corners; ignored elsewhere.
            _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        };
    }

    private static Label ActionLabel(string text, Color background) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = background,
        ForeColor = Color.White,
        Size = new Size(104, 34),
        Cursor = Cursors.Hand,
        Visible = false
    };

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8;
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    public void ShowListening(Rectangle anchor, string hint) =>
        Present(anchor, ListeningColor, "正在聆聽…", null, hint, buttons: false, following: false, autoHide: null);

    public void ShowTranscribing() =>
        Present(_anchor, WorkingColor, "正在辨識…", null, "", buttons: false, following: false, autoHide: TimeSpan.FromSeconds(15));

    public void ShowProcessing(Rectangle anchor, string heard)
    {
        Present(anchor, WorkingColor, "處理中", $"你說：{heard}", "", buttons: false, following: true, autoHide: null);
        StartElapsed("處理中");
    }

    public void ShowPending(Rectangle anchor, PendingAction action, string? hint = null) =>
        Present(anchor, WorkingColor, action.Title, null,
            action.Details + "\n\n" + (hint ?? "請說 approve 或 reject，或點下方按鈕。"),
            buttons: true, following: true, autoHide: null);

    public void ShowResult(string text) =>
        Present(_anchor, DoneColor, "完成", null, text, buttons: false, following: false, autoHide: TimeSpan.FromSeconds(5));

    public void ShowNotice(Rectangle anchor, string title, string text) =>
        Present(anchor, Theme.Accent, title, null, text, buttons: false, following: false, autoHide: TimeSpan.FromSeconds(3));

    public void HidePopup()
    {
        _autoHide.Stop();
        _elapsed.Stop();
        IsFollowingRequest = false;
        Hide();
    }

    private void StartElapsed(string title)
    {
        _processingTitle = title;
        _processingStarted = DateTime.Now;
        _title.Text = $"{title}（0 秒）";
        _elapsed.Start();
    }

    private void Present(Rectangle anchor, Color dot, string title, string? heard, string body, bool buttons, bool following, TimeSpan? autoHide)
    {
        _anchor = anchor;
        _elapsed.Stop();
        _autoHide.Stop();
        IsFollowingRequest = following;
        _dot.ForeColor = dot;
        _title.Text = title;
        _heard.Text = heard ?? "";
        _heard.Visible = heard is not null;
        _body.Text = body;
        _body.Visible = body.Length > 0;
        _approve.Visible = buttons;
        _reject.Visible = buttons;

        var y = Pad + 28;
        if (_heard.Visible)
        {
            _heard.Location = new Point(Pad, y);
            y += _heard.PreferredHeight + 6;
        }
        if (_body.Visible)
        {
            _body.Location = new Point(Pad, y);
            y += _body.PreferredHeight + 10;
        }
        if (buttons)
        {
            _approve.Location = new Point(PopupWidth - Pad - _approve.Width, y);
            _reject.Location = new Point(_approve.Left - 8 - _reject.Width, y);
            y += _approve.Height + 4;
        }
        Height = y + Pad - 4;

        var area = Screen.FromRectangle(anchor).WorkingArea;
        var x = Math.Clamp(anchor.Right - Width, area.Left, area.Right - Width);
        var top = anchor.Top - Height - 10;
        Location = new Point(x, top >= area.Top ? top : Math.Min(anchor.Bottom + 10, area.Bottom - Height));

        if (autoHide is { } delay)
        {
            _autoHide.Interval = (int)delay.TotalMilliseconds;
            _autoHide.Start();
        }
        if (!Visible)
            Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var border = new Pen(Theme.Border);
        eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoHide.Dispose();
            _elapsed.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
