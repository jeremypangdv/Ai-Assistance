using System.Runtime.InteropServices;

namespace LaunchBuddy;

internal sealed class SettingsForm : Form
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private enum KeyCapture { None, PushToTalkKey, QuickToggle }

    private readonly CheckBox _pushToTalkEnabled;
    private readonly Label _keyLabel;
    private readonly Button _changeKey;
    private readonly Label _keyHint;
    private readonly CheckBox _quickToggleEnabled;
    private readonly Label _toggleLabel;
    private readonly Button _changeToggle;
    private readonly Label _toggleHint;
    private KeyCapture _capture;

    public int PushToTalkKey { get; private set; }
    public bool PushToTalkEnabled => _pushToTalkEnabled.Checked;
    public Keys QuickToggleKeys { get; private set; }
    public bool QuickToggleEnabled => _quickToggleEnabled.Checked;

    public SettingsForm(AppSettings settings, string? quickToggleProblem = null)
    {
        PushToTalkKey = settings.PushToTalkKey;
        QuickToggleKeys = (Keys)settings.QuickToggleKeys;
        Text = "LaunchBuddy 設定";
        Font = new Font("Segoe UI", 10F);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 560);
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);

        var heading = Heading("按住說話（Push-to-talk）", 20);
        var description = Description(
            "按住按鍵說話，放開後辨識並送出，不需要說 Hey Minibot。出現確認卡時，按住說 approve 或 reject。" +
            "按住期間若按了其他鍵（例如 Ctrl+C）或滑鼠，這次就不會送出。", 54, 96);
        _pushToTalkEnabled = new CheckBox { Text = "啟用按住說話", Checked = settings.PushToTalkEnabled, AutoSize = true, Location = new Point(24, 156) };
        (_keyLabel, _changeKey) = KeyRow("按鍵：", PushToTalkHook.KeyName(PushToTalkKey), 196, KeyCapture.PushToTalkKey);
        _keyHint = Hint(236);

        var toggleHeading = Heading("快速開關", 290);
        var toggleDescription = Description("按這組快捷鍵暫停語音輸入（含 Hey Minibot），再按一次恢復。", 324, 30);
        _quickToggleEnabled = new CheckBox { Text = "啟用快速開關快捷鍵", Checked = settings.QuickToggleEnabled, AutoSize = true, Location = new Point(24, 362) };
        (_toggleLabel, _changeToggle) = KeyRow("快捷鍵：", GlobalHotkey.Describe(QuickToggleKeys), 402, KeyCapture.QuickToggle);
        _toggleHint = Hint(442);
        _toggleHint.Text = quickToggleProblem ?? "";

        var save = new Button { Text = "儲存", Size = new Size(96, 34), Location = new Point(276, 506), DialogResult = DialogResult.OK };
        Theme.StyleButton(save, Theme.Accent);
        var cancel = new Button { Text = "取消", Size = new Size(96, 34), Location = new Point(380, 506), DialogResult = DialogResult.Cancel };
        Theme.StyleButton(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.AddRange([heading, description, _pushToTalkEnabled, _keyHint, toggleHeading, toggleDescription, _quickToggleEnabled, _toggleHint, save, cancel]);
        UpdateKeyHint();
    }

    private static Label Heading(string text, int top) =>
        new() { Text = text, Font = new Font("Segoe UI Semibold", 12F), AutoSize = true, Location = new Point(24, top) };

    private static Label Description(string text, int top, int height) =>
        new() { Text = text, ForeColor = Theme.TextSecondary, Location = new Point(24, top), Size = new Size(452, height) };

    private static Label Hint(int top) =>
        new() { ForeColor = Theme.ApprovalText, Location = new Point(24, top), Size = new Size(452, 40), Font = new Font("Segoe UI", 9F) };

    private (Label Value, Button Change) KeyRow(string caption, string value, int top, KeyCapture capture)
    {
        var captionLabel = new Label { Text = caption, AutoSize = true, Location = new Point(24, top + 6) };
        var valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI Semibold", 10.5F),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Theme.Surface,
            Location = new Point(100, top),
            Size = new Size(204, 32)
        };
        var change = new Button { Text = "更改", Location = new Point(316, top), Size = new Size(110, 32) };
        Theme.StyleButton(change);
        change.Click += (_, _) => BeginCapture(capture);
        Controls.AddRange([captionLabel, valueLabel, change]);
        return (valueLabel, change);
    }

    private void BeginCapture(KeyCapture capture)
    {
        _capture = capture;
        var (label, hint) = capture == KeyCapture.PushToTalkKey ? (_keyLabel, _keyHint) : (_toggleLabel, _toggleHint);
        label.Text = capture == KeyCapture.PushToTalkKey ? "請按下新按鍵…" : "請按下組合鍵…";
        label.ForeColor = Theme.UserName;
        hint.Text = capture == KeyCapture.PushToTalkKey ? "按 Esc 取消。" : "例如 Ctrl + Alt + M；需包含 Ctrl、Alt 或 Shift。按 Esc 取消。";
        _changeKey.Enabled = false;
        _changeToggle.Enabled = false;
    }

    private void EndCapture()
    {
        _capture = KeyCapture.None;
        _keyLabel.Text = PushToTalkHook.KeyName(PushToTalkKey);
        _toggleLabel.Text = GlobalHotkey.Describe(QuickToggleKeys);
        _keyLabel.ForeColor = Theme.Text;
        _toggleLabel.ForeColor = Theme.Text;
        _toggleHint.Text = "";
        _changeKey.Enabled = true;
        _changeToggle.Enabled = true;
        UpdateKeyHint();
    }

    private void UpdateKeyHint()
    {
        var key = (Keys)PushToTalkKey;
        _keyHint.Text = key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or Keys.Space
            ? "注意：按住這個鍵也會輸入文字，建議改用 Ctrl、Alt 或 F13 這類不會打字的按鍵。"
            : string.Empty;
    }

    // Every key, including Tab, arrows, Ctrl and Alt on their own, arrives here before any control handles it.
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (_capture == KeyCapture.None || (message.Msg != WM_KEYDOWN && message.Msg != WM_SYSKEYDOWN))
            return base.ProcessCmdKey(ref message, keyData);

        var virtualKey = (int)message.WParam;
        if (virtualKey == (int)Keys.Escape)
        {
            EndCapture();
            return true;
        }

        if (_capture == KeyCapture.PushToTalkKey)
        {
            PushToTalkKey = ResolveSide(virtualKey);
            EndCapture();
            return true;
        }

        // Quick toggle: wait while only modifiers are down, then require at least one of them.
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
        {
            _toggleLabel.Text = modifiers == Keys.None ? "請按下組合鍵…" : string.Join(" + ", GlobalHotkey.ModifierNames(modifiers)) + " + …";
            return true;
        }
        if (modifiers == Keys.None)
        {
            _toggleHint.Text = "快捷鍵需要包含 Ctrl、Alt 或 Shift，否則平常打字也會觸發。";
            return true;
        }
        QuickToggleKeys = modifiers | key;
        EndCapture();
        return true;
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
