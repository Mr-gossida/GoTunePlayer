using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TuneSync.App;

/// <summary>The reason Windows shows while the app holds up signing out or shutting down ("저장하지
/// 않은 태그 변경이 3곡 있습니다." instead of "This app is preventing you from signing out").</summary>
internal static class ShutdownBlock
{
    public static void Set(Window window, string reason)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) ShutdownBlockReasonCreate(handle, reason);
    }

    public static void Clear(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) ShutdownBlockReasonDestroy(handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
