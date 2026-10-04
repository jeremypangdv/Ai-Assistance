using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LaunchBuddy;

// A chat app Minibot can type into. Reading messages (for AI replies) needs per-app knowledge of its window,
// so only apps whose layout has been checked can do it.
internal sealed record ChatAppInfo(string Name, string[] ProcessNames, string[] Aliases, bool CanReadMessages);

internal static class ChatApps
{
    public static readonly ChatAppInfo Discord = new("Discord", ["Discord", "DiscordPTB", "DiscordCanary"], ["discord", "dc"], CanReadMessages: true);
    public static readonly ChatAppInfo WhatsApp = new("WhatsApp", ["WhatsApp", "WhatsApp.Root"], ["whatsapp", "whats app", "wa"], CanReadMessages: false);

    public static IReadOnlyList<ChatAppInfo> All { get; } = [Discord, WhatsApp];

    public static string SupportedNames => string.Join("、", All.Select(app => app.Name));

    public static ChatAppInfo? Match(string query)
    {
        var text = query.Trim();
        return All.FirstOrDefault(app =>
            app.Name.Equals(text, StringComparison.OrdinalIgnoreCase) ||
            app.Aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase)));
    }

    // The app's main window, or zero when it isn't open. A window hidden to the tray counts as not open.
    public static IntPtr FindWindow(ChatAppInfo app)
    {
        foreach (var name in app.ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    var window = process.MainWindowHandle;
                    if (window != IntPtr.Zero && IsWindowVisible(window))
                        return window;
                }
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
}
