using System.Diagnostics;

namespace LaunchBuddy;

internal sealed class AssistantForm : Form
{
    private readonly WebsiteStore _websiteStore = new();
    private readonly ApplicationIndex _applicationIndex = new();
    private readonly IntentInterpreter _interpreter;
    private readonly Panel _messageViewport;
    private readonly RichTextBox _messages;
    private readonly Panel _approvalPanel;
    private readonly TableLayoutPanel _chatSurface;
    private readonly Control _sidebar;
    private readonly Panel _composer;
    private readonly Label _shortcutHint;
    private Label _statusLabel = null!;
    private Label _headerSubtitle = null!;
    private Panel _headerActions = null!;
    private bool _compact;
    private readonly TextBox _input;
    private readonly Button _sendButton;
    private readonly Button _cancelButton;
    private readonly System.Windows.Forms.Timer _busyTimer;
    private CancellationTokenSource? _requestCts;
    private bool _isSubmitting;
    private DateTime _requestStarted;
    private string _phase = "";
    private PendingAction? _pendingAction;
    private DateTime _pendingSince;

    public AssistantForm(IntentInterpreter? interpreter = null, bool startIndex = true)
    {
        _interpreter = interpreter ?? new IntentInterpreter();
        Text = $"LaunchBuddy {typeof(AssistantForm).Assembly.GetName().Version?.ToString(3)}";
        Font = new Font("Segoe UI", 10F);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 820);
        MinimumSize = new Size(820, 620);
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);

        _sidebar = BuildSidebar();
        _chatSurface = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _chatSurface.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _chatSurface.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        _chatSurface.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _chatSurface.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _chatSurface.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        Controls.Add(_chatSurface);
        Controls.Add(_sidebar);
        var header = BuildHeader();

        _approvalPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 0,
            Visible = false,
            Padding = new Padding(14, 8, 14, 8),
            BackColor = Theme.ApprovalBackground
        };

        _composer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(26, 12, 26, 16), BackColor = BackColor };
        var composerCard = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 10, 8), BackColor = Theme.Surface };
        _input = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 11F),
            PlaceholderText = "輸入指令，例如：開 Downloads 裡的 report.pdf",
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Dock = DockStyle.Fill
        };
        Theme.UseDarkScrollBars(_input);
        _input.KeyDown += InputKeyDown;
        _sendButton = new Button
        {
            Text = "傳送",
            Dock = DockStyle.Right,
            Width = 92
        };
        Theme.StyleButton(_sendButton, Theme.Accent);
        _sendButton.Click += async (_, _) => await SubmitAsync();
        _shortcutHint = new Label
        {
            Text = "Enter 傳送 · Shift + Enter 換行 · 所有動作均須 Approve",
            Dock = DockStyle.Bottom,
            Height = 19,
            ForeColor = Theme.TextMuted,
            Font = new Font("Segoe UI", 8.5F)
        };
        composerCard.Controls.Add(_input);
        composerCard.Controls.Add(_sendButton);
        composerCard.Controls.Add(_shortcutHint);
        _composer.Controls.Add(composerCard);
        _cancelButton = new Button
        {
            Text = "取消等待",
            Dock = DockStyle.Right,
            Width = 92,
            Visible = false
        };
        Theme.StyleButton(_cancelButton);
        _cancelButton.Click += (_, _) => _requestCts?.Cancel();
        composerCard.Controls.Add(_cancelButton);
        _cancelButton.BringToFront();

        _busyTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _busyTimer.Tick += (_, _) =>
        {
            if (_isSubmitting)
                _statusLabel.Text = $"{_phase}（{(DateTime.Now - _requestStarted).TotalSeconds:0} 秒）";
        };

        _messageViewport = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(26, 18, 26, 18),
            BackColor = BackColor
        };
        _messages = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 10.5F),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            HideSelection = false,
            ShortcutsEnabled = true
        };
        Theme.UseDarkScrollBars(_messages);
        _messageViewport.Controls.Add(_messages);

        _messageViewport.Margin = Padding.Empty;
        _approvalPanel.Margin = Padding.Empty;
        header.Dock = DockStyle.Fill;
        header.Margin = Padding.Empty;
        _chatSurface.Controls.Add(header, 0, 0);
        _chatSurface.Controls.Add(_messageViewport, 0, 1);
        _chatSurface.Controls.Add(_approvalPanel, 0, 2);
        _chatSurface.Controls.Add(_composer, 0, 3);
        // The title bar's own maximize button on the compact window switches to the full layout.
        Resize += (_, _) =>
        {
            if (_compact && WindowState == FormWindowState.Maximized)
                ApplyLayout(compact: false);
        };

        AddMessage("助手", "你好，我只會協助你尋找／開啟程式，以及永久記住你明確指定的網站。\n\n所有開啟、儲存、更新與刪除，都必須先由你按 Approve。", false);
        Shown += async (_, _) =>
        {
            if (startIndex)
                _applicationIndex.StartExtendedIndexing();
            await UpdateModelStatusAsync();
            _input.Focus();
        };
    }

    public bool IsCompactShowing => Visible && _compact && WindowState != FormWindowState.Minimized;
    public bool IsBusy => _isSubmitting;
    public bool HasPendingAction => _pendingAction is not null;
    // When the current Approve card appeared; speech that started earlier must not answer it.
    public DateTime PendingSince => _pendingSince;

    public Task SubmitSpokenAsync(string text) => SubmitAsync(text);

    public void ConfirmBySpeech(VoiceConfirmation confirmation, string heard)
    {
        if (_pendingAction is null || confirmation == VoiceConfirmation.None)
            return;
        AddMessage("你（語音）", heard, true);
        if (confirmation == VoiceConfirmation.Approve)
            ApprovePendingAction();
        else
            RejectPendingAction();
    }

    public bool IsFullShowing => Visible && !_compact && WindowState != FormWindowState.Minimized;

    public void ShowFull()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        if (!Visible || _compact)
        {
            ApplyLayout(compact: false);
            Size = new Size(1180, 820);
            StartPosition = FormStartPosition.CenterScreen;
            CenterToScreen();
        }
        WindowState = FormWindowState.Maximized;
        ShowAndFocus();
    }

    // Small chat window placed beside the floating button; shares the same conversation as the full window.
    public void ShowCompact(Rectangle anchor)
    {
        if (WindowState != FormWindowState.Normal)
            WindowState = FormWindowState.Normal;
        ApplyLayout(compact: true);

        var size = new Size(400, 580);
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var x = anchor.Right - size.Width;
        var y = anchor.Top - size.Height - 10;
        if (y < area.Top)
            y = anchor.Bottom + 10;
        Bounds = new Rectangle(
            Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - size.Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)),
            size.Width, size.Height);
        ShowAndFocus();
    }

    private void ApplyLayout(bool compact)
    {
        _compact = compact;
        SuspendLayout();
        TopMost = compact;
        MinimumSize = compact ? new Size(340, 440) : new Size(820, 620);
        _sidebar.Visible = !compact;
        _headerSubtitle.Visible = !compact;
        _headerActions.Visible = !compact;
        _shortcutHint.Visible = !compact;
        _statusLabel.Top = compact ? 40 : 70;
        _chatSurface.RowStyles[0].Height = compact ? 68 : 108;
        _chatSurface.RowStyles[3].Height = compact ? 100 : 126;
        _messageViewport.Padding = compact ? new Padding(14, 10, 14, 10) : new Padding(26, 18, 26, 18);
        _composer.Padding = compact ? new Padding(12, 8, 12, 12) : new Padding(26, 12, 26, 16);
        ResumeLayout(true);
        FitStatusLabel();
    }

    private void FitStatusLabel()
    {
        if (_statusLabel.Parent is { } header)
            _statusLabel.Width = Math.Max(120,
                header.ClientSize.Width - _statusLabel.Left - (_headerActions.Visible ? _headerActions.Width : 0) - 12);
    }

    private Control BuildSidebar()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 248,
            Padding = new Padding(16),
            BackColor = Theme.Sidebar
        };

        var title = new Label
        {
            Text = "LaunchBuddy",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 15F),
            Location = new Point(16, 18)
        };
        var subtitle = new Label
        {
            Text = "本機啟動助手",
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Location = new Point(18, 51)
        };
        var newChat = new Button
        {
            Text = "＋  新對話",
            Location = new Point(16, 87),
            Size = new Size(216, 38)
        };
        Theme.StyleButton(newChat);
        newChat.Click += (_, _) => StartNewConversation();

        var guide = new Label
        {
            Text = "你可以要求：\n\n• 開啟 application\n• 開啟檔案或資料夾\n• 以管理員方式開啟 .exe\n• 記住或開啟網站\n\n所有動作均需你按 Approve。",
            ForeColor = Theme.TextSecondary,
            Location = new Point(19, 160),
            Size = new Size(205, 195),
            Font = new Font("Segoe UI", 9.5F)
        };
        var storage = new Label
        {
            Text = "本機模式\n資料不會上傳",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            Location = new Point(19, 690),
            Font = new Font("Segoe UI", 8.5F)
        };
        sidebar.Resize += (_, _) => storage.Location = new Point(19, sidebar.ClientSize.Height - storage.Height - 18);
        sidebar.Controls.Add(title);
        sidebar.Controls.Add(subtitle);
        sidebar.Controls.Add(newChat);
        sidebar.Controls.Add(guide);
        sidebar.Controls.Add(storage);
        return sidebar;
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(16, 11, 12, 10), BackColor = Theme.Surface };
        var title = new Label
        {
            Text = "LaunchBuddy",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 13F),
            Location = new Point(16, 10)
        };
        header.Controls.Add(title);

        _headerSubtitle = new Label
        {
            Text = "本機 application、檔案、資料夾與已記錄網站助手",
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Location = new Point(18, 42)
        };
        header.Controls.Add(_headerSubtitle);

        _statusLabel = new Label
        {
            Text = "正在檢查 Ollama…",
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextSecondary,
            Location = new Point(18, 70),
            Size = new Size(620, 20)
        };
        header.Controls.Add(_statusLabel);

        _headerActions = new Panel
        {
            Dock = DockStyle.Right,
            Width = 142,
            Padding = new Padding(12, 16, 12, 16)
        };
        var refresh = new Button
        {
            Text = "刷新索引",
            Dock = DockStyle.Fill
        };
        Theme.StyleButton(refresh);
        refresh.Click += (_, _) =>
        {
            _applicationIndex.Refresh();
            AddMessage("助手", "已開始在背景刷新索引，你可以繼續聊天。", false);
        };
        _headerActions.Controls.Add(refresh);
        header.Controls.Add(_headerActions);
        // Keep the status line within the header at any window width.
        header.Resize += (_, _) => FitStatusLabel();
        return header;
    }

    private void ShowAndFocus()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
        _input.Focus();
    }

    private void StartNewConversation()
    {
        if (_pendingAction is not null || _isSubmitting)
        {
            AddMessage("助手", "目前正在處理要求，請先取消等待，或處理 Approve／Reject。", false);
            return;
        }

        _messages.Clear();
        AddMessage("助手", "新對話已開始。告訴我你想開啟哪一個 application、檔案、資料夾或已記錄網站。", false);
        _input.Focus();
    }

    private async Task UpdateModelStatusAsync()
    {
        var status = await _interpreter.GetStatusAsync();
        if (!IsDisposed && !_isSubmitting && _pendingAction is null)
        {
            _statusLabel.Text = _applicationIndex.IsExtendedIndexing
                ? $"{status}・正在背景索引電腦項目"
                : $"{status}・已索引 {_applicationIndex.ExtendedItemCount:N0} 個電腦項目";
        }
    }

    private void InputKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Enter && !eventArgs.Shift)
        {
            eventArgs.SuppressKeyPress = true;
            _ = SubmitAsync();
        }
    }

    private async Task SubmitAsync(string? spokenText = null)
    {
        var text = (spokenText ?? _input.Text).Trim();
        if (string.IsNullOrWhiteSpace(text) || _pendingAction is not null || _isSubmitting)
        {
            return;
        }

        _isSubmitting = true;
        using var request = new CancellationTokenSource();
        _requestCts = request;
        _sendButton.Enabled = false;
        _sendButton.Text = "處理中…";
        _cancelButton.Visible = true;
        _requestStarted = DateTime.Now;
        _phase = "本機模型正在理解你的要求";
        _busyTimer.Start();
        try
        {
            // A spoken command leaves whatever the user was typing in the box.
            if (spokenText is null)
                _input.Clear();
            AddMessage(spokenText is null ? "你" : "你（語音）", text, true);
            _statusLabel.Text = _phase;
            _messages.Update();
            // Give Windows a chance to paint the sent message before starting work.
            await Task.Yield();
            var (intent, modelStatus) = await _interpreter.InterpretAsync(text, _websiteStore.All(), request.Token);
            if (IsDisposed)
                return;
            _phase = "正在尋找目標";
            await HandleIntentAsync(intent, request.Token);
            _statusLabel.Text = _pendingAction is not null ? "等待你選擇 Approve 或 Reject" : modelStatus;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                AddMessage("助手", "已取消等待，沒有執行任何動作。", false);
                _statusLabel.Text = "已取消，可以傳送下一個要求";
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Send", exception);
            if (!IsDisposed)
                AddMessage("助手", $"無法處理這個要求：{exception.Message}", false);
        }
        finally
        {
            _isSubmitting = false;
            _requestCts = null;
            if (!IsDisposed)
            {
                _busyTimer.Stop();
                _sendButton.Text = "傳送";
                _cancelButton.Visible = false;
                _sendButton.Enabled = _pendingAction is null;
                if (_pendingAction is null)
                    _input.Focus();
            }
        }
    }

    // The model may pick "open application" for a saved site name, or "open saved website" for an app; route to whichever exists.
    private Intent RouteOpenIntent(Intent intent) => intent.Action switch
    {
        "open_application" when !intent.AsAdministrator && _websiteStore.Find(intent.Query) is { } site =>
            new Intent { Action = "open_saved_website", Alias = site.Alias },
        "open_saved_website" when _websiteStore.Find(intent.Alias) is null && !string.IsNullOrWhiteSpace(intent.Alias) =>
            new Intent { Action = "open_application", Query = intent.Alias },
        _ => intent
    };

    private async Task HandleIntentAsync(Intent intent, CancellationToken cancellationToken)
    {
        intent = RouteOpenIntent(intent);
        switch (intent.Action)
        {
            case "save_website":
                if (!IsSafeUrl(intent.Url))
                {
                    AddMessage("助手", "請提供有效的 http 或 https 網址，例如：https://github.com", false);
                    return;
                }
                if (string.IsNullOrWhiteSpace(intent.Alias))
                {
                    AddMessage("助手", "我知道你想記住網址，但還需要一個名稱。例如：「記住這個網站叫 GitHub https://github.com」。", false);
                    return;
                }
                var existing = _websiteStore.Find(intent.Alias);
                SetPending(new PendingAction
                {
                    Kind = PendingActionKind.SaveWebsite,
                    Title = existing is null ? "準備永久儲存網站" : "準備更新已儲存網站",
                    Details = $"名稱：{intent.Alias}\n網址：{intent.Url}\n\n此資料會保存在本機，直到你明確移除它。",
                    Website = new WebsiteRecord { Alias = intent.Alias, Url = intent.Url }
                });
                return;

            case "open_saved_website":
                var website = _websiteStore.Find(intent.Alias);
                if (website is null)
                {
                    AddMessage("助手", $"找不到名為「{intent.Alias}」的已記錄網站。你可先說「記住這個網站叫 … https://…」。", false);
                    return;
                }
                SetPending(new PendingAction
                {
                    Kind = PendingActionKind.OpenWebsite,
                    Title = "準備開啟已記錄網站",
                    Details = $"名稱：{website.Alias}\n網址：{website.Url}\n\n按 Approve 後才會開啟 Google Chrome（若已安裝），否則使用預設瀏覽器。",
                    Website = website
                });
                return;

            case "remove_saved_website":
                var toRemove = _websiteStore.Find(intent.Alias);
                if (toRemove is null)
                {
                    AddMessage("助手", $"找不到名為「{intent.Alias}」的已記錄網站。", false);
                    return;
                }
                SetPending(new PendingAction
                {
                    Kind = PendingActionKind.RemoveWebsite,
                    Title = "準備永久移除已記錄網站",
                    Details = $"名稱：{toRemove.Alias}\n網址：{toRemove.Url}\n\n移除後，之後說「開 {toRemove.Alias}」便不會再找到它。",
                    Website = toRemove
                });
                return;

            case "list_saved_websites":
                var websites = _websiteStore.All();
                AddMessage("助手", websites.Count == 0
                    ? "目前尚未記錄任何網站。"
                    : "已永久記錄的網站：\n" + string.Join("\n", websites.Select(site => $"• {site.Alias} — {site.Url}")), false);
                return;

            case "open_application":
                if (string.IsNullOrWhiteSpace(intent.Query))
                {
                    AddMessage("助手", "請告訴我想開啟哪一個 application、檔案、資料夾或完整路徑，例如：「開 Notepad」或「開下載資料夾」。", false);
                    return;
                }
                var item = await Task.Run(() => intent.AsAdministrator
                    ? _applicationIndex.FindExecutable(intent.Query) ?? _applicationIndex.Find(intent.Query)
                    : _applicationIndex.Find(intent.Query), cancellationToken).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDisposed)
                    return;
                if (item is null)
                {
                    AddMessage("助手", $"目前找不到「{intent.Query}」。你可按上方「刷新索引」後再試；完整電腦的背景索引需要一些時間。若是檔案或資料夾，也可直接提供完整路徑。", false);
                    return;
                }
                if (intent.AsAdministrator && (item.Kind != ComputerItemKind.Application || !item.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    AddMessage("助手", $"找到的是「{item.Name}」（{item.Path}），但找不到對應的 .exe，所以無法以管理員身分開啟。你可以直接提供 .exe 的完整路徑，或改用一般方式開啟。", false);
                    return;
                }
                SetPending(new PendingAction
                {
                    Kind = PendingActionKind.OpenApplication,
                    Title = intent.AsAdministrator ? "準備以管理員身分開啟 application" : $"準備開啟{ItemKindLabel(item.Kind)}",
                    Details = $"名稱：{item.Name}\n位置：{item.Path}\n來源：{item.Source}\n權限：{(intent.AsAdministrator ? "管理員（Approve 後 Windows 仍會顯示 UAC）" : "一般使用者")}",
                    Item = item,
                    AsAdministrator = intent.AsAdministrator
                });
                return;

            default:
                AddMessage("助手", string.IsNullOrWhiteSpace(intent.Reply)
                    ? "我可以協助開 application，或管理你明確要求永久記住的網站。"
                    : intent.Reply, false);
                return;
        }
    }

    private void SetPending(PendingAction action)
    {
        _pendingAction = action;
        _pendingSince = DateTime.Now;
        _sendButton.Enabled = false;
        _input.Enabled = false;
        _approvalPanel.Controls.Clear();
        _chatSurface.RowStyles[2].Height = 192;
        _approvalPanel.Visible = true;

        var heading = new Label
        {
            Text = action.Title,
            Font = new Font("Segoe UI Semibold", 10F),
            Dock = DockStyle.Top,
            Height = 24
        };
        var detail = new Label
        {
            Text = action.Details,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Theme.ApprovalText
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var approve = new Button { Text = "Approve", Width = 108, Height = 36 };
        Theme.StyleButton(approve, Theme.Approve);
        approve.Click += (_, _) => ApprovePendingAction();
        var reject = new Button { Text = "Reject", Width = 98, Height = 36 };
        Theme.StyleButton(reject);
        reject.Click += (_, _) => RejectPendingAction();
        buttons.Controls.Add(approve);
        buttons.Controls.Add(reject);
        _approvalPanel.Controls.Add(detail);
        _approvalPanel.Controls.Add(buttons);
        _approvalPanel.Controls.Add(heading);
        AddMessage("助手", "我已準備好這項動作；請在下方選擇 Approve 或 Reject（開啟語音輸入時，也可以直接說 approve 或 reject）。", false);
    }

    private void ApprovePendingAction()
    {
        var action = _pendingAction;
        if (action is null)
        {
            return;
        }

        try
        {
            switch (action.Kind)
            {
                case PendingActionKind.SaveWebsite:
                    var saved = _websiteStore.Upsert(action.Website!.Alias, action.Website.Url);
                    AddMessage("助手", $"已永久記住「{saved.Alias}」。下次可說「開 {saved.Alias}」。", false);
                    break;
                case PendingActionKind.OpenWebsite:
                    var browser = BrowserLauncher.Open(action.Website!.Url);
                    AddMessage("助手", $"已用 {browser} 開啟「{action.Website.Alias}」。", false);
                    break;
                case PendingActionKind.RemoveWebsite:
                    _websiteStore.Remove(action.Website!.Alias);
                    AddMessage("助手", $"已移除「{action.Website.Alias}」的永久記錄。", false);
                    break;
                case PendingActionKind.OpenApplication:
                    var startInfo = new ProcessStartInfo(action.Item!.Path) { UseShellExecute = true };
                    if (action.AsAdministrator)
                    {
                        startInfo.Verb = "runas";
                    }
                    Process.Start(startInfo);
                    AddMessage("助手", action.AsAdministrator
                        ? $"已提出以管理員身分開啟「{action.Item.Name}」；請在 Windows UAC 確認。"
                        : $"已開啟「{action.Item.Name}」。", false);
                    break;
            }
        }
        catch (System.ComponentModel.Win32Exception exception) when (action.AsAdministrator && exception.NativeErrorCode == 1223)
        {
            AddMessage("助手", "你已在 Windows UAC 取消管理員權限，因此程式沒有開啟。", false);
        }
        catch (Exception exception)
        {
            AddMessage("助手", $"動作沒有完成：{exception.Message}", false);
        }
        finally
        {
            ClearPendingAction();
        }
    }

    private void RejectPendingAction()
    {
        AddMessage("助手", "已 Reject；沒有執行、儲存、更新或移除任何內容。", false);
        ClearPendingAction();
    }

    private void ClearPendingAction()
    {
        _pendingAction = null;
        _approvalPanel.Visible = false;
        _chatSurface.RowStyles[2].Height = 0;
        _approvalPanel.Controls.Clear();
        _input.Enabled = true;
        _sendButton.Enabled = true;
        _statusLabel.Text = $"就緒 · 已索引 {_applicationIndex.ExtendedItemCount:N0} 個項目";
        _input.Focus();
    }

    private void AddMessage(string sender, string text, bool fromUser)
    {
        _messages.SelectionStart = _messages.TextLength;
        _messages.SelectionLength = 0;
        _messages.SelectionFont = new Font("Segoe UI Semibold", 10F);
        _messages.SelectionColor = fromUser ? Theme.UserName : Theme.Text;
        _messages.AppendText(sender + "\n");
        _messages.SelectionFont = new Font("Segoe UI", 11F);
        _messages.SelectionColor = Theme.Text;
        _messages.AppendText(text.Trim() + "\n\n");
        _messages.SelectionColor = _messages.ForeColor;
        _messages.SelectionFont = _messages.Font;
        ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        if (!IsHandleCreated || _messages.IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed || _messages.IsDisposed)
            {
                return;
            }
            _messages.SelectionStart = _messages.TextLength;
            _messages.ScrollToCaret();
        });
    }

    private static bool IsSafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string ItemKindLabel(ComputerItemKind kind) => kind switch
    {
        ComputerItemKind.Folder => "資料夾",
        ComputerItemKind.File => "檔案",
        _ => " application"
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _interpreter.Dispose();
            _requestCts?.Cancel();
            _busyTimer.Dispose();
            _applicationIndex.Dispose();
        }
        base.Dispose(disposing);
    }
}
