using System.Runtime.InteropServices;

namespace LaunchBuddy;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(18, 18, 20);
    public static readonly Color Sidebar = Color.FromArgb(10, 10, 12);
    public static readonly Color Surface = Color.FromArgb(28, 28, 32);
    public static readonly Color Raised = Color.FromArgb(40, 40, 46);
    public static readonly Color Border = Color.FromArgb(62, 62, 70);
    public static readonly Color Text = Color.FromArgb(232, 234, 238);
    public static readonly Color TextSecondary = Color.FromArgb(160, 166, 176);
    public static readonly Color TextMuted = Color.FromArgb(112, 118, 128);
    public static readonly Color Accent = Color.FromArgb(79, 124, 255);
    public static readonly Color UserName = Color.FromArgb(120, 165, 255);
    public static readonly Color Approve = Color.FromArgb(34, 132, 80);
    public static readonly Color ApprovalBackground = Color.FromArgb(44, 38, 22);
    public static readonly Color ApprovalText = Color.FromArgb(228, 210, 160);

    public static void StyleButton(Button button, Color? background = null)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = background ?? Raised;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = background is null ? 1 : 0;
        button.Cursor = Cursors.Hand;
    }

    // Dark title bar (Windows 10 20H1+ / 11); ignored on older versions.
    public static void UseDarkTitleBar(Form form)
    {
        var enabled = 1;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
    }

    // Dark scrollbars on native controls such as RichTextBox and multiline TextBox.
    public static void UseDarkScrollBars(Control control)
    {
        if (control.IsHandleCreated)
            _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        else
            control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
}
