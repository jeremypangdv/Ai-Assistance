namespace LaunchBuddy;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private NotifyIcon _trayIcon = null!;
    private readonly AssistantForm _form;
    private readonly FloatingButton _floatingButton;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private bool _quitting;

    public TrayApplicationContext()
    {
        _form = new AssistantForm();
        _form.FormClosing += FormClosing;

        var menu = new ContextMenuStrip();
        var maximize = new ToolStripMenuItem("Maximize（完整聊天）", null, (_, _) => _form.ShowFull()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(maximize);
        menu.Items.Add("小型聊天", null, (_, _) => ShowCompact());
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
            _trayIcon.Dispose();
            _floatingButton.Dispose();
        }

        base.Dispose(disposing);
    }
}
