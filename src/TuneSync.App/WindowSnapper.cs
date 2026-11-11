using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TuneSync.App;

/// <summary>Magnetic edges for a window being dragged: when one of its edges comes within a few
/// pixels of an edge of the screen's work area (the screen minus the taskbar) it snaps flush to
/// it, and it also snaps to the screen's center lines. Works on whichever monitor the window is
/// on, at any display scale. Hooks WM_MOVING, so it applies to DragMove and title-bar drags alike.</summary>
public static class WindowSnapper
{
    private const int WM_MOVING = 0x0216;
    private const double SnapDistanceDip = 16;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_MOVING) Snap(window, hwnd, lParam);
                    return IntPtr.Zero;
                });
        };
    }

    private static void Snap(Window window, IntPtr hwnd, IntPtr lParam)
    {
        var rect = Marshal.PtrToStructure<RECT>(lParam);
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        var work = info.rcWork;
        var threshold = SnapDistanceDip * VisualTreeHelper.GetDpi(window).DpiScaleX;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        var left = Nearest(rect.Left, threshold,
            work.Left, work.Right - width, (work.Left + work.Right - width) / 2);
        var top = Nearest(rect.Top, threshold,
            work.Top, work.Bottom - height, (work.Top + work.Bottom - height) / 2);

        if (left == rect.Left && top == rect.Top) return;
        rect.Left = left;
        rect.Right = left + width;
        rect.Top = top;
        rect.Bottom = top + height;
        Marshal.StructureToPtr(rect, lParam, false);
    }

    /// <summary>The closest snap position within reach, or the position as it is.</summary>
    private static int Nearest(int position, double threshold, params int[] targets)
    {
        var best = position;
        var bestDistance = threshold;
        foreach (var target in targets)
        {
            var distance = Math.Abs(position - target);
            if (distance <= bestDistance)
            {
                best = target;
                bestDistance = distance;
            }
        }
        return best;
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
