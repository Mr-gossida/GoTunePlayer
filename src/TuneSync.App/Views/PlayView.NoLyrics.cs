using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>A song without lyrics: the "가사 찾기" notice is shown as the song starts, then gives
/// way to something to look at - a big spectrum, the song and what plays next. The notice comes
/// back while the pointer is over the lyrics area, and stays while a search is under way or its
/// result waits to be saved.</summary>
public partial class PlayView
{
    /// <summary>How long the notice stays before the spectrum takes its place.</summary>
    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(6);

    /// <summary>How long the notice lingers after the pointer leaves, so passing over the area
    /// doesn't make it blink.</summary>
    private static readonly TimeSpan LeaveGrace = TimeSpan.FromSeconds(1.2);

    private readonly DispatcherTimer _noticeTimer = new() { Interval = NoticeTime };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = LeaveGrace };
    private bool _noticeHadItsTime;
    private bool _pointerOverLyrics;
    private bool? _ambientShown;

    private void HookNoLyrics()
    {
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _noticeHadItsTime = true;
            UpdateNoLyricsStage();
        };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            _pointerOverLyrics = false;
            UpdateNoLyricsStage();
        };
        _viewModel.Playback.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PlaybackViewModel.CurrentFile):
                case nameof(PlaybackViewModel.HasNoLyrics):
                    RestartNoLyricsNotice();
                    break;
                case nameof(PlaybackViewModel.SpectrumLook):
                    UpdateAmbientLook();
                    break;
                case nameof(PlaybackViewModel.IsShuffle):
                case nameof(PlaybackViewModel.Repeat):
                case nameof(PlaybackViewModel.HasUpNext):
                    if (_ambientShown == true) UpdateAmbientNext();
                    break;
            }
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayPageViewModel.FindState)) UpdateNoLyricsStage();
        };
        Loaded += (_, _) => RestartNoLyricsNotice();
    }

    /// <summary>A new song, or its lyrics turned out missing (or were just saved): the notice
    /// gets its time again.</summary>
    private void RestartNoLyricsNotice()
    {
        _noticeTimer.Stop();
        _noticeHadItsTime = false;
        if (_viewModel.Playback.HasNoLyrics) _noticeTimer.Start();
        UpdateNoLyricsStage();
    }

    private void LyricsArea_MouseChanged(object sender, MouseEventArgs e)
    {
        if (e.RoutedEvent == MouseEnterEvent)
        {
            _leaveTimer.Stop();
            _pointerOverLyrics = true;
            UpdateNoLyricsStage();
        }
        else
        {
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }
    }

    private void UpdateNoLyricsStage()
    {
        var ambient = _viewModel.Playback.HasNoLyrics
                      && _noticeHadItsTime
                      && !_pointerOverLyrics
                      && _viewModel.FindState == LyricsFindState.Idle;
        if (ambient == _ambientShown) return;
        var first = _ambientShown is null;
        _ambientShown = ambient;
        if (ambient) UpdateAmbientNext();

        // The card's visibility follows HasNoLyrics (its binding); here only whether it shows.
        NoLyricsCard.IsHitTestVisible = !ambient;
        Fade(NoLyricsCard, ambient ? 0 : 1, first ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ambient ? 300 : 220));

        // The spectrum under the song info moves to the middle: hidden there meanwhile (Hidden,
        // not Collapsed, so the song info keeps its place; hidden, it stops drawing too).
        if (ambient) UpdateAmbientLook();
        // Back: the panel's fitting decides whether it has room for its spectrum.
        if (!ambient && !first) FitNowPlayingPanel();
        foreach (var small in new[] { PanelSpectrum, HeaderSpectrum })
        {
            if (!ambient && small == HeaderSpectrum) small.Visibility = Visibility.Visible;
            var element = small;
            Fade(small, ambient ? 0 : 1, first ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ambient ? 350 : 450),
                completed: ambient ? () => { if (_ambientShown == true) element.Visibility = Visibility.Hidden; } : null);
        }

        if (ambient) NoLyricsAmbient.Visibility = Visibility.Visible;
        // Collapsed once faded out, so the spectrum stops drawing (SpectrumView pauses when hidden).
        Fade(NoLyricsAmbient, ambient ? 1 : 0,
            first ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ambient ? 700 : 220), delay: ambient ? 200 : 0,
            completed: ambient ? null : () =>
            {
                if (_ambientShown != true) NoLyricsAmbient.Visibility = Visibility.Collapsed;
            });
    }

    private static void Fade(UIElement element, double to, TimeSpan duration, int delay = 0, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        // before BeginAnimation: the clock is made from the animation then, handlers and all
        if (completed is not null) animation.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>The big spectrum looks like the small one it stands in for; with the spectrum
    /// switched off there, mirrored bars - the middle would be empty otherwise.</summary>
    private void UpdateAmbientLook()
    {
        var look = _viewModel.Playback.SpectrumLook;
        AmbientSpectrum.Look = look is Core.Models.SpectrumStyle.Off or Core.Models.SpectrumStyle.Random
            ? Core.Models.SpectrumStyle.Mirror
            : look;
    }

    /// <summary>"다음 곡": only when it's already known (not under shuffle, which picks it when
    /// this song ends).</summary>
    private void UpdateAmbientNext()
    {
        if (_viewModel.Playback.PeekNext() is { } next)
        {
            AmbientNextText.Text = string.IsNullOrWhiteSpace(next.Artist) ? next.DisplayTitle : $"{next.DisplayTitle} — {next.Artist}";
            AmbientNext.Visibility = Visibility.Visible;
        }
        else
        {
            AmbientNext.Visibility = Visibility.Collapsed;
        }
    }
}
