using System.Media;

namespace LaunchBuddy;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private NotifyIcon _trayIcon = null!;
    private readonly AssistantForm _form;
    private readonly FloatingButton _floatingButton;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private bool _quitting;

    private const string VoiceMenuText = "語音輸入（Hey Minibot）";
    // After a bare "Hey Minibot", the next thing said within this window is taken as the command.
    private static readonly TimeSpan CommandWindow = TimeSpan.FromSeconds(8);
    // A shorter press is a tap of the key, not push-to-talk.
    private static readonly TimeSpan MinimumHold = TimeSpan.FromMilliseconds(250);
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _voiceItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly System.Windows.Forms.Timer _voiceStateTimer;
    private readonly System.Windows.Forms.Timer _mouseWatch;
    private readonly PushToTalkHook? _pushToTalk;
    private readonly GlobalHotkey? _quickToggle;
    private string? _quickToggleProblem;
    // Voice requests made while the chat is hidden are shown here instead of opening the chat.
    private readonly VoicePopup _popup = new();
    // The listening card waits for MinimumHold, so Ctrl+C and quick taps don't flash it.
    private readonly System.Windows.Forms.Timer _listeningDelay;
    // Loaded Whisper model, shared by "Hey Minibot" listening and push-to-talk; the microphone opens only while one is active.
    private VoiceListener? _listener;
    private Task<bool>? _listenerLoading;
    private CancellationTokenSource? _download;
    private DateTime _wakeSpokenAt = DateTime.MaxValue;
    private DateTime _wakeHeardAt = DateTime.MinValue;
    private bool _talking;
    private DateTime _talkStarted;
    private bool _offerModelDownload;
    // Quick-toggle pause: push-to-talk and Hey Minibot both off until toggled back; not saved.
    private bool _voicePaused;
    // The popup shows a transient listening card (key held, or a bare "Hey Minibot").
    private bool _popupListening;
    private bool _settingsOpen;
    // Set while Minibot controls a chat app (top bar showing).
    private ChatControlSession? _chatSession;
    private string _lastReply = "";
    // Signalled when LaunchBuddy is started again while this copy is running.
    private readonly RegisteredWaitHandle _showRequest;

    public TrayApplicationContext(EventWaitHandle showRequested)
    {
        _form = new AssistantForm();
        _form.FormClosing += FormClosing;
        _form.SettingsRequested += (_, _) => OpenSettings();
        _form.AssistantReplied += text => _lastReply = text;
        _form.ChatControlRequested += StartChatControl;
        // Creating the form installed the WinForms context; voice events arrive on background threads and are posted here.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _showRequest = ThreadPool.RegisterWaitForSingleObject(showRequested, (_, _) => _ui.Post(_ =>
        {
            if (!_quitting)
                _form.ShowFull();
        }, null), null, Timeout.Infinite, executeOnlyOnce: false);

        _popup.ApproveClicked += () =>
        {
            _form.ApprovePending();
            _popup.ShowResult(_lastReply);
        };
        _popup.RejectClicked += () =>
        {
            _form.RejectPending();
            _popup.ShowResult(_lastReply);
        };

        var menu = new ContextMenuStrip();
        var maximize = new ToolStripMenuItem("Maximize（完整聊天）", null, (_, _) => _form.ShowFull()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(maximize);
        menu.Items.Add("小型聊天", null, (_, _) => ShowCompact());
        _voiceItem = new ToolStripMenuItem(VoiceMenuText);
        _voiceItem.Click += async (_, _) => await ToggleWakeListeningAsync();
        menu.Items.Add(_voiceItem);
        _pauseItem = new ToolStripMenuItem("暫停語音輸入", null, (_, _) => ToggleVoicePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("設定（語音、網站）…", null, (_, _) => OpenSettings());
        var runAtStartup = new ToolStripMenuItem("隨 Windows 開機啟動") { Checked = StartupManager.IsEnabled(), CheckOnClick = false };
        runAtStartup.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(!runAtStartup.Checked);
                runAtStartup.Checked = StartupManager.IsEnabled();
                _trayIcon.ShowBalloonTip(1800, "LaunchBuddy", runAtStartup.Checked ? "已設定為隨 Windows 開機啟動。" : "已取消隨 Windows 開機啟動。", ToolTipIcon.Info);
            }
            catch (Exception exception)
            {
                _trayIcon.ShowBalloonTip(2500, "無法更新開機啟動設定", exception.Message, ToolTipIcon.Error);
            }
        };
        menu.Items.Add(runAtStartup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束 LaunchBuddy", null, (_, _) => Quit());

        _singleClickTimer = new System.Windows.Forms.Timer { Interval = 260 };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            ToggleCompact();
        };

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "LaunchBuddy — 本機啟動助手",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += TrayMouseClick;
        _trayIcon.MouseDoubleClick += TrayMouseDoubleClick;
        _trayIcon.BalloonTipClicked += async (_, _) =>
        {
            if (_offerModelDownload)
            {
                _offerModelDownload = false;
                await EnsureListenerAsync();
            }
        };
        _trayIcon.BalloonTipClosed += (_, _) => _offerModelDownload = false;
        _floatingButton = new FloatingButton(menu);
        _floatingButton.Clicked += (_, _) => ToggleCompact();
        _floatingButton.Show();
        // The full window covers the screen corner, so park the button while it is open.
        _form.VisibleChanged += (_, _) => UpdateFloatingButton();
        _form.Resize += (_, _) => UpdateFloatingButton();
        // Opening the chat takes over from the popup; it shows the same conversation and Approve card.
        _form.VisibleChanged += (_, _) =>
        {
            if (_form.Visible)
            {
                _popupListening = false;
                _popup.HidePopup();
            }
        };

        _voiceStateTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _voiceStateTimer.Tick += (_, _) => UpdateVoiceIndicator();
        _voiceStateTimer.Start();

        // A mouse click while the key is held means Ctrl+click, not push-to-talk.
        _mouseWatch = new System.Windows.Forms.Timer { Interval = 50 };
        _mouseWatch.Tick += (_, _) =>
        {
            if (_talking && Control.MouseButtons != MouseButtons.None)
                CancelTalking();
        };

        _listeningDelay = new System.Windows.Forms.Timer { Interval = (int)MinimumHold.TotalMilliseconds };
        _listeningDelay.Tick += (_, _) =>
        {
            _listeningDelay.Stop();
            // While a spoken request is still processing, its card stays; the red button already shows the key is held.
            if (!_talking || _form.Visible || _form.IsBusy)
                return;
            _popupListening = true;
            _popup.ShowListening(Anchor,
                _form.HasPendingAction ? "說 approve 或 reject，放開按鍵送出。"
                : _chatSession is { Mode: ChatMode.PushToTalk } session ? $"說出要打進 {session.App.Name} 的訊息，放開按鍵輸入。"
                : "說出指令，放開按鍵送出。");
        };

        try
        {
            _pushToTalk = new PushToTalkHook(_settings.PushToTalkKey);
            _pushToTalk.Pressed += OnTalkPressed;
            _pushToTalk.Released += OnTalkReleased;
            _pushToTalk.Interrupted += CancelTalking;
            ApplyPushToTalkEnabled();
        }
        catch (Exception exception)
        {
            AppLog.Error("PushToTalk", exception);
        }

        try
        {
            _quickToggle = new GlobalHotkey();
            _quickToggle.Pressed += ToggleVoicePause;
        }
        catch (Exception exception)
        {
            AppLog.Error("QuickToggle", exception);
        }
        RegisterQuickToggle();

        var toggleHint = _pauseItem.ShortcutKeyDisplayString is { } shortcut ? $"按 {shortcut} 可暫停／恢復語音輸入。" : "";
        _trayIcon.ShowBalloonTip(2500, "LaunchBuddy 已就緒",
            $"點擊右下角的圓形 icon 開啟小型聊天；右鍵選 Maximize 開啟完整聊天。按住 {PushToTalkHook.KeyName(_settings.PushToTalkKey)} 可以直接說話。{toggleHint}",
            ToolTipIcon.Info);

        // Only resume automatically; the first download of the model is always the user's choice.
        if (VoiceListener.IsModelDownloaded && (_settings.VoiceEnabled || _settings.PushToTalkEnabled))
            _ = ResumeVoiceAsync();
    }

    private bool IsAwaitingCommand => DateTime.Now - _wakeHeardAt <= CommandWindow;

    private Rectangle Anchor => _floatingButton.Bounds;

    private void UpdateVoiceIndicator()
    {
        var wake = _listener?.IsContinuous == true;
        _floatingButton.SetVoiceState(wake, _talking || (wake && (_form.HasPendingAction || IsAwaitingCommand)));
        // The "say your command" card from a bare Hey Minibot closes when the command window runs out.
        if (_popupListening && !_talking && !IsAwaitingCommand)
        {
            _popupListening = false;
            RestorePopup();
        }
    }

    private async Task ResumeVoiceAsync()
    {
        if (await EnsureListenerAsync() && _settings.VoiceEnabled && !_voicePaused)
            StartWakeListening(announce: false);
    }

    // Downloads the model on first use and loads it once; later calls reuse it.
    private Task<bool> EnsureListenerAsync()
    {
        if (_listener is not null)
            return Task.FromResult(true);
        if (!IsListenerLoading)
            _listenerLoading = LoadListenerAsync();
        return _listenerLoading!;
    }

    private bool IsListenerLoading => _listenerLoading is { IsCompleted: false };

    private async Task<bool> LoadListenerAsync()
    {
        if (!VoiceListener.IsModelDownloaded && !await DownloadModelAsync())
            return false;

        var listener = new VoiceListener();
        listener.Transcribed += (text, spokenAt, pushToTalk) => _ui.Post(_ => OnSpeech(text, spokenAt, pushToTalk), null);
        listener.Failed += exception => _ui.Post(_ =>
        {
            if (_listener != listener)
                return;
            StopWakeListening();
            CancelTalking();
            _trayIcon.ShowBalloonTip(3000, "語音輸入已停止", exception.Message, ToolTipIcon.Error);
        }, null);
        try
        {
            // Loading the model takes a moment; keep the menu and chat responsive meanwhile.
            await Task.Run(listener.LoadModel);
        }
        catch (Exception exception)
        {
            AppLog.Error("VoiceLoad", exception);
            listener.Dispose();
            _trayIcon.ShowBalloonTip(3000, "無法載入語音模型", exception.Message, ToolTipIcon.Error);
            return false;
        }
        if (_quitting)
        {
            listener.Dispose();
            return false;
        }
        _listener = listener;
        return true;
    }

    private async Task<bool> DownloadModelAsync()
    {
        using var download = new CancellationTokenSource();
        _download = download;
        _voiceItem.Text = "語音輸入：正在下載模型…（點擊取消）";
        _trayIcon.ShowBalloonTip(3000, "正在下載語音模型", "Whisper small（約 190 MB），只需下載一次，之後完全在本機辨識。", ToolTipIcon.Info);
        try
        {
            var progress = new Progress<long>(bytes => _voiceItem.Text = $"語音輸入：下載中 {bytes / 1048576} MB…（點擊取消）");
            await VoiceListener.DownloadModelAsync(progress, download.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            _trayIcon.ShowBalloonTip(1800, "LaunchBuddy", "已取消下載語音模型。", ToolTipIcon.Info);
            return false;
        }
        catch (Exception exception)
        {
            AppLog.Error("VoiceDownload", exception);
            _trayIcon.ShowBalloonTip(3000, "無法下載語音模型", exception.Message, ToolTipIcon.Error);
            return false;
        }
        finally
        {
            _download = null;
            _voiceItem.Text = VoiceMenuText;
        }
    }

    private async Task ToggleWakeListeningAsync()
    {
        if (_download is not null)
        {
            _download.Cancel();
            return;
        }
        if (_listener?.IsContinuous == true)
        {
            StopWakeListening();
            _settings.VoiceEnabled = false;
            _settings.Save();
            _trayIcon.ShowBalloonTip(1800, "LaunchBuddy", "Hey Minibot 語音輸入已關閉，麥克風已停止使用。", ToolTipIcon.Info);
            return;
        }
        // Turning Hey Minibot on by hand also ends a quick-toggle pause.
        SetVoicePaused(false);
        if (await EnsureListenerAsync() && StartWakeListening(announce: true))
        {
            _settings.VoiceEnabled = true;
            _settings.Save();
        }
    }

    private bool StartWakeListening(bool announce)
    {
        try
        {
            _listener!.SetContinuous(true);
        }
        catch (Exception exception)
        {
            AppLog.Error("VoiceStart", exception);
            ShowMicrophoneError(exception);
            return false;
        }
        _voiceItem.Checked = true;
        if (announce)
            _trayIcon.ShowBalloonTip(2500, "語音輸入已開啟", "說「Hey Minibot, open Google Chrome」；出現確認卡後說 approve 或 reject。", ToolTipIcon.Info);
        return true;
    }

    private void StopWakeListening()
    {
        _voiceItem.Checked = false;
        _wakeHeardAt = DateTime.MinValue;
        try { _listener?.SetContinuous(false); } catch (Exception exception) { AppLog.Error("VoiceStop", exception); }
    }

    private void ShowMicrophoneError(Exception exception) =>
        _trayIcon.ShowBalloonTip(3500, "無法使用麥克風",
            $"請確認已接上麥克風，並在 Windows 設定 → 隱私權 → 麥克風 允許桌面應用程式使用。\n{exception.Message}", ToolTipIcon.Error);

    private void ApplyPushToTalkEnabled()
    {
        if (_pushToTalk is not null)
            _pushToTalk.Enabled = _settings.PushToTalkEnabled && !_voicePaused;
    }

    private void RegisterQuickToggle()
    {
        _quickToggleProblem = null;
        _pauseItem.ShortcutKeyDisplayString = null;
        if (_quickToggle is null || !_settings.QuickToggleEnabled)
        {
            _quickToggle?.Unregister();
            return;
        }
        var keys = (Keys)_settings.QuickToggleKeys;
        if (_quickToggle.Register(keys))
            _pauseItem.ShortcutKeyDisplayString = GlobalHotkey.Describe(keys);
        else
            _quickToggleProblem = $"{GlobalHotkey.Describe(keys)} 已被其他程式使用，請換一組快捷鍵。";
    }

    private void ToggleVoicePause()
    {
        if (_quitting)
            return;
        SetVoicePaused(!_voicePaused);
        var shortcut = _pauseItem.ShortcutKeyDisplayString;
        var (title, text) = _voicePaused
            ? ("語音輸入已暫停", shortcut is null ? "按住說話和 Hey Minibot 都不會使用麥克風。" : $"按住說話和 Hey Minibot 都不會使用麥克風。再按 {shortcut} 恢復。")
            : ("語音輸入已恢復", $"按住 {PushToTalkHook.KeyName(_settings.PushToTalkKey)} 可以直接說話。");
        // Don't cover an Approve card or the open chat with the notice.
        if (_form.Visible || _form.HasPendingAction || _form.IsBusy)
            _trayIcon.ShowBalloonTip(1800, title, text, ToolTipIcon.Info);
        else
            _popup.ShowNotice(Anchor, title, text);
    }

    private void SetVoicePaused(bool paused)
    {
        if (_voicePaused == paused)
            return;
        _voicePaused = paused;
        _pauseItem.Checked = paused;
        if (paused)
        {
            CancelTalking();
            StopWakeListening();
        }
        else if (_settings.VoiceEnabled && _listener is not null)
        {
            StartWakeListening(announce: false);
        }
        ApplyPushToTalkEnabled();
    }

    private void OnTalkPressed()
    {
        if (_quitting || !_settings.PushToTalkEnabled || _voicePaused)
            return;
        if (_listener is null)
        {
            if (!VoiceListener.IsModelDownloaded && !IsListenerLoading)
            {
                _offerModelDownload = true;
                _trayIcon.ShowBalloonTip(5000, "按住說話需要語音模型",
                    "第一次使用需要下載 Whisper 語音模型（約 190 MB），之後完全在本機辨識。點這裡開始下載。", ToolTipIcon.Info);
            }
            return;
        }
        try
        {
            _listener.BeginPushToTalk();
        }
        catch (Exception exception)
        {
            AppLog.Error("PushToTalk", exception);
            ShowMicrophoneError(exception);
            return;
        }
        _talking = true;
        _talkStarted = DateTime.Now;
        _mouseWatch.Start();
        _listeningDelay.Stop();
        _listeningDelay.Start();
        UpdateVoiceIndicator();
    }

    private void OnTalkReleased()
    {
        if (!_talking)
            return;
        if (DateTime.Now - _talkStarted < MinimumHold)
        {
            CancelTalking();
            return;
        }
        _talking = false;
        _mouseWatch.Stop();
        _listeningDelay.Stop();
        var popupShown = _popupListening;
        _popupListening = false;
        if (popupShown)
            _popup.ShowTranscribing();
        _ = FinishTalkingAsync(popupShown);
        UpdateVoiceIndicator();
    }

    private async Task FinishTalkingAsync(bool popupShown)
    {
        if (_listener is not { } listener)
            return;
        bool queued;
        try
        {
            queued = await listener.EndPushToTalkAsync();
        }
        catch (Exception exception)
        {
            AppLog.Error("PushToTalk", exception);
            queued = false;
        }
        // Nothing will be transcribed, so the "正在辨識" card would otherwise just sit there.
        if (!queued && popupShown && !_talking && !_form.Visible)
            RestorePopup("沒有聽到聲音，請按住按鍵再說一次。");
    }

    private void CancelTalking()
    {
        _listeningDelay.Stop();
        if (!_talking)
            return;
        _talking = false;
        _mouseWatch.Stop();
        try { _listener?.CancelPushToTalk(); } catch (Exception exception) { AppLog.Error("PushToTalk", exception); }
        if (_popupListening)
        {
            _popupListening = false;
            RestorePopup();
        }
        UpdateVoiceIndicator();
    }

    // Puts back whatever the listening card replaced: the Approve card if one is open, otherwise nothing (or a short notice).
    private void RestorePopup(string? problem = null)
    {
        if (_form.CurrentPendingAction is { } action)
            _popup.ShowPending(Anchor, action, problem is null ? null : problem + "\n請說 approve 或 reject，或點下方按鈕。");
        else if (problem is not null)
            _popup.ShowNotice(Anchor, "沒有聽清楚", problem);
        else
            _popup.HidePopup();
    }

    private void StartChatControl(ChatAppInfo app)
    {
        var window = ChatApps.FindWindow(app);
        if (window == IntPtr.Zero)
        {
            _trayIcon.ShowBalloonTip(2500, $"無法控制 {app.Name}", $"{app.Name} 已經沒有開著。", ToolTipIcon.Warning);
            return;
        }
        // One chat at a time: taking over another ends the current one.
        _chatSession?.Stop($"改為控制 {app.Name}。");
        var session = new ChatControlSession(new ChatWindow(app, window), _form.Interpreter, PushToTalkHook.KeyName(_settings.PushToTalkKey));
        session.Ended += reason =>
        {
            if (_chatSession == session)
                _chatSession = null;
            if (!_quitting)
                _trayIcon.ShowBalloonTip(2000, $"已停止控制 {app.Name}", reason, ToolTipIcon.Info);
        };
        _chatSession = session;
        session.Start();
    }

    private void OpenSettings()
    {
        if (_settingsOpen)
            return;
        _settingsOpen = true;
        using var dialog = new SettingsForm(_settings, _form.Websites.All(), _quickToggleProblem);
        // Pressing the current keys in the dialog must not start a recording or toggle the pause.
        if (_pushToTalk is not null)
            _pushToTalk.Enabled = false;
        _quickToggle?.Unregister();
        CancelTalking();
        // From the icon menu the chat is hidden, and a hidden owner would center the dialog on nothing.
        if (!_form.Visible)
            dialog.StartPosition = FormStartPosition.CenterScreen;
        var result = _form.Visible ? dialog.ShowDialog(_form) : dialog.ShowDialog();
        _settingsOpen = false;
        if (result == DialogResult.OK)
        {
            if (dialog.WebsitesChanged)
            {
                try
                {
                    _form.Websites.ReplaceAll(dialog.Websites);
                }
                catch (Exception exception)
                {
                    AppLog.Error("WebsiteStore", exception);
                    _trayIcon.ShowBalloonTip(3000, "無法儲存網站清單", exception.Message, ToolTipIcon.Error);
                }
            }
            _settings.PushToTalkEnabled = dialog.PushToTalkEnabled;
            _settings.PushToTalkKey = dialog.PushToTalkKey;
            _settings.QuickToggleEnabled = dialog.QuickToggleEnabled;
            _settings.QuickToggleKeys = (int)dialog.QuickToggleKeys;
            _settings.Save();
            if (_pushToTalk is not null)
                _pushToTalk.VirtualKey = _settings.PushToTalkKey;
            if (_settings.PushToTalkEnabled && VoiceListener.IsModelDownloaded)
                _ = EnsureListenerAsync();
        }
        ApplyPushToTalkEnabled();
        RegisterQuickToggle();
        if (_quickToggleProblem is not null)
            _trayIcon.ShowBalloonTip(3000, "快速開關快捷鍵無法使用", _quickToggleProblem, ToolTipIcon.Warning);
    }

    private void OnSpeech(string text, DateTime spokenAt, bool pushToTalk)
    {
        if (_listener is null || _quitting || _voicePaused)
            return;

        // With the chat hidden, voice requests stay in the popup beside the button instead of opening the chat.
        var usePopup = !_form.Visible;
        _popupListening = false;

        // While an Approve card is open, only a short spoken answer given after the card appeared counts.
        if (_form.CurrentPendingAction is { } pending)
        {
            var confirmation = VoicePhrases.ParseConfirmation(text);
            if (spokenAt >= _form.PendingSince && confirmation != VoiceConfirmation.None)
            {
                _form.ConfirmBySpeech(confirmation, text);
                if (usePopup)
                    _popup.ShowResult(_lastReply);
            }
            else if (usePopup && pushToTalk)
            {
                _popup.ShowPending(Anchor, pending, "沒聽清楚，請按住按鍵再說一次 approve 或 reject。");
            }
            return;
        }

        // Controlling a chat in push-to-talk mode: what was said is the message, typed into the chat unsent.
        if (pushToTalk && _chatSession is { Mode: ChatMode.PushToTalk } session)
        {
            var message = VoicePhrases.Clean(text);
            if (message.Length == 0)
            {
                RestorePopup("沒有聽清楚，請按住按鍵再說一次。");
                return;
            }
            _popup.HidePopup();
            _ = session.DictateAsync(message);
            return;
        }

        string command;
        if (pushToTalk)
        {
            // Holding the key already says "I'm talking to you"; a habitual "Hey Minibot" is dropped.
            command = VoicePhrases.TryStripWakePhrase(text, out var rest) ? rest : VoicePhrases.Clean(text);
            if (command.Length == 0)
            {
                if (usePopup)
                    RestorePopup("沒有聽清楚，請按住按鍵再說一次。");
                return;
            }
        }
        else if (VoicePhrases.TryStripWakePhrase(text, out command))
        {
            if (command.Length == 0)
            {
                _wakeSpokenAt = spokenAt;
                _wakeHeardAt = DateTime.Now;
                SystemSounds.Asterisk.Play();
                if (usePopup && !_form.IsBusy)
                {
                    _popupListening = true;
                    _popup.ShowListening(Anchor, "請說出指令，例如「open Google Chrome」。");
                }
                return;
            }
        }
        else if (IsAwaitingCommand && spokenAt > _wakeSpokenAt)
        {
            command = text;
        }
        else
        {
            return;
        }

        _wakeHeardAt = DateTime.MinValue;
        if (_form.IsBusy)
            return;
        if (usePopup)
            _ = SubmitFromPopupAsync(command);
        else
            _ = _form.SubmitSpokenAsync(command);
    }

    private async Task SubmitFromPopupAsync(string command)
    {
        _popup.ShowProcessing(Anchor, command);
        await _form.SubmitSpokenAsync(command);
        // The user opened the chat meanwhile, or closed the card.
        if (_quitting || _form.Visible || !_popup.Visible)
            return;
        if (_form.CurrentPendingAction is { } action)
            _popup.ShowPending(Anchor, action);
        else
            _popup.ShowResult(_lastReply);
    }

    private void ToggleCompact()
    {
        if (_form.IsCompactShowing)
            _form.Hide();
        else
            ShowCompact();
    }

    private void UpdateFloatingButton()
    {
        if (!_quitting && !_floatingButton.IsDisposed)
            _floatingButton.Visible = !_form.IsFullShowing;
    }

    private void ShowCompact() => _form.ShowCompact(_floatingButton.Bounds);

    private void TrayMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _singleClickTimer.Stop();
            _singleClickTimer.Start();
        }
    }

    private void TrayMouseDoubleClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _singleClickTimer.Stop();
            _form.ShowFull();
        }
    }

    private void FormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!_quitting && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            _form.Hide();
        }
    }

    private void Quit()
    {
        _quitting = true;
        _showRequest.Unregister(null);
        _singleClickTimer.Stop();
        _voiceStateTimer.Stop();
        _mouseWatch.Stop();
        _listeningDelay.Stop();
        _download?.Cancel();
        _pushToTalk?.Dispose();
        _quickToggle?.Dispose();
        _chatSession?.Dispose();
        var listener = _listener;
        _listener = null;
        listener?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _popup.Close();
        _floatingButton.Close();
        _form.Close();
        _form.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _singleClickTimer.Dispose();
            _voiceStateTimer.Dispose();
            _mouseWatch.Dispose();
            _listeningDelay.Dispose();
            _pushToTalk?.Dispose();
            _quickToggle?.Dispose();
            _listener?.Dispose();
            _trayIcon.Dispose();
            _popup.Dispose();
            _floatingButton.Dispose();
        }

        base.Dispose(disposing);
    }
}
