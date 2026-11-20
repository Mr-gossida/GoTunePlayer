using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App;

/// <summary>What a screen-saver window shows. With one monitor it's just <see cref="Main"/>;
/// with more, the next monitor over shows the lyrics large and any others a clock and spectrum,
/// all under one aurora that flows across the screens.</summary>
public enum ScreenSaverRole
{
    /// <summary>Cover, song, seek bar, spectrum, clock (and the lyrics, if no other screen has them).</summary>
    Main,
    /// <summary>The lyrics, large; a clock and spectrum while there are none to show.</summary>
    Lyrics,
    /// <summary>A big clock over a wide spectrum.</summary>
    Ambient,
}

/// <summary>The "now playing" picture shared by the screen saver (<see cref="ScreenSaverWindow"/>,
/// display only) and focus mode in the player window (<see cref="Interactive"/>: the seek bar can
/// be dragged and a lyric line clicked to play from there). It starts its timers and the aurora
/// when it's shown and stops them when it's taken away.</summary>
public partial class NowPlayingStage : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _drift = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly PlaybackViewModel _playback;
    private readonly ScreenSaverRole _role;
    private readonly bool _interactive;

    public ScreenSaverRole Role => _role;

    /// <param name="width">The screen's width, for the lyrics screen's line width.</param>
    /// <param name="interactive">Focus mode: seek bar and lyric lines answer to the mouse, and
    /// nothing drifts (burn-in protection is the screen saver's job).</param>
    public NowPlayingStage(PlaybackViewModel playback, ScreenSaverRole role = ScreenSaverRole.Main,
                           bool lyricsElsewhere = false, double width = 1920, bool interactive = false)
    {
        InitializeComponent();
        _playback = playback;
        _role = role;
        _interactive = interactive;
        DataContext = playback;
        ApplyRole(width, lyricsElsewhere);

        if (interactive)
        {
            SeekPanel.IsHitTestVisible = true;
            StageSeekBar.SeekStarted += (_, _) => _playback.BeginSeek();
            StageSeekBar.SeekCompleted += (_, seconds) => _playback.EndSeek(seconds);
            LyricsViewer.IsHitTestVisible = true;
            // In a window the taskbar clock is right there; and the song keeps clear of the
            // controls that show along the bottom.
            ClockPanel.Visibility = Visibility.Collapsed;
            MainStack.Margin = new Thickness(0, 0, 0, 84);
        }

        _auroraCache = AuroraLayer.CacheMode;

        // The column changes height too, not just the window: the lyrics appear or go with the
        // song (no synced lyrics -> none), and a long title wraps. Fit again after each change.
        MainStack.SizeChanged += (_, _) => Dispatcher.BeginInvoke(FitToSize, DispatcherPriority.Loaded);

        _clock.Tick += (_, _) => UpdateClock();
        _drift.Tick += (_, _) => DriftSomewhere();
        _lyricsGate.Tick += LyricsGate_Tick;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    public bool Interactive => _interactive;

    private bool _running;

    private void Start()
    {
        if (_running) return;
        _running = true;
        _playback.PropertyChanged += Playback_PropertyChanged;
        PlayerPalette.Changed += RecolorAurora;
        UpdateClock();
        StartAurora();
        // Open on the line being sung, without a glide (once the lines are laid out).
        Dispatcher.BeginInvoke(() => Views.LyricsScroller.SnapTo(LyricsViewer, LyricsItems, _playback.CurrentLyricsIndex),
            DispatcherPriority.Loaded);
        _clock.Start();
        if (!_interactive) _drift.Start();
        _lyricsHidden = false;
        if (_playback.IsIntroVisible) HideLyrics(instant: true);
        UpdateWall(animate: false);
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        _playback.PropertyChanged -= Playback_PropertyChanged;
        PlayerPalette.Changed -= RecolorAurora;
        StopAuroraSteps();
        Views.LyricsScroller.Stop(LyricsViewer);
        _lyricsGate.Stop();
        _clock.Stop();
        _drift.Stop();
    }

    /// <summary>The cover, for transitions that fly it in from elsewhere.</summary>
    public FrameworkElement Cover => CoverCard;

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
            case nameof(PlaybackViewModel.HasSyncedLyrics):
                UpdateWall(animate: true);
                break;
            case nameof(PlaybackViewModel.AuroraMode):
                ApplyAuroraMode();
                break;
            case nameof(PlaybackViewModel.BackgroundEffect):
                ApplyLook();
                break;
        }
    }

    /// <summary>Focus mode: a click on a lyric line plays from it.</summary>
    private void LyricLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive || sender is not FrameworkElement { DataContext: LyricsLineViewModel line }) return;
        _playback.SeekTo(line.Time);
        e.Handled = true;
    }

    /// <summary>The song's column shrinks to fit a window too short for it (focus mode in a small
    /// player window, or a small screen), and never grows past its designed size.</summary>
    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => FitToSize();

    private void FitToSize()
    {
        if (ActualHeight <= 0 || MainStack.Visibility != Visibility.Visible) return;
        var scale = StackScale.ScaleX;
        MainStack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = MainStack.DesiredSize.Height / Math.Max(0.01, scale);
        var naturalWidth = MainStack.DesiredSize.Width / Math.Max(0.01, scale);
        if (natural <= 0) return;
        var fit = Math.Min(1, Math.Min((ActualHeight - 48) / natural, (ActualWidth - 48) / Math.Max(1, naturalWidth)));
        fit = Math.Max(0.4, fit);
        if (Math.Abs(fit - scale) < 0.01) return;
        StackScale.ScaleX = fit;
        StackScale.ScaleY = fit;
    }

    // --- roles (several monitors) ---

    private void ApplyRole(double width, bool lyricsElsewhere)
    {
        var collapsed = Visibility.Collapsed;
        switch (_role)
        {
            case ScreenSaverRole.Main:
                if (lyricsElsewhere) LyricsViewer.Visibility = collapsed; // the lyrics screen has them
                break;

            case ScreenSaverRole.Lyrics:
                foreach (var element in new UIElement[] { ClockPanel, CoverCard, TitleText, SubtitleText, SeekPanel, MainSpectrum })
                    element.Visibility = collapsed;
                WallFooter.Visibility = Visibility.Visible;
                // The same lines as on a single screen, half as big again, and more of them.
                const double scale = 1.5, height = 340;
                LyricsViewer.LayoutTransform = new ScaleTransform(scale, scale);
                LyricsViewer.Height = height;
                LyricsViewer.Margin = new Thickness(0);
                var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, height) };
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                mask.GradientStops.Add(new GradientStop(Colors.Black, 0.18));
                mask.GradientStops.Add(new GradientStop(Colors.Black, 0.82));
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                LyricsViewer.OpacityMask = mask;
                MainStack.MaxWidth = Math.Max(400, Math.Min(1100, width / scale - 80));
                AmbientSpectrum.Width = Math.Min(1000, width - 200);
                break;

            case ScreenSaverRole.Ambient:
                foreach (var element in new UIElement[] { ClockPanel, MainStack })
                    element.Visibility = collapsed;
                AmbientPanel.Visibility = Visibility.Visible;
                AmbientSpectrum.Width = Math.Min(1000, width - 200);
                break;
        }
    }

    /// <summary>The lyrics screen: the lines while there are any to show (not during an intro),
    /// otherwise the clock and spectrum, cross-faded.</summary>
    private void UpdateWall(bool animate)
    {
        if (_role != ScreenSaverRole.Lyrics) return;
        var lyrics = _playback.HasSyncedLyrics && !_lyricsHidden;
        void Fade(UIElement element, bool show)
        {
            if (show) element.Visibility = Visibility.Visible;
            var to = show ? 1.0 : 0.0;
            if (!animate)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = to;
                if (!show) element.Visibility = Visibility.Collapsed;
                return;
            }
            var fade = new DoubleAnimation(to, TimeSpan.FromMilliseconds(700));
            if (!show) fade.Completed += (_, _) => { if (element.Opacity == 0) element.Visibility = Visibility.Collapsed; };
            element.BeginAnimation(OpacityProperty, fade);
        }
        Fade(AmbientPanel, !lyrics);
        Fade(WallFooter, lyrics);
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
        UpdateWall(animate: !instant);
        LyricsViewer.Effect = null;
        LyricsViewer.BeginAnimation(OpacityProperty, instant ? null
            : new DoubleAnimation(LyricsViewer.Opacity, 0, TimeSpan.FromMilliseconds(500)));
        if (instant) LyricsViewer.Opacity = 0;
    }

    private void ScheduleLyricsReveal()
    {
        _lyricsGate.Stop();
        if (!_playback.IsIntroVisible) _lyricsGate.Start();
    }

    private void LyricsGate_Tick(object? sender, EventArgs e)
    {
        _lyricsGate.Stop();
        if (!_lyricsHidden || _playback.IsIntroVisible) return;
        _lyricsHidden = false;
        UpdateWall(animate: true);
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

    // One clock for every screen's light, so the bands line up across the monitors. Each
    // screen saver starts at a random point so it never opens on the same picture.
    private static readonly System.Diagnostics.Stopwatch AuroraClock = new();
    private static double _auroraStart;

    /// <summary>Called once per screen saver, before its windows open.</summary>
    public static void NewAuroraSession()
    {
        _auroraStart = Random.Shared.Next(0, 10_000);
        AuroraClock.Restart();
    }

    /// <summary>This screen's part of the light shared by all monitors (see Aurora.hlsl).</summary>
    public void SetAuroraRegion(Point offset, Size span)
    {
        AuroraLight.Offset = offset;
        AuroraLight.Span = span;
    }

    /// <summary>The background effect chosen (for 임의, one picked when the stage opens).</summary>
    private void ApplyLook()
    {
        var look = _playback.BackgroundEffect;
        if (look == BackgroundEffect.Random)
        {
            BackgroundEffect[] choices = [BackgroundEffect.Aurora, BackgroundEffect.Caustics, BackgroundEffect.Bokeh, BackgroundEffect.Nebula, BackgroundEffect.Lava, BackgroundEffect.CoverGradient];
            look = choices[Random.Shared.Next(choices.Length)];
        }
        AuroraLight.Look = look;
    }

    private void StartAurora()
    {
        ApplyLook();
        var (main, second) = PlayerPalette.AuroraColors();
        _seekBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _seekBrush.Color = main;
        StageSeekBar.WaveBrush = _seekBrush;
        AuroraLight.Color1 = main;
        AuroraLight.Color2 = second;
        if (!AuroraClock.IsRunning) NewAuroraSession();
        ShowAuroraNow();
        ApplyAuroraMode();
        // The light fades in over the backdrop (the light, not the layer: the backdrop is in it).
        AuroraLight.BeginAnimation(Effects.AuroraEffect.IntensityProperty, AuroraTimeline(new DoubleAnimation(0, 0.7, TimeSpan.FromSeconds(2.5))));
    }

    // The aurora needs pixel shader 3.0 (any GPU from the last ~15 years).
    private static readonly bool AuroraSupported = RenderCapability.IsPixelShaderVersionSupported(3, 0);
    private readonly CacheMode? _auroraCache;
    private bool _auroraStepping;
    private int _auroraEvery = 2;

    /// <summary>설정 › 배경 효과 (or no shader support): the light on, stepping less often, or
    /// off - then the plain backdrop is shown full size, and with nothing changing it, it is only
    /// redrawn where something over it changes.</summary>
    private void ApplyAuroraMode()
    {
        if (!_running) return;
        var mode = AuroraSupported ? _playback.AuroraMode : AuroraMode.Off;
        if (mode == AuroraMode.Off)
        {
            AuroraLayer.Effect = null;
            AuroraLayer.CacheMode = null;
            StopAuroraSteps();
            return;
        }
        AuroraLayer.Effect = AuroraLight;
        AuroraLayer.CacheMode = _auroraCache;
        _auroraEvery = mode == AuroraMode.Saver ? 4 : 2;
        if (!_auroraStepping)
        {
            _auroraStepping = true;
            ShowAuroraNow();
            FrameTicker.Add(AdvanceAurora);
        }
    }

    private void StopAuroraSteps()
    {
        if (!_auroraStepping) return;
        _auroraStepping = false;
        FrameTicker.Remove(AdvanceAurora);
    }

    /// <summary>Every change of the light redraws the whole screen: no more often than the light
    /// itself moves (WPF would otherwise animate it at the monitor's full refresh rate).</summary>
    private static T AuroraTimeline<T>(T timeline) where T : Timeline
    {
        Timeline.SetDesiredFrameRate(timeline, 15);
        return timeline;
    }

    private int _auroraTick;

    /// <summary>15 times a second (every other tick of the shared ~30 fps clock; 절전: every
    /// fourth): the light drifts too slowly for more to show, and each update redraws the whole
    /// screen. (The ticker's sender is always null; skipping ticks only for a non-null sender
    /// meant the light really moved at the clock's full ~30 a second.)</summary>
    private void AdvanceAurora(object? sender, EventArgs e)
    {
        if (++_auroraTick % _auroraEvery != 0) return;
        ShowAuroraNow();
    }

    private void ShowAuroraNow() => AuroraLight.Time = _auroraStart + AuroraClock.Elapsed.TotalSeconds;

    /// <summary>A new song: the light slowly takes on its cover's colors.</summary>
    /// <summary>The seek bar takes the stage's own cover color (light and vivid on the dark
    /// stage, whatever the app's theme), like the player's bar takes the player's.</summary>
    private readonly SolidColorBrush _seekBrush = new(Colors.White);

    private void RecolorAurora()
    {
        var (main, second) = PlayerPalette.AuroraColors();
        var duration = TimeSpan.FromSeconds(2);
        _seekBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(main, duration));
        AuroraLight.BeginAnimation(Effects.AuroraEffect.Color1Property, AuroraTimeline(new ColorAnimation(main, duration)));
        AuroraLight.BeginAnimation(Effects.AuroraEffect.Color2Property, AuroraTimeline(new ColorAnimation(second, duration)));
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
        AmbientClock.Text = ClockText.Text;
        AmbientDate.Text = DateText.Text;
    }

    /// <summary>A slow glide to a nearby spot (burn-in protection on OLED and plasma screens).</summary>
    private void DriftSomewhere()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromSeconds(10);
        Drift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(Random.Shared.Next(-90, 91), duration) { EasingFunction = ease });
        Drift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Random.Shared.Next(-50, 51), duration) { EasingFunction = ease });
    }
}
