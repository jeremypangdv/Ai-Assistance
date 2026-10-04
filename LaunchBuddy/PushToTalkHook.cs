using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LaunchBuddy;

// Watches one key system-wide via a low-level keyboard hook, without swallowing it, so Ctrl shortcuts keep working.
// Pressed fires when the key goes down; Released when it comes up; Interrupted instead of Released if another key
// was pressed while it was held (a shortcut such as Ctrl+C, not push-to-talk).
internal sealed class PushToTalkHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly LowLevelKeyboardProc _callback;
    private readonly SynchronizationContext _ui;
    private IntPtr _hook;
    private bool _held;
    private bool _interrupted;

    public int VirtualKey { get; set; }
    public bool Enabled { get; set; } = true;

    public event Action? Pressed;
    public event Action? Released;
    public event Action? Interrupted;

    public PushToTalkHook(int virtualKey)
    {
        VirtualKey = virtualKey;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _callback = HookCallback; // kept in a field so the GC does not collect the delegate Windows calls
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(data);
            var down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            var up = message == WM_KEYUP || message == WM_SYSKEYUP;
            // Windows drops hooks that take too long, so only record the transition and handle it after returning.
            if (key == VirtualKey)
            {
                if (down && !_held && Enabled)
                {
                    _held = true;
                    _interrupted = false;
                    _ui.Post(_ => Pressed?.Invoke(), null);
                }
                else if (up && _held)
                {
                    _held = false;
                    if (!_interrupted)
                        _ui.Post(_ => Released?.Invoke(), null);
                }
            }
            else if (down && _held && !_interrupted)
            {
                _interrupted = true;
                _ui.Post(_ => Interrupted?.Invoke(), null);
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public static string KeyName(int virtualKey) => virtualKey switch
    {
        0xA2 => "Left Ctrl",
        0xA3 => "Right Ctrl",
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0xA4 => "Left Alt",
        0xA5 => "Right Alt",
        0x5B => "Left Windows",
        0x5C => "Right Windows",
        0x14 => "Caps Lock",
        _ => ((Keys)virtualKey).ToString()
    };

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
