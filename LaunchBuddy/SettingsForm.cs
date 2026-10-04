using System.Runtime.InteropServices;

namespace LaunchBuddy;

internal sealed class SettingsForm : Form
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly CheckBox _pushToTalkEnabled;
    private readonly Label _keyLabel;
    private readonly Button _changeKey;
    private readonly Label _keyHint;
    private bool _capturing;

    public int PushToTalkKey { get; private set; }
    public bool PushToTalkEnabled => _pushToTalkEnabled.Checked;

    public SettingsForm(AppSettings settings)
    {
        PushToTalkKey = settings.PushToTalkKey;
        Text = "LaunchBuddy 設定";
        Font = new Font("Segoe UI", 10F);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 340);
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);

        var heading = new Label
        {
            Text = "按住說話（Push-to-talk）",
            Font = new Font("Segoe UI Semibold", 12F),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        var description = new Label
        {
            Text = "按住按鍵說話，放開後辨識並送出，不需要說 Hey Minibot。出現確認卡時，按住說 approve 或 reject。" +
                   "按住期間若按了其他鍵（例如 Ctrl+C）或滑鼠，這次就不會送出。",
            ForeColor = Theme.TextSecondary,
            Location = new Point(24, 54),
            Size = new Size(452, 96)
        };
        _pushToTalkEnabled = new CheckBox
        {
            Text = "啟用按住說話",
            Checked = settings.PushToTalkEnabled,
            AutoSize = true,
            Location = new Point(24, 162)
        };
        var keyCaption = new Label { Text = "按鍵：", AutoSize = true, Location = new Point(24, 210) };
        _keyLabel = new Label
        {
            Text = PushToTalkHook.KeyName(PushToTalkKey),
            Font = new Font("Segoe UI Semibold", 10.5F),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Theme.Surface,
            Location = new Point(84, 204),
            Size = new Size(220, 32)
        };
        _changeKey = new Button { Text = "更改按鍵", Location = new Point(316, 204), Size = new Size(110, 32) };
        Theme.StyleButton(_changeKey);
        _changeKey.Click += (_, _) => BeginCapture();
        _keyHint = new Label
        {
            ForeColor = Theme.ApprovalText,
            Location = new Point(24, 244),
            Size = new Size(452, 40),
            Font = new Font("Segoe UI", 9F)
        };

        var save = new Button { Text = "儲存", Size = new Size(96, 34), Location = new Point(276, 290), DialogResult = DialogResult.OK };
        Theme.StyleButton(save, Theme.Accent);
        var cancel = new Button { Text = "取消", Size = new Size(96, 34), Location = new Point(380, 290), DialogResult = DialogResult.Cancel };
        Theme.StyleButton(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.AddRange([heading, description, _pushToTalkEnabled, keyCaption, _keyLabel, _changeKey, _keyHint, save, cancel]);
        UpdateHint();
    }

    private void BeginCapture()
    {
        _capturing = true;
        _keyLabel.Text = "請按下新按鍵…";
        _keyLabel.ForeColor = Theme.UserName;
        _keyHint.Text = "按 Esc 取消。";
        _changeKey.Enabled = false;
    }

    private void EndCapture(int? virtualKey)
    {
        _capturing = false;
        if (virtualKey is { } key)
            PushToTalkKey = key;
        _keyLabel.Text = PushToTalkHook.KeyName(PushToTalkKey);
        _keyLabel.ForeColor = Theme.Text;
        _changeKey.Enabled = true;
        _changeKey.Focus();
        UpdateHint();
    }

    private void UpdateHint()
    {
        var key = (Keys)PushToTalkKey;
        _keyHint.Text = key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or Keys.Space
            ? "注意：按住這個鍵也會輸入文字，建議改用 Ctrl、Alt 或 F13 這類不會打字的按鍵。"
            : string.Empty;
    }

    // Every key, including Tab, arrows, Ctrl and Alt on their own, arrives here before any control handles it.
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (_capturing && (message.Msg == WM_KEYDOWN || message.Msg == WM_SYSKEYDOWN))
        {
            var virtualKey = (int)message.WParam;
            if (virtualKey == (int)Keys.Escape)
                EndCapture(null);
            else
                EndCapture(ResolveSide(virtualKey));
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    // Window messages report Ctrl/Shift/Alt without the side; the hook needs Left Ctrl (0xA2) rather than Ctrl (0x11).
    private static int ResolveSide(int virtualKey) => virtualKey switch
    {
        VK_CONTROL => IsDown(0xA3) ? 0xA3 : 0xA2,
        VK_SHIFT => IsDown(0xA1) ? 0xA1 : 0xA0,
        VK_MENU => IsDown(0xA5) ? 0xA5 : 0xA4,
        _ => virtualKey
    };

    private static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
