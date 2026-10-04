namespace LaunchBuddy;

// Add or edit one saved website: its name (what you say after "開") and its address.
internal sealed class WebsiteEditForm : Form
{
    private readonly TextBox _name;
    private readonly TextBox _url;
    private readonly Label _problem;
    private readonly IReadOnlyCollection<string> _otherNames;

    public string WebsiteName => _name.Text.Trim();
    public string WebsiteUrl { get; private set; } = string.Empty;

    public WebsiteEditForm(WebsiteRecord? website, IReadOnlyCollection<string> otherNames)
    {
        _otherNames = otherNames;
        Text = website is null ? "新增網站" : "編輯網站";
        Font = new Font("Segoe UI", 10F);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 236);
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);

        var nameCaption = new Label { Text = "名稱（說「開 名稱」來開啟）", AutoSize = true, Location = new Point(20, 18) };
        _name = Field(website?.Alias, 44);
        var urlCaption = new Label { Text = "網址", AutoSize = true, Location = new Point(20, 86) };
        _url = Field(website?.Url, 112);
        _problem = new Label { ForeColor = Theme.ApprovalText, Location = new Point(20, 146), Size = new Size(420, 36), Font = new Font("Segoe UI", 9F) };

        var save = new Button { Text = "確定", Size = new Size(96, 34), Location = new Point(236, 186) };
        Theme.StyleButton(save, Theme.Accent);
        save.Click += (_, _) => Confirm();
        var cancel = new Button { Text = "取消", Size = new Size(96, 34), Location = new Point(344, 186), DialogResult = DialogResult.Cancel };
        Theme.StyleButton(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([nameCaption, _name, urlCaption, _url, _problem, save, cancel]);
    }

    private static TextBox Field(string? text, int top) => new()
    {
        Text = text ?? string.Empty,
        Location = new Point(20, top),
        Width = 420,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle
    };

    private void Confirm()
    {
        if (WebsiteName.Length == 0)
        {
            _problem.Text = "請輸入名稱。";
            _name.Focus();
            return;
        }
        if (_otherNames.Contains(WebsiteName, StringComparer.OrdinalIgnoreCase))
        {
            _problem.Text = $"已經有網站叫「{WebsiteName}」，請換一個名稱。";
            _name.Focus();
            return;
        }
        // "github.com" is accepted as https://github.com/.
        if (CurrentBrowserTab.NormalizeUrl(_url.Text) is not { } url)
        {
            _problem.Text = "請輸入有效的 http 或 https 網址，例如 https://github.com";
            _url.Focus();
            return;
        }
        WebsiteUrl = url;
        DialogResult = DialogResult.OK;
    }
}
