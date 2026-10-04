using System.Runtime.InteropServices;

namespace LaunchBuddy;

// A system-wide shortcut such as Ctrl+Alt+M, delivered to a hidden message window.
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 1;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        // HWND_MESSAGE (-3): a message-only window, never shown.
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
    }

    // Returns false when another program already owns the combination.
    public bool Register(Keys combination)
    {
        Unregister();
        uint modifiers = MOD_NOREPEAT;
        if (combination.HasFlag(Keys.Control)) modifiers |= MOD_CONTROL;
        if (combination.HasFlag(Keys.Alt)) modifiers |= MOD_ALT;
        if (combination.HasFlag(Keys.Shift)) modifiers |= MOD_SHIFT;
        _registered = RegisterHotKey(Handle, HotkeyId, modifiers, (uint)(combination & Keys.KeyCode));
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
            UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    public static string Describe(Keys combination)
    {
        var key = combination & Keys.KeyCode;
        var name = key is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (key - Keys.D0))).ToString() : key.ToString();
        return string.Join(" + ", ModifierNames(combination).Append(name));
    }

    public static IEnumerable<string> ModifierNames(Keys combination)
    {
        if (combination.HasFlag(Keys.Control)) yield return "Ctrl";
        if (combination.HasFlag(Keys.Alt)) yield return "Alt";
        if (combination.HasFlag(Keys.Shift)) yield return "Shift";
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_HOTKEY && (int)message.WParam == HotkeyId)
            Pressed?.Invoke();
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
