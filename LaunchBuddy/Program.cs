using System.Runtime.InteropServices;

namespace LaunchBuddy;

internal static class Program
{
    // Per user session: another user signed in on the same PC gets their own LaunchBuddy.
    private const string MutexName = @"Local\LaunchBuddy.SingleInstance";
    private const string ShowEventName = @"Local\LaunchBuddy.ShowWindow";
    private const int AnyProcess = -1;

    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            // Already running: bring that window up instead of starting a second copy.
            // Windows only lets the running copy take focus if this process passes its permission on.
            AllowSetForegroundWindow(AnyProcess);
            showRequested.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(showRequested));
        GC.KeepAlive(instance);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
