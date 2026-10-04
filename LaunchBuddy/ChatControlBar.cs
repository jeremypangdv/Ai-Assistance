using System.Runtime.InteropServices;

namespace LaunchBuddy;

internal enum ChatMode
{
    PushToTalk,
    AiReply
}

// The bar at the top centre of the screen while Minibot controls a chat app: which app, the mode switch,
// Send (push-to-talk mode) and Cancel. Like the voice popup it never takes focus, so clicking it leaves
// the caret in the chat's message box.
internal sealed class ChatControlBar : Form
{
    private const int BarWidth = 560;
    private const int Pad = 14;
    private static readonly Color ControlColor = Color.FromArgb(52, 199, 89);

    private readonly Label _title;
    private readonly Label _conversation;
    private readonly Label _pushToTalk;
    private readonly Label _aiReply;
    private readonly Label _send;
    private readonly Label _cancel;
    private readonly Label _status;
    private readonly bool _aiAvailable;
    private ChatMode _mode;

    public event Action<ChatMode>? ModeSelected;
    public event Action? SendClicked;
    public event Action? CancelClicked;

    public ChatControlBar(string appName, bool aiAvailable)
    {
        _aiAvailable = aiAvailable;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10F);
        Size = new Size(BarWidth, 128);
        DoubleBuffered = true;

        var dot = new Label { Text = "●", ForeColor = ControlColor, AutoSize = true, Location = new Point(Pad - 2, Pad) };
        _title = new Label
        {
            Text = $"Minibot 正在控制 {appName}",
            Font = new Font("Segoe UI Semibold", 10.5F),
            AutoSize = true,
            Location = new Point(Pad + 16, Pad - 1)
        };
        _conversation = new Label
        {
            ForeColor = Theme.TextMuted,
            AutoEllipsis = true,
            Location = new Point(Pad + 16, Pad + 22),
            Size = new Size(BarWidth - Pad * 2 - 16 - 96, 20)
        };
        _cancel = ActionLabel("取消", Color.FromArgb(150, 52, 56), new Point(BarWidth - Pad - 84, Pad - 2), 84);
        _cancel.Click += (_, _) => CancelClicked?.Invoke();

        _pushToTalk = ActionLabel("Push to talk 訊息", Theme.Raised, new Point(Pad, 60), 150);
        _pushToTalk.Click += (_, _) => Select(ChatMode.PushToTalk);
        _aiReply = ActionLabel(aiAvailable ? "AI 回覆" : "AI 回覆（尚未支援）", Theme.Raised, new Point(Pad + 150, 60), 150);
        _aiReply.Click += (_, _) => Select(ChatMode.AiReply);
        if (!aiAvailable)
        {
            _aiReply.ForeColor = Theme.TextMuted;
            _aiReply.Cursor = Cursors.Default;
        }
        _send = ActionLabel("發送", Theme.Approve, new Point(BarWidth - Pad - 84, 60), 84);
        _send.Click += (_, _) => SendClicked?.Invoke();

        _status = new Label
        {
            ForeColor = Theme.TextSecondary,
            AutoEllipsis = true,
            Location = new Point(Pad, 98),
            Size = new Size(BarWidth - Pad * 2, 20)
        };
        Controls.AddRange([dot, _title, _conversation, _cancel, _pushToTalk, _aiReply, _send, _status]);

        HandleCreated += (_, _) =>
        {
            var round = 2; // DWMWCP_ROUND: Windows 11 rounded corners; ignored elsewhere.
            _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        };
        ShowMode(ChatMode.PushToTalk);
    }

    // Labels rather than Buttons: a Button takes focus on click, which would activate this window.
    private static Label ActionLabel(string text, Color background, Point location, int width) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = background,
        ForeColor = Color.White,
        Location = location,
        Size = new Size(width, 30),
        Cursor = Cursors.Hand
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

    // Top centre of the screen the chat window is on.
    public void ShowAbove(IntPtr chatWindow)
    {
        var area = Screen.FromHandle(chatWindow).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 8);
        Show();
    }

    public void SetConversation(string conversation) => _conversation.Text = conversation;

    public void SetStatus(string status) => _status.Text = status;

    public void ShowMode(ChatMode mode)
    {
        _mode = mode;
        _pushToTalk.BackColor = mode == ChatMode.PushToTalk ? Theme.Accent : Theme.Raised;
        _aiReply.BackColor = mode == ChatMode.AiReply ? Theme.Accent : Theme.Raised;
        _send.Visible = mode == ChatMode.PushToTalk;
    }

    private void Select(ChatMode mode)
    {
        if (mode == _mode || mode == ChatMode.AiReply && !_aiAvailable)
            return;
        ShowMode(mode);
        ModeSelected?.Invoke(mode);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var border = new Pen(Theme.Border);
        eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
