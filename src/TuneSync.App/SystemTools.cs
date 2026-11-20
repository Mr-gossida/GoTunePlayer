using System.Diagnostics;
using System.IO;

namespace TuneSync.App;

/// <summary>Starts Windows' own programs (Explorer, the control panel, shutdown) from where Windows
/// keeps them. Given only a name, Windows may look in the app's folder and the current folder
/// first - and the current folder can be wherever a file dialog last pointed - so a program of the
/// same name lying there would run instead. A program is started and let go, never waited for, so
/// the UI thread doesn't stall; when it can't be started, a toast says so.</summary>
internal static class SystemTools
{
    private static string Explorer =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    private static string InSystemFolder(string program) => Path.Combine(Environment.SystemDirectory, program);

    /// <summary>A folder window on <paramref name="folder"/>; with <paramref name="create"/> the
    /// folder is made first if it isn't there yet (the app's own folders).</summary>
    public static void OpenFolder(string folder, bool create = false) =>
        Try("폴더를 열지 못했습니다", () =>
        {
            if (create) Directory.CreateDirectory(folder);
            Start(Explorer, $"\"{folder}\"");
        });

    /// <summary>A folder window with <paramref name="file"/> selected in it.</summary>
    public static void RevealFile(string file) =>
        Try("파일 위치를 열 수 없습니다", () => Start(Explorer, $"/select,\"{file}\""));

    /// <summary>Windows' 소리 settings (the playback devices and their formats).</summary>
    public static void OpenSoundSettings() =>
        Try("Windows 소리 설정을 열지 못했습니다", () => Start(InSystemFolder("control.exe"), "mmsys.cpl"));

    /// <summary>shutdown.exe with <paramref name="arguments"/>. False when it couldn't be started;
    /// a toast titled <paramref name="failureTitle"/> has then said why.</summary>
    public static bool RunShutdown(string arguments, string failureTitle) =>
        Try(failureTitle, () => Start(InSystemFolder("shutdown.exe"), arguments));

    private static bool Try(string failureTitle, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Toast.Error(failureTitle, ex.Message);
            return false;
        }
    }

    private static void Start(string program, string arguments)
    {
        // No console window flashing up for shutdown.exe (the flag means nothing to the windowed
        // programs). The process handle isn't needed, so it's closed straight away.
        Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }
}
