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
/// 화면 보호기), and on demand from the 도구 menu. Every monitor is covered: the player's own gets
/// the usual screen, the nearest other one the lyrics, any further ones a clock and spectrum.</summary>
public static class ScreenSaver
{
    private static PlaybackViewModel? _playback;
    private static AppSettings? _settings;
    private static List<ScreenSaverWindow>? _windows;
    private static DispatcherTimer? _watch;

    public static void Start(IServiceProvider services)
    {
        _playback = services.GetRequiredService<PlaybackViewModel>();
        _settings = services.GetRequiredService<AppSettings>();
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _watch.Tick += (_, _) =>
        {
            if (_windows is not null || !_settings.ScreenSaverEnabled || !_playback.IsPlaying) return;
            if (IdleTime() >= TimeSpan.FromMinutes(Math.Max(1, _settings.ScreenSaverMinutes))) Show();
        };
        _watch.Start();
    }

    public static void Show()
    {
        if (_windows is not null || _playback is null) return;
        ScreenSaverWindow.NewAuroraSession();
        ScreenSaverWindow.EscapeOnly = _settings?.ScreenSaverExit == ScreenSaverExit.EscapeKey;
        var player = ActiveWindow();
        var monitors = Monitors();
        var windows = new List<ScreenSaverWindow>();

        if (monitors.Count < 2 || player is null)
        {
            windows.Add(new ScreenSaverWindow(_playback, ScreenOf(player)));
        }
        else
        {
            var handle = new WindowInteropHelper(player).Handle;
            var mainHandle = MonitorFromWindow(handle, 2);
            var main = monitors.FirstOrDefault(m => m.Handle == mainHandle);
            if (main.Handle == IntPtr.Zero) main = monitors[0];
            // The lyrics go on the screen next to the player's (nearest centre).
            var lyrics = monitors.Where(m => m.Handle != main.Handle)
                                 .OrderBy(m => Distance(m.Bounds, main.Bounds)).First();

            // The light is one picture over the whole desktop, in units of the tallest screen.
            var unit = (double)monitors.Max(m => m.Bounds.Height);
            var left = monitors.Min(m => m.Bounds.X);
            var top = monitors.Min(m => m.Bounds.Y);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(player);

            foreach (var monitor in monitors)
            {
                var role = monitor.Handle == main.Handle ? ScreenSaverRole.Main
                         : monitor.Handle == lyrics.Handle ? ScreenSaverRole.Lyrics
                         : ScreenSaverRole.Ambient;
                var px = monitor.Bounds;
                var dips = new Rect(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY, px.Width / dpi.DpiScaleX, px.Height / dpi.DpiScaleY);
                var window = new ScreenSaverWindow(_playback, dips, role, lyricsElsewhere: true) { PixelBounds = px };
                window.SetAuroraRegion(new Point((px.X - left) / unit, (px.Y - top) / unit),
                                       new Size(px.Width / unit, px.Height / unit));
                windows.Add(window);
            }
        }

        _windows = windows;
        foreach (var window in windows)
        {
            // Input on any screen ends the screen saver on all of them.
            window.Closed += (_, _) =>
            {
                if (_windows is not { } open) return;
                open.Remove(window);
                foreach (var other in open.ToList()) other.Dismiss();
                if (open.Count > 0) return;
                _windows = null;
                ActiveWindow()?.Activate();
            };
            window.ShowActivated = false;
            window.Show();
        }
        // Keyboard input goes to the player's screen.
        (windows.FirstOrDefault(w => w.Role == ScreenSaverRole.Main) ?? windows[0]).Activate();
    }

    private static double Distance(Int32Rect a, Int32Rect b)
    {
        double dx = a.X + a.Width / 2.0 - (b.X + b.Width / 2.0), dy = a.Y + a.Height / 2.0 - (b.Y + b.Height / 2.0);
        return dx * dx + dy * dy;
    }

    private readonly record struct Monitor(IntPtr Handle, Int32Rect Bounds);

    /// <summary>Every monitor, with its bounds in device pixels.</summary>
    private static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var r = info.rcMonitor;
                list.Add(new Monitor(monitor, new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

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
