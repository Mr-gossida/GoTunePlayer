using System.Diagnostics;
using System.Windows.Threading;

namespace TuneSync.App;

/// <summary>One shared clock, about 30 times a second, for the decorative motion (seek bar waves
/// and fire, spectrum, the screen saver's aurora). They used CompositionTarget.Rendering, which
/// runs at the monitor's refresh rate (60-144 times a second) and keeps WPF redrawing the whole
/// app every frame for as long as anyone listens - most of the app's GPU use while playing.
/// Slow, flowing visuals look the same at 30, at a fraction of the cost.</summary>
internal static class FrameTicker
{
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static EventHandler? _tick;
    private static int _listeners;

    static FrameTicker() => Timer.Tick += (_, e) => _tick?.Invoke(null, e);

    /// <summary>Time since the app started; use it, not the tick count, for motion.</summary>
    public static TimeSpan Now => Clock.Elapsed;

    public static void Add(EventHandler handler)
    {
        _tick += handler;
        if (++_listeners == 1) Timer.Start();
    }

    public static void Remove(EventHandler handler)
    {
        _tick -= handler;
        if (--_listeners <= 0)
        {
            _listeners = 0;
            Timer.Stop();
        }
    }
}
