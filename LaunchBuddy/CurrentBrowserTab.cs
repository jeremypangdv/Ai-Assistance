using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace LaunchBuddy;

internal sealed record BrowserTab(string Url, string Title, string Browser);

// Reads the address bar of the browser window the user was last in, through UI Automation.
// Only the browser's own toolbar is read; no page content, cookies or history.
internal static class CurrentBrowserTab
{
    private static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi",
        ["opera"] = "Opera",
        ["firefox"] = "Firefox"
    };

    // The highest browser window in the Z-order: the one in front when spoken to with the chat hidden,
    // and the one just left when the chat window has the focus.
    public static BrowserTab? Capture()
    {
        foreach (var (window, browser) in BrowserWindowsTopDown())
        {
            try
            {
                var url = ReadAddressBar(window);
                if (url is not null)
                    return new BrowserTab(url, PageTitle(window, browser), browser);
            }
            catch (Exception exception)
            {
                AppLog.Error("BrowserTab", exception);
            }
        }
        return null;
    }

    private static List<(IntPtr Window, string Browser)> BrowserWindowsTopDown()
    {
        var found = new List<(IntPtr, string)>();
        // EnumWindows walks top-level windows from the top of the Z-order down.
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window) || GetWindowTextLength(window) == 0)
                return true;
            GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (Browsers.TryGetValue(process.ProcessName, out var browser))
                    found.Add((window, browser));
            }
            catch (ArgumentException)
            {
                // The process exited while enumerating.
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string? ReadAddressBar(IntPtr window)
    {
        var root = AutomationElement.FromHandle(window);
        // Chromium browsers and Firefox both expose the address bar as the first Edit control of the window.
        var edit = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        if (edit is null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            return null;
        return NormalizeUrl(((ValuePattern)pattern).Current.Value);
    }

    // Chromium hides "https://" in the address bar; anything that isn't a web address (new tab page, typed search words) is rejected.
    public static string? NormalizeUrl(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(' '))
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.Host.Contains('.') && uri.Host != "localhost")
            return null;
        return uri.AbsoluteUri;
    }

    // Window titles look like "Page title - Google Chrome" or "Page title - Personal - Microsoft Edge".
    private static string PageTitle(IntPtr window, string browser)
    {
        var length = GetWindowTextLength(window);
        var buffer = new StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        var title = buffer.ToString();
        var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (cut <= 0)
            cut = title.LastIndexOf(" — ", StringComparison.Ordinal);
        if (cut > 0 && title[(cut + 3)..].Contains(browser.Split(' ')[^1], StringComparison.OrdinalIgnoreCase))
            title = title[..cut];
        return title.Trim();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
