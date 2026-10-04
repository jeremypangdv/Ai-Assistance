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
    private readonly ListView _websiteList;
    private readonly Button _editWebsite;
    private readonly Button _removeWebsite;
    private readonly List<WebsiteRecord> _websites;
    private KeyCapture _capture;

    public int PushToTalkKey { get; private set; }
    public bool PushToTalkEnabled => _pushToTalkEnabled.Checked;
    public Keys QuickToggleKeys { get; private set; }
    public bool QuickToggleEnabled => _quickToggleEnabled.Checked;
    // The edited list; only written to the store when the dialog is saved.
    public IReadOnlyList<WebsiteRecord> Websites => _websites;
    public bool WebsitesChanged { get; private set; }

    public SettingsForm(AppSettings settings, IReadOnlyList<WebsiteRecord> websites, string? quickToggleProblem = null)
    {
        _websites = websites.ToList();
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
        ClientSize = new Size(980, 560);
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);

        var heading = Heading("按住說話（Push-to-talk）", 20);
        var description = Description(
            "按住按鍵說話，放開後辨識並送出，不需要說 Hey Minibot。出現確認卡時，按住說 approve 或 reject。" +
            "按住期間若按了其他鍵（例如 Ctrl+C）或滑鼠，這次就不會送出。", 54, 96);
        _pushToTalkEnabled = new CheckBox { Text = "啟用按住說話", Checked = settings.PushToTalkEnabled, AutoSize = true, Location = new Point(24, 156) };
        (_keyLabel, _changeKey) = KeyRow("按鍵：", PushToTalkHook.KeyName(PushToTalkKey), 196, KeyCapture.PushToTalkKey);
        _keyHint = Hint(236);

        var toggleHeading = Heading("快速開關", 290);
        var toggleDescription = Description("暫停語音輸入（含 Hey Minibot），再按一次恢復。", 324, 30);
        _quickToggleEnabled = new CheckBox { Text = "啟用快速開關快捷鍵", Checked = settings.QuickToggleEnabled, AutoSize = true, Location = new Point(24, 362) };
        (_toggleLabel, _changeToggle) = KeyRow("快捷鍵：", GlobalHotkey.Describe(QuickToggleKeys), 402, KeyCapture.QuickToggle);
        _toggleHint = Hint(442);
        _toggleHint.Text = quickToggleProblem ?? "";

        // Right column: saved websites.
        var websiteHeading = Heading("網站", 20, left: WebsiteLeft);
        var websiteDescription = Description(
            "說「記住這個網站」會讀取瀏覽器目前的網頁，並由 AI 自動取名。名稱就是之後說「開 名稱」時用的字，可在這裡改名、新增或移除；按儲存後生效。",
            54, 72, left: WebsiteLeft, width: WebsiteWidth);
        _websiteList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            ShowItemToolTips = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(WebsiteLeft, 132),
            Size = new Size(WebsiteWidth, 318)
        };
        // The native header ignores BackColor, so draw it to match the dark theme.
        _websiteList.OwnerDraw = true;
        _websiteList.DrawColumnHeader += (_, eventArgs) =>
        {
            using (var background = new SolidBrush(Theme.Raised))
                eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
            using (var divider = new Pen(Theme.Border))
                eventArgs.Graphics.DrawLine(divider, eventArgs.Bounds.Right - 1, eventArgs.Bounds.Top, eventArgs.Bounds.Right - 1, eventArgs.Bounds.Bottom);
            var text = eventArgs.Bounds with { X = eventArgs.Bounds.X + 6, Width = eventArgs.Bounds.Width - 6 };
            TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Header?.Text, _websiteList.Font, text, Theme.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        _websiteList.DrawItem += (_, eventArgs) => eventArgs.DrawDefault = true;
        _websiteList.DrawSubItem += (_, eventArgs) => eventArgs.DrawDefault = true;
        _websiteList.Columns.Add("名稱", 150);
        _websiteList.Columns.Add("網址");
        Theme.UseDarkScrollBars(_websiteList);
        _websiteList.SelectedIndexChanged += (_, _) => UpdateWebsiteButtons();
        // The vertical scrollbar only counts once the list exists and is filled.
        _websiteList.HandleCreated += (_, _) => BeginInvoke(StretchAddressColumn);
        _websiteList.ClientSizeChanged += (_, _) => StretchAddressColumn();
        _websiteList.DoubleClick += (_, _) => EditWebsite();
        _websiteList.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Delete)
                RemoveWebsite();
        };
        var addWebsite = WebsiteButton("新增…", 0, AddWebsite);
        _editWebsite = WebsiteButton("改名／編輯…", 1, EditWebsite);
        _removeWebsite = WebsiteButton("移除", 2, RemoveWebsite);

        var save = new Button { Text = "儲存", Size = new Size(96, 34), Location = new Point(ClientSize.Width - 24 - 96 - 8 - 96, 506), DialogResult = DialogResult.OK };
        Theme.StyleButton(save, Theme.Accent);
        var cancel = new Button { Text = "取消", Size = new Size(96, 34), Location = new Point(ClientSize.Width - 24 - 96, 506), DialogResult = DialogResult.Cancel };
        Theme.StyleButton(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.AddRange([heading, description, _pushToTalkEnabled, _keyHint, toggleHeading, toggleDescription, _quickToggleEnabled, _toggleHint,
            websiteHeading, websiteDescription, _websiteList, addWebsite, _editWebsite, _removeWebsite, save, cancel]);
        UpdateKeyHint();
        FillWebsiteList();
    }

    private const int WebsiteLeft = 520;
    private const int WebsiteWidth = 436;

    private Button WebsiteButton(string text, int index, Action onClick)
    {
        var button = new Button { Text = text, Location = new Point(WebsiteLeft + index * 124, 458), Size = new Size(116, 32) };
        Theme.StyleButton(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void FillWebsiteList(WebsiteRecord? select = null)
    {
        _websiteList.BeginUpdate();
        _websiteList.Items.Clear();
        foreach (var website in _websites.OrderBy(site => site.Alias, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ListViewItem([website.Alias, website.Url]) { Tag = website, ToolTipText = website.Url };
            _websiteList.Items.Add(item);
            if (website == select)
            {
                item.Selected = true;
                item.EnsureVisible();
            }
        }
        _websiteList.EndUpdate();
        StretchAddressColumn();
        UpdateWebsiteButtons();
    }

    // Fill the list to its edge (inside any vertical scrollbar), so no undrawn header strip or horizontal scrollbar appears.
    private void StretchAddressColumn()
    {
        if (_websiteList.IsHandleCreated)
            _websiteList.Columns[1].Width = _websiteList.ClientSize.Width - _websiteList.Columns[0].Width;
    }

    private WebsiteRecord? SelectedWebsite =>
        _websiteList.SelectedItems.Count == 1 ? (WebsiteRecord)_websiteList.SelectedItems[0].Tag! : null;

    private void UpdateWebsiteButtons()
    {
        _editWebsite.Enabled = SelectedWebsite is not null;
        _removeWebsite.Enabled = SelectedWebsite is not null;
    }

    private void AddWebsite()
    {
        using var dialog = new WebsiteEditForm(null, _websites.Select(site => site.Alias).ToList());
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var website = new WebsiteRecord { Alias = dialog.WebsiteName, Url = dialog.WebsiteUrl };
        _websites.Add(website);
        WebsitesChanged = true;
        FillWebsiteList(website);
    }

    private void EditWebsite()
    {
        if (SelectedWebsite is not { } website)
            return;
        using var dialog = new WebsiteEditForm(website, _websites.Where(site => site != website).Select(site => site.Alias).ToList());
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        website.Alias = dialog.WebsiteName;
        website.Url = dialog.WebsiteUrl;
        WebsitesChanged = true;
        FillWebsiteList(website);
    }

    private void RemoveWebsite()
    {
        if (SelectedWebsite is not { } website)
            return;
        if (MessageBox.Show(this, $"移除「{website.Alias}」？\n{website.Url}\n\n按儲存後才會真正移除。", "移除網站",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;
        _websites.Remove(website);
        WebsitesChanged = true;
        FillWebsiteList();
    }

    private static Label Heading(string text, int top, int left = 24) =>
        new() { Text = text, Font = new Font("Segoe UI Semibold", 12F), AutoSize = true, Location = new Point(left, top) };

    private static Label Description(string text, int top, int height, int left = 24, int width = 452) =>
        new() { Text = text, ForeColor = Theme.TextSecondary, Location = new Point(left, top), Size = new Size(width, height) };

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
