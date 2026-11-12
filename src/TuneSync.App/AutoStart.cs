using Microsoft.Win32;

namespace TuneSync.App;

/// <summary>"Windows 시작 시 자동 실행": an entry under the current user's Run key (no admin
/// rights needed; shows in 작업 관리자 › 시작 앱 where it can also be turned off). Started that
/// way, the app opens minimized to the taskbar.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TuneSync";
    public const string Argument = "--autostart";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command());
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }

    /// <summary>Keeps the entry pointing at this copy of the app (after an update or a move).</summary>
    public static void Refresh()
    {
        if (IsEnabled()) Set(true);
    }

    private static string Command() => $"\"{Environment.ProcessPath}\" {Argument}";
}
