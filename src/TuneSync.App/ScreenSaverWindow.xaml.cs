using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TuneSync.App.ViewModels;

namespace TuneSync.App;

/// <summary>Full-screen "now playing" view shown after a while without input during playback:
/// the cover, the song, the line being sung and a clock over the cover-colored backdrop. Any
/// mouse movement or key brings the player back.</summary>
public partial class ScreenSaverWindow : Window
{
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _drift;
    private Point? _firstMouse;
    private bool _closing;

    public ScreenSaverWindow(PlaybackViewModel playback, Rect screen)
    {
        InitializeComponent();
        DataContext = playback;
        Left = screen.Left;
        Top = screen.Top;
        Width = screen.Width;
        Height = screen.Height;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _drift = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _drift.Tick += (_, _) => DriftSomewhere();

        Loaded += (_, _) =>
        {
            UpdateClock();
            _clock.Start();
            _drift.Start();
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));
            Hint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.5)) { BeginTime = TimeSpan.FromSeconds(4) });
        };
        Closed += (_, _) =>
        {
            _clock.Stop();
            _drift.Stop();
        };
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        DateText.Text = now.ToString("M월 d일 dddd", CultureInfo.GetCultureInfo("ko-KR"));
    }

    /// <summary>A slow glide to a nearby spot (burn-in protection on OLED and plasma screens).</summary>
    private void DriftSomewhere()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromSeconds(10);
        Drift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(Random.Shared.Next(-90, 91), duration) { EasingFunction = ease });
        Drift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(Random.Shared.Next(-50, 51), duration) { EasingFunction = ease });
    }

    // --- any real input ends it (a tiny mouse jitter doesn't) ---

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_firstMouse is not { } first)
        {
            _firstMouse = position;
            return;
        }
        if (Math.Abs(position.X - first.X) + Math.Abs(position.Y - first.Y) > 12) Dismiss();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e) => Dismiss();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    /// <summary>Fades out, then closes.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }
}
