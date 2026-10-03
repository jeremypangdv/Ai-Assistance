using Microsoft.Win32;
using System.Reflection;

namespace LaunchBuddy;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LaunchBuddy";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, GetStartupCommand());
    }

    private static string GetStartupCommand()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("無法確認目前的程式位置。");
        if (processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Quote(processPath);
        }

        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        return $"{Quote(processPath)} {Quote(assemblyPath)}";
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", string.Empty)}\"";
}
