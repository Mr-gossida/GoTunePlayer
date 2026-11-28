using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;

namespace TuneSync.App;

/// <summary>One shared clock, about 30 times a second, for the decorative motion (seek bar waves
/// and fire, spectrum, the screen saver's aurora). They used CompositionTarget.Rendering, which
/// runs at the monitor's refresh rate (60-144 times a second) and keeps WPF redrawing the whole
/// app every frame for as long as anyone listens - most of the app's GPU use while playing.
/// Slow, flowing visuals look the same at 30, at a fraction of the cost. Its pace follows
/// 그래픽 품질 (<see cref="GraphicsProfile"/>): about 20 on the light level, and on the high one
/// the screen's own frames after all, for those who want the smoothest motion.</summary>
internal static class FrameTicker
{
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static EventHandler? _tick;
    private static int _listeners;
    private static bool _onScreenFrames;
    private static TimeSpan _lastScreenFrame;

    static FrameTicker()
    {
        Timer.Tick += (_, e) => _tick?.Invoke(null, e);
        GraphicsProfile.Changed += () =>
        {
            if (_listeners == 0) return;
            StopClock();
            StartClock();
        };
    }

    /// <summary>Time since the app started; use it, not the tick count, for motion.</summary>
    public static TimeSpan Now => Clock.Elapsed;

    public static void Add(EventHandler handler)
    {
        _tick += handler;
        if (++_listeners == 1) StartClock();
    }

    public static void Remove(EventHandler handler)
    {
        _tick -= handler;
        if (--_listeners <= 0)
        {
            _listeners = 0;
            StopClock();
        }
    }

    private static void StartClock()
    {
        _onScreenFrames = GraphicsProfile.ScreenFrames;
        if (_onScreenFrames)
        {
            _lastScreenFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnScreenFrame;
        }
        else
        {
            Timer.Interval = GraphicsProfile.TickInterval;
            Timer.Start();
        }
    }

    private static void StopClock()
    {
        if (_onScreenFrames) CompositionTarget.Rendering -= OnScreenFrame;
        else Timer.Stop();
    }

    private static void OnScreenFrame(object? sender, EventArgs e)
    {
        // Rendering can come more than once for the same frame: one tick per frame drawn.
        if (e is RenderingEventArgs { RenderingTime: var time })
        {
            if (time == _lastScreenFrame) return;
            _lastScreenFrame = time;
        }
        _tick?.Invoke(null, e);
    }
}
