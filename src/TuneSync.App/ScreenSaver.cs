using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App;

/// <summary>Starts the player's screen saver (<see cref="ScreenSaverWindow"/>) once music has been
/// playing with no mouse or keyboard input anywhere in Windows for the chosen time (Settings ›
/// 화면 보호기), and on demand from the 도구 menu.</summary>
public static class ScreenSaver
{
    private static PlaybackViewModel? _playback;
    private static AppSettings? _settings;
    private static ScreenSaverWindow? _window;
    private static DispatcherTimer? _watch;

    public static void Start(IServiceProvider services)
    {
        _playback = services.GetRequiredService<PlaybackViewModel>();
        _settings = services.GetRequiredService<AppSettings>();
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _watch.Tick += (_, _) =>
        {
            if (_window is not null || !_settings.ScreenSaverEnabled || !_playback.IsPlaying) return;
            if (IdleTime() >= TimeSpan.FromMinutes(Math.Max(1, _settings.ScreenSaverMinutes))) Show();
        };
        _watch.Start();
    }

    public static void Show()
    {
        if (_window is not null || _playback is null) return;
        _window = new ScreenSaverWindow(_playback, ScreenOf(ActiveWindow()));
        _window.Closed += (_, _) =>
        {
            _window = null;
            ActiveWindow()?.Activate();
        };
        _window.Show();
        _window.Activate();
    }

    /// <summary>The player window in use: the full player, or the mini player while it's shown.</summary>
    private static Window? ActiveWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is ShellWindow or MiniPlayerWindow)
        ?? Application.Current.MainWindow;

    /// <summary>The bounds (in WPF units) of the monitor the player is on.</summary>
    private static Rect ScreenOf(Window? window)
    {
        var fallback = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        if (window is null) return fallback;
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return fallback;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var r = info.rcMonitor;
        return new Rect(r.Left / scale.DpiScaleX, r.Top / scale.DpiScaleY,
                        (r.Right - r.Left) / scale.DpiScaleX, (r.Bottom - r.Top) / scale.DpiScaleY);
    }

    /// <summary>Time since the last mouse or keyboard input anywhere in Windows.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

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
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
