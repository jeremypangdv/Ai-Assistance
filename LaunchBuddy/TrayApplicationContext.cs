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
    private readonly System.Windows.Forms.Timer _voiceStateTimer;
    private readonly System.Windows.Forms.Timer _mouseWatch;
    private readonly PushToTalkHook? _pushToTalk;
    // Loaded Whisper model, shared by "Hey Minibot" listening and push-to-talk; the microphone opens only while one is active.
    private VoiceListener? _listener;
    private Task<bool>? _listenerLoading;
    private CancellationTokenSource? _download;
    private DateTime _wakeSpokenAt = DateTime.MaxValue;
    private DateTime _wakeHeardAt = DateTime.MinValue;
    private bool _talking;
    private DateTime _talkStarted;
    private bool _offerModelDownload;

    public TrayApplicationContext()
    {
        _form = new AssistantForm();
        _form.FormClosing += FormClosing;
        _form.SettingsRequested += (_, _) => OpenSettings();
        // Creating the form installed the WinForms context; voice events arrive on background threads and are posted here.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        var maximize = new ToolStripMenuItem("Maximize（完整聊天）", null, (_, _) => _form.ShowFull()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(maximize);
        menu.Items.Add("小型聊天", null, (_, _) => ShowCompact());
        _voiceItem = new ToolStripMenuItem(VoiceMenuText);
        _voiceItem.Click += async (_, _) => await ToggleWakeListeningAsync();
        menu.Items.Add(_voiceItem);
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
        _trayIcon.ShowBalloonTip(2500, "LaunchBuddy 已就緒",
            $"點擊右下角的圓形 icon 開啟小型聊天；右鍵選 Maximize 開啟完整聊天。按住 {PushToTalkHook.KeyName(_settings.PushToTalkKey)} 可以直接說話。",
            ToolTipIcon.Info);

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

        try
        {
            _pushToTalk = new PushToTalkHook(_settings.PushToTalkKey) { Enabled = _settings.PushToTalkEnabled };
            _pushToTalk.Pressed += OnTalkPressed;
            _pushToTalk.Released += OnTalkReleased;
            _pushToTalk.Interrupted += CancelTalking;
        }
        catch (Exception exception)
        {
            AppLog.Error("PushToTalk", exception);
        }

        // Only resume automatically; the first download of the model is always the user's choice.
        if (VoiceListener.IsModelDownloaded && (_settings.VoiceEnabled || _settings.PushToTalkEnabled))
            _ = ResumeVoiceAsync();
    }

    private bool IsAwaitingCommand => DateTime.Now - _wakeHeardAt <= CommandWindow;

    private void UpdateVoiceIndicator()
    {
        var wake = _listener?.IsContinuous == true;
        _floatingButton.SetVoiceState(wake, _talking || (wake && (_form.HasPendingAction || IsAwaitingCommand)));
    }

    private async Task ResumeVoiceAsync()
    {
        if (await EnsureListenerAsync() && _settings.VoiceEnabled)
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

    private void OnTalkPressed()
    {
        if (_quitting || !_settings.PushToTalkEnabled)
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
        _ = _listener?.EndPushToTalkAsync();
        UpdateVoiceIndicator();
    }

    private void CancelTalking()
    {
        if (!_talking)
            return;
        _talking = false;
        _mouseWatch.Stop();
        try { _listener?.CancelPushToTalk(); } catch (Exception exception) { AppLog.Error("PushToTalk", exception); }
        UpdateVoiceIndicator();
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings);
        // Pressing the current key in the dialog must not start a recording.
        if (_pushToTalk is not null)
            _pushToTalk.Enabled = false;
        CancelTalking();
        var result = dialog.ShowDialog(_form);
        if (result == DialogResult.OK)
        {
            _settings.PushToTalkEnabled = dialog.PushToTalkEnabled;
            _settings.PushToTalkKey = dialog.PushToTalkKey;
            _settings.Save();
            if (_pushToTalk is not null)
                _pushToTalk.VirtualKey = _settings.PushToTalkKey;
            if (_settings.PushToTalkEnabled && VoiceListener.IsModelDownloaded)
                _ = EnsureListenerAsync();
        }
        if (_pushToTalk is not null)
            _pushToTalk.Enabled = _settings.PushToTalkEnabled;
    }

    private void OnSpeech(string text, DateTime spokenAt, bool pushToTalk)
    {
        if (_listener is null || _quitting)
            return;

        // While an Approve card is open, only a short spoken answer given after the card appeared counts.
        if (_form.HasPendingAction)
        {
            var confirmation = VoicePhrases.ParseConfirmation(text);
            if (spokenAt >= _form.PendingSince && confirmation != VoiceConfirmation.None)
                _form.ConfirmBySpeech(confirmation, text);
            return;
        }

        string command;
        if (pushToTalk)
        {
            // Holding the key already says "I'm talking to you"; a habitual "Hey Minibot" is dropped.
            command = VoicePhrases.TryStripWakePhrase(text, out var rest) ? rest : VoicePhrases.Clean(text);
            if (command.Length == 0)
                return;
        }
        else if (VoicePhrases.TryStripWakePhrase(text, out command))
        {
            if (command.Length == 0)
            {
                _wakeSpokenAt = spokenAt;
                _wakeHeardAt = DateTime.Now;
                SystemSounds.Asterisk.Play();
                ShowChatForSpeech();
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
        ShowChatForSpeech();
        _ = _form.SubmitSpokenAsync(command);
    }

    private void ShowChatForSpeech()
    {
        if (!_form.IsFullShowing && !_form.IsCompactShowing)
            ShowCompact();
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
        _singleClickTimer.Stop();
        _voiceStateTimer.Stop();
        _mouseWatch.Stop();
        _download?.Cancel();
        _pushToTalk?.Dispose();
        var listener = _listener;
        _listener = null;
        listener?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
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
            _pushToTalk?.Dispose();
            _listener?.Dispose();
            _trayIcon.Dispose();
            _floatingButton.Dispose();
        }

        base.Dispose(disposing);
    }
}
