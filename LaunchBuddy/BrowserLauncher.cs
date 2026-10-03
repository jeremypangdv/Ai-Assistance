using System.Diagnostics;

namespace LaunchBuddy;

internal static class BrowserLauncher
{
    public static string Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("只允許 http 或 https 網址。");
        }

        var chromePath = FindChrome();
        if (chromePath is not null)
        {
            var info = new ProcessStartInfo(chromePath) { UseShellExecute = true };
            info.ArgumentList.Add(uri.AbsoluteUri);
            Process.Start(info);
            return "Google Chrome";
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return "預設瀏覽器";
    }

    private static string? FindChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
