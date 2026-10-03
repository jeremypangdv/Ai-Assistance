namespace LaunchBuddy;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchBuddy");

    public static void Error(string component, Exception exception)
    {
        // Error diagnostics only. Do not log conversation content or saved URLs.
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath, "errors.log"),
                    $"{DateTimeOffset.Now:O} [{component}] {exception}\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
