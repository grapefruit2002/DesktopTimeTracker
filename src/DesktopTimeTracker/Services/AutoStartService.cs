using Microsoft.Win32;

namespace DesktopTimeTracker.Services;

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopTimeTracker";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    // Returns true if the registry value matches the current executable path,
    // i.e. the auto-start entry is still valid (points at the right file).
    public static bool IsPointingAtCurrentExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        string? value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string exe = Environment.ProcessPath ?? "";
        // Expected format: "<full path to exe>" --minimized
        return !string.IsNullOrEmpty(exe) && value.Contains($"\"{exe}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled)
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径");
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}