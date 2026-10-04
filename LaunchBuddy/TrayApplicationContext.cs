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
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _voiceItem;
    private readonly System.Windows.Forms.Timer _voiceStateTimer;
    private VoiceListener? _voice;
    private CancellationTokenSource? _download;
    private DateTime _wakeSpokenAt = DateTime.MaxValue;
    private DateTime _wakeHeardAt = DateTime.MinValue;

    public TrayApplicationContext()
    {
        _form = new AssistantForm();
        _form.FormClosing += FormClosing;
        // Creating the form installed the WinForms context; voice events arrive on background threads and are posted here.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        var maximize = new ToolStripMenuItem("Maximize（完整聊天）", null, (_, _) => _form.ShowFull()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(maximize);
        menu.Items.Add("小型聊天", null, (_, _) => ShowCompact());
        _voiceItem = new ToolStripMenuItem(VoiceMenuText);
        _voiceItem.Click += async (_, _) => await ToggleVoiceAsync();
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
        _floatingButton = new FloatingButton(menu);
        _floatingButton.Clicked += (_, _) => ToggleCompact();
        _floatingButton.Show();
        // The full window covers the screen corner, so park the button while it is open.
        _form.VisibleChanged += (_, _) => UpdateFloatingButton();
        _form.Resize += (_, _) => UpdateFloatingButton();
        _trayIcon.ShowBalloonTip(2500, "LaunchBuddy 已就緒", "點擊右下角的圓形 icon 開啟小型聊天；右鍵選 Maximize 開啟完整聊天。", ToolTipIcon.Info);

        _voiceStateTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _voiceStateTimer.Tick += (_, _) => _floatingButton.SetVoiceState(_voice is not null,
            _voice is not null && (_form.HasPendingAction || IsAwaitingCommand));
        _voiceStateTimer.Start();
        // Only resume automatically; the first enable (which downloads the model) is always the user's choice.
        if (_settings.VoiceEnabled && VoiceListener.IsModelDownloaded)
            _ = StartVoiceAsync();
    }

    private bool IsAwaitingCommand => DateTime.Now - _wakeHeardAt <= CommandWindow;

    private async Task ToggleVoiceAsync()
    {
        if (_download is not null)
        {
            _download.Cancel();
            return;
        }
        if (_voice is not null)
        {
            StopVoice();
            _settings.VoiceEnabled = false;
            _settings.Save();
            _trayIcon.ShowBalloonTip(1800, "LaunchBuddy", "語音輸入已關閉，麥克風已停止使用。", ToolTipIcon.Info);
            return;
        }
        if (await StartVoiceAsync())
        {
            _settings.VoiceEnabled = true;
            _settings.Save();
        }
    }

    private async Task<bool> StartVoiceAsync()
    {
        if (!VoiceListener.IsModelDownloaded)
        {
            using var download = new CancellationTokenSource();
            _download = download;
            _voiceItem.Text = "語音輸入：正在下載模型…（點擊取消）";
            _trayIcon.ShowBalloonTip(3000, "正在下載語音模型", "Whisper small（約 190 MB），只需下載一次，之後完全在本機辨識。", ToolTipIcon.Info);
            try
            {
                var progress = new Progress<long>(bytes => _voiceItem.Text = $"語音輸入：下載中 {bytes / 1048576} MB…（點擊取消）");
                await VoiceListener.DownloadModelAsync(progress, download.Token);
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

        var listener = new VoiceListener();
        listener.Transcribed += (text, spokenAt) => _ui.Post(_ => OnSpeech(text, spokenAt), null);
        listener.Failed += exception => _ui.Post(_ =>
        {
            if (_voice != listener)
                return;
            StopVoice();
            _trayIcon.ShowBalloonTip(3000, "語音輸入已停止", exception.Message, ToolTipIcon.Error);
        }, null);
        try
        {
            // Loading the model takes a moment; keep the menu and chat responsive meanwhile.
            await Task.Run(listener.Start);
        }
        catch (Exception exception)
        {
            AppLog.Error("VoiceStart", exception);
            listener.Dispose();
            _trayIcon.ShowBalloonTip(3500, "無法啟動語音輸入",
                $"請確認已接上麥克風，並在 Windows 設定 → 隱私權 → 麥克風 允許桌面應用程式使用。\n{exception.Message}", ToolTipIcon.Error);
            return false;
        }
        if (_quitting)
        {
            listener.Dispose();
            return false;
        }

        _voice = listener;
        _voiceItem.Checked = true;
        _trayIcon.ShowBalloonTip(2500, "語音輸入已開啟", "說「Hey Minibot, open Google Chrome」；出現確認卡後說 approve 或 reject。", ToolTipIcon.Info);
        return true;
    }

    private void StopVoice()
    {
        var listener = _voice;
        _voice = null;
        _voiceItem.Checked = false;
        _wakeHeardAt = DateTime.MinValue;
        listener?.Dispose();
    }

    private void OnSpeech(string text, DateTime spokenAt)
    {
        if (_voice is null || _quitting)
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
        if (VoicePhrases.TryStripWakePhrase(text, out command))
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
        _download?.Cancel();
        StopVoice();
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
            _voice?.Dispose();
            _trayIcon.Dispose();
            _floatingButton.Dispose();
        }

        base.Dispose(disposing);
    }
}
