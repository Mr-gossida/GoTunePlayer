using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly PlaybackViewModel _playback;
    private Point? _firstMouse;
    private bool _closing;

    public ScreenSaverWindow(PlaybackViewModel playback, Rect screen)
    {
        InitializeComponent();
        _playback = playback;
        DataContext = playback;
        playback.PropertyChanged += Playback_PropertyChanged;
        PlayerPalette.Changed += RecolorAurora;

        // The aurora needs pixel shader 3.0 (any GPU from the last ~15 years); without it the
        // backdrop is shown as before.
        if (!RenderCapability.IsPixelShaderVersionSupported(3, 0)) AuroraLayer.Visibility = Visibility.Collapsed;
        Left = screen.Left;
        Top = screen.Top;
        Width = screen.Width;
        Height = screen.Height;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _drift = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _drift.Tick += (_, _) => DriftSomewhere();
        _lyricsGate.Tick += LyricsGate_Tick;

        Loaded += (_, _) =>
        {
            UpdateClock();
            StartAurora();
            // Open on the line being sung, without a glide (once the lines are laid out).
            Dispatcher.BeginInvoke(() => Views.LyricsScroller.SnapTo(LyricsViewer, LyricsItems, _playback.CurrentLyricsIndex),
                DispatcherPriority.Loaded);
            _clock.Start();
            _drift.Start();
            if (_playback.IsIntroVisible) HideLyrics(instant: true);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));
            Hint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.5)) { BeginTime = TimeSpan.FromSeconds(4) });
        };
        Closed += (_, _) =>
        {
            playback.PropertyChanged -= Playback_PropertyChanged;
            PlayerPalette.Changed -= RecolorAurora;
            AuroraLight.BeginAnimation(Effects.AuroraEffect.TimeProperty, null);
            Views.LyricsScroller.Stop(LyricsViewer);
            _lyricsGate.Stop();
            _clock.Stop();
            _drift.Stop();
        };
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackViewModel.CurrentLyricsIndex):
                Views.LyricsScroller.CenterOn(LyricsViewer, LyricsItems, _playback.CurrentLyricsIndex);
                break;
            case nameof(PlaybackViewModel.CurrentFile):
                Views.LyricsScroller.Reset(LyricsViewer); // a new song starts from its first line
                HideLyrics(instant: true);
                ScheduleLyricsReveal();
                break;
            case nameof(PlaybackViewModel.IsIntroVisible):
                if (_playback.IsIntroVisible) HideLyrics(instant: false);
                ScheduleLyricsReveal();
                break;
        }
    }

    // --- synced lyrics wait for the singing ---
    // During a song's opening (the player's intro) the lines stay hidden; a few seconds before the
    // first one they rise in out of a blur, like in the player. A new song starts hidden too, and
    // is shown only once it's clear there's no intro (the intro is known a moment after loading).

    private bool _lyricsHidden;
    private readonly DispatcherTimer _lyricsGate = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void HideLyrics(bool instant)
    {
        if (_lyricsHidden) return;
        _lyricsHidden = true;
        LyricsViewer.Effect = null;
        LyricsViewer.BeginAnimation(OpacityProperty, instant ? null
            : new DoubleAnimation(LyricsViewer.Opacity, 0, TimeSpan.FromMilliseconds(500)));
        if (instant) LyricsViewer.Opacity = 0;
    }

    private void ScheduleLyricsReveal()
    {
        if (_lyricsGate is null) return;
        _lyricsGate.Stop();
        if (!_playback.IsIntroVisible) _lyricsGate.Start();
    }

    private void LyricsGate_Tick(object? sender, EventArgs e)
    {
        _lyricsGate.Stop();
        if (!_lyricsHidden || _playback.IsIntroVisible) return;
        _lyricsHidden = false;
        // Land on the line about to be sung before showing anything.
        Views.LyricsScroller.SnapTo(LyricsViewer, LyricsItems, _playback.CurrentLyricsIndex);
        Views.PlayView.RevealLyrics(LyricsViewer, originX: 0.5, rise: 40);
    }

    /// <summary>Half the view of space above and below the lines, so the first and last can
    /// sit in the middle too.</summary>
    private void LyricsViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var half = LyricsViewer.ActualHeight / 2;
        LyricsItems.Margin = new Thickness(0, half, 0, half);
        Views.LyricsScroller.SnapTo(LyricsViewer, LyricsItems, _playback.CurrentLyricsIndex);
    }

    // --- aurora light ---

    private void StartAurora()
    {
        var (main, second) = PlayerPalette.AuroraColors();
        AuroraLight.Color1 = main;
        AuroraLight.Color2 = second;
        // Endless slow drift, from a random point so it never opens on the same picture.
        var start = Random.Shared.Next(0, 10_000);
        AuroraLight.BeginAnimation(Effects.AuroraEffect.TimeProperty,
            new DoubleAnimation(start, start + 1_000_000, TimeSpan.FromSeconds(1_000_000)));
        AuroraLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.5)));
    }

    /// <summary>A new song: the light slowly takes on its cover's colors.</summary>
    private void RecolorAurora()
    {
        var (main, second) = PlayerPalette.AuroraColors();
        var duration = TimeSpan.FromSeconds(2);
        AuroraLight.BeginAnimation(Effects.AuroraEffect.Color1Property, new ColorAnimation(main, duration));
        AuroraLight.BeginAnimation(Effects.AuroraEffect.Color2Property, new ColorAnimation(second, duration));
    }

    private void AuroraLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0) AuroraLight.Aspect = e.NewSize.Width / e.NewSize.Height;
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
