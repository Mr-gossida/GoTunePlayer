using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TuneSync.App.ViewModels;

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

/// <summary>Full-screen "now playing" view shown after a while without input during playback:
/// the cover, the song, the line being sung and a clock over the cover-colored backdrop. Any
/// mouse movement or key brings the player back.</summary>
public partial class ScreenSaverWindow : Window
{
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _drift;
    private readonly PlaybackViewModel _playback;
    private readonly ScreenSaverRole _role;

    public ScreenSaverRole Role => _role;
    private Point? _firstMouse;
    private bool _closing;

    /// <summary>The monitor's exact bounds in device pixels: the window is placed with them once
    /// it has a handle (monitors can have different scaling, so DIPs alone can be off).</summary>
    public Int32Rect? PixelBounds { get; init; }

    public ScreenSaverWindow(PlaybackViewModel playback, Rect screen,
                             ScreenSaverRole role = ScreenSaverRole.Main, bool lyricsElsewhere = false)
    {
        InitializeComponent();
        _playback = playback;
        _role = role;
        ApplyRole(screen, lyricsElsewhere);
        SourceInitialized += (_, _) =>
        {
            if (PixelBounds is not { } px) return;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(-1), px.X, px.Y, px.Width, px.Height, 0x0010); // topmost, no activate
        };
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
            UpdateWall(animate: false);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));
            Hint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.5)) { BeginTime = TimeSpan.FromSeconds(4) });
        };
        Closed += (_, _) =>
        {
            playback.PropertyChanged -= Playback_PropertyChanged;
            PlayerPalette.Changed -= RecolorAurora;
            FrameTicker.Remove(AdvanceAurora);
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
            case nameof(PlaybackViewModel.HasSyncedLyrics):
                UpdateWall(animate: true);
                break;
        }
    }

    // --- roles (several monitors) ---

    private void ApplyRole(Rect screen, bool lyricsElsewhere)
    {
        var collapsed = Visibility.Collapsed;
        switch (_role)
        {
            case ScreenSaverRole.Main:
                if (lyricsElsewhere) LyricsViewer.Visibility = collapsed; // the lyrics screen has them
                break;

            case ScreenSaverRole.Lyrics:
                foreach (var element in new UIElement[] { ClockPanel, CoverCard, TitleText, SubtitleText, SeekPanel, MainSpectrum, Hint })
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
                MainStack.MaxWidth = Math.Max(400, Math.Min(1100, screen.Width / scale - 80));
                AmbientSpectrum.Width = Math.Min(1000, screen.Width - 200);
                break;

            case ScreenSaverRole.Ambient:
                foreach (var element in new UIElement[] { ClockPanel, MainStack, Hint })
                    element.Visibility = collapsed;
                AmbientPanel.Visibility = Visibility.Visible;
                AmbientSpectrum.Width = Math.Min(1000, screen.Width - 200);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

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
        if (_lyricsGate is null) return;
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

    private void StartAurora()
    {
        var (main, second) = PlayerPalette.AuroraColors();
        AuroraLight.Color1 = main;
        AuroraLight.Color2 = second;
        if (!AuroraClock.IsRunning) NewAuroraSession();
        AdvanceAurora(null, EventArgs.Empty);
        FrameTicker.Add(AdvanceAurora);
        AuroraLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.5)));
    }

    private void AdvanceAurora(object? sender, EventArgs e) =>
        AuroraLight.Time = _auroraStart + AuroraClock.Elapsed.TotalSeconds;

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
        AmbientClock.Text = ClockText.Text;
        AmbientDate.Text = DateText.Text;
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
