using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// A song's instrumental opening: the cover card takes the middle of the screen (or, in a narrow
// window, a pulse stands in for the lyrics), a bar shows how long is left, and the card glides back
// to its place as the lyrics come in.
public partial class PlayView
{
    // --- intro stage: hero card during the opening, then a dramatic hand-off to the lyrics ---

    private bool _hero;
    private bool _lyricsHeldBack;
    private bool _trackChanging;

    /// <summary>During a long instrumental opening the cover card moves to the middle of the
    /// screen (wide layout) or a big equalizer pulse stands in for the lyrics (narrow layout).
    /// When the singing is about to start, the cover glides back to its place and the lyrics rise
    /// in out of a blur.</summary>
    private void ApplyIntroStage(bool animate)
    {
        var playback = _viewModel.Playback;
        var intro = playback.IsIntroVisible;
        // With the equalizer in the lyrics' place there's no intro to stage: the card stays on
        // the left with the song's details.
        var hero = intro && playback.IsLyricsVisible && _compact != true && !IsEqualizerOpen;
        animate = animate && IsLoaded && !_trackChanging;
        // Only while an intro is on (it holds looping animations) - and, after it, until its
        // fade-out (the style's exit animation) has played instead of vanishing at once.
        var pulse = _compact == true && intro;
        if (pulse) IntroPulse.Visibility = System.Windows.Visibility.Visible;
        else if (animate && IntroPulse.IsVisible) CollapseAfter(IntroPulse, TimeSpan.FromMilliseconds(550), () => _viewModel.Playback.IsIntroVisible);
        else IntroPulse.Visibility = System.Windows.Visibility.Collapsed;

        // Measured on the cover, which sits at the card's top-left corner: the card's width and
        // its cover's size needn't match (a short window gets a smaller cover), and the glide
        // has to scale the card by how the cover changes.
        System.Windows.Rect? before = animate && _hero && !hero && NowPlayingPanel.IsVisible ? BoundsInLayer(BigCover) : null;
        var spectrumBefore = PanelSpectrum.ActualWidth;
        ShowHeroGauge(intro && !IsEqualizerOpen && playback.ShowIntroGauge, animate);

        if (hero != _hero)
        {
            StopCardGlide();
            var from = before;
            _hero = hero;
            if (hero)
            {
                Grid.SetColumnSpan(NowPlayingPanel, 2);
                NowPlayingPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                NowPlayingPanel.Margin = new System.Windows.Thickness(0);
                FitNowPlayingPanel();
            }
            else
            {
                NowPlayingPanel.ClearValue(Grid.ColumnSpanProperty);
                NowPlayingPanel.ClearValue(HorizontalAlignmentProperty);
                NowPlayingPanel.ClearValue(MarginProperty);
                FitNowPlayingPanel();
                if (from is { } start)
                {
                    UpdateLayout();
                    // Cover, song info and spectrum travel together, as one card. Unhurried: the
                    // intro ends a few seconds before the singing, so there's time.
                    GlideCard(start, spectrumBefore, TimeSpan.FromMilliseconds(1400));
                }
            }
        }

        if (intro != _lyricsHeldBack)
        {
            _lyricsHeldBack = intro;
            if (intro)
            {
                LyricsScrollViewer.BeginAnimation(OpacityProperty, null);
                LyricsScrollViewer.Opacity = 0;
            }
            else if (animate)
            {
                RevealLyrics(LyricsScrollViewer, delay: TimeSpan.FromMilliseconds(450));
            }
            else
            {
                LyricsScrollViewer.BeginAnimation(OpacityProperty, null);
                LyricsScrollViewer.Opacity = 1;
            }
        }
    }

    /// <summary>The intro card's progress bar: shown at once, faded out when the intro ends (it
    /// fills up first, so it ends complete rather than cut off). It takes no room in the card, so
    /// this never moves the card.</summary>
    private void ShowHeroGauge(bool show, bool animate)
    {
        if (show)
        {
            if (!_gaugeRetiring)
            {
                HeroGauge.BeginAnimation(OpacityProperty, null);
                HeroGauge.Opacity = 1;
            }
            HeroGauge.Visibility = System.Windows.Visibility.Visible;
            return;
        }
        if (!animate || !HeroGauge.IsVisible)
        {
            HeroGauge.BeginAnimation(OpacityProperty, null);
            HeroGauge.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(700))
        {
            BeginTime = TimeSpan.FromMilliseconds(150),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        fade.Completed += (_, _) =>
        {
            if (_viewModel.Playback.IsIntroVisible && !IsEqualizerOpen) return; // a new intro began meanwhile
            HeroGauge.BeginAnimation(OpacityProperty, null);
            HeroGauge.Visibility = System.Windows.Visibility.Collapsed;
        };
        HeroGauge.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Collapses an element once its own fade-out has had time to play, unless it is
    /// wanted again by then.</summary>
    private void CollapseAfter(System.Windows.UIElement element, TimeSpan delay, Func<bool> stillWanted)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!stillWanted()) element.Visibility = System.Windows.Visibility.Collapsed;
        };
        timer.Start();
    }

    /// <summary>The lyrics' entrance after the intro: they rise from below, grow to full size and
    /// come into focus out of a heavy blur, all at once.</summary>
    internal static void RevealLyrics(System.Windows.FrameworkElement element, TimeSpan? delay = null, double originX = 0, double rise = 70)
    {
        var duration = TimeSpan.FromMilliseconds(1300);
        var begin = delay ?? TimeSpan.Zero;
        var ease = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var blur = new System.Windows.Media.Effects.BlurEffect
        {
            Radius = 30,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
        };
        var scale = new System.Windows.Media.ScaleTransform(0.88, 0.88);
        var shift = new System.Windows.Media.TranslateTransform(0, rise);
        element.Effect = blur;
        element.RenderTransformOrigin = new System.Windows.Point(originX, 0.5);
        element.RenderTransform = new System.Windows.Media.TransformGroup { Children = { scale, shift } };

        System.Windows.Media.Animation.DoubleAnimation Anim(double from, double to, TimeSpan length) =>
            new(from, to, length) { EasingFunction = ease, BeginTime = begin };
        // Invisible until the (possibly delayed) fade starts; the fade then holds full opacity.
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800))
        {
            BeginTime = begin,
        });
        var focus = Anim(30, 0, duration);
        focus.Completed += (_, _) =>
        {
            if (element.Effect == blur) element.Effect = null; // no effect left running afterwards
        };
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, focus);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, Anim(0.88, 1, duration));
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, Anim(0.88, 1, duration));
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, Anim(rise, 0, duration));
    }

    /// <summary>The intro's "lyrics start soon" bar. Position updates come every 200 ms, so binding
    /// the bar to them made it step; instead each update starts a linear glide from where the bar
    /// is now to the end, timed to the intro's remaining length - which keeps it moving every frame
    /// and still corrects itself against the real position.</summary>
    private void GlideIntroGauge()
    {
        // Two bars show the intro: under the lyrics-area equalizer, and on the hero card.
        GlideGauge(IntroGaugeScale);
        GlideGauge(HeroGaugeScale);
        RetireGaugesNearTheEnd();
    }

    private bool _gaugeRetiring;
    private static readonly TimeSpan GaugeRetireTime = TimeSpan.FromMilliseconds(800);

    /// <summary>The bars dissolve where they are over the intro's last moments, finishing just as
    /// they fill - so they are gone before the card and lyrics start moving, instead of being
    /// carried off mid-fade. Seeking back brings them back.</summary>
    private void RetireGaugesNearTheEnd()
    {
        var playback = _viewModel.Playback;
        if (!playback.IsIntroVisible || playback.IntroLength <= TimeSpan.Zero) return;
        var remaining = TimeSpan.FromSeconds((1 - playback.IntroProgress) * playback.IntroLength.TotalSeconds);
        var near = playback.IsPlaying && remaining <= GaugeRetireTime;
        if (near == _gaugeRetiring) return;
        _gaugeRetiring = near;
        if (!near)
        {
            ShowGaugesAgain(); // sought back, or paused
            return;
        }
        foreach (var gauge in new System.Windows.UIElement[] { HeroGauge, PulseGauge })
            gauge.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, remaining)
            {
                EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
            });
    }

    /// <summary>Both intro bars back at full strength, whatever fade was running on them.</summary>
    private void ShowGaugesAgain()
    {
        foreach (var gauge in new System.Windows.UIElement[] { HeroGauge, PulseGauge })
        {
            gauge.BeginAnimation(OpacityProperty, null);
            gauge.Opacity = 1;
        }
    }

    private void GlideGauge(System.Windows.Media.ScaleTransform gauge)
    {
        var playback = _viewModel.Playback;
        var shown = gauge.ScaleX;
        var target = playback.IntroProgress;
        if (!playback.IsIntroVisible)
        {
            // The intro is over: fill up (it may be a hair short) and hold there while it fades
            // out, rather than snapping back to 0 mid-fade.
            gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(shown, 1, TimeSpan.FromMilliseconds(250)));
            return;
        }
        if (!playback.IsPlaying || playback.IntroLength <= TimeSpan.Zero)
        {
            gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            gauge.ScaleX = target;
            return;
        }
        // A new song or a seek: start from the real position instead of gliding across.
        var from = Math.Abs(shown - target) > 0.05 ? target : shown;
        var remaining = (1 - target) * playback.IntroLength.TotalSeconds;
        if (remaining <= 0.01)
        {
            gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            gauge.ScaleX = 1;
            return;
        }
        gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new System.Windows.Media.Animation.DoubleAnimation(from, 1, TimeSpan.FromSeconds(remaining)));
    }

    private EventHandler? _cardGlide;
    private System.Windows.Media.TranslateTransform? _cardShift;
    private System.Windows.Thickness _spectrumMargin;

    /// <summary>Takes the card from the intro's centre to its place beside the lyrics. Only the
    /// cover changes size; the title, details and spectrum keep their real size and stay right
    /// under the cover all the way. (Scaling the whole card made them grow at the start, so the
    /// spectrum jumped down and back.) Each frame lays the card out with the cover's current
    /// size, then shifts it so the cover is where the motion has got to.</summary>
    private void GlideCard(System.Windows.Rect from, double spectrumFrom, TimeSpan length)
    {
        ClearTransitionLayer();
        BigCover.Opacity = 1;
        NowPlayingPanel.BeginAnimation(OpacityProperty, null);
        NowPlayingPanel.Opacity = 1;
        var to = BoundsInLayer(BigCover);
        if (to.Width <= 0 || from.Width <= 0) return;

        var spectrumTo = PanelSpectrum.ActualWidth;
        _spectrumMargin = PanelSpectrum.Margin;
        PanelSpectrum.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var shift = new System.Windows.Media.TranslateTransform();
        _cardShift = shift;
        NowPlayingPanel.RenderTransformOrigin = new System.Windows.Point(0, 0);
        NowPlayingPanel.RenderTransform = shift;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };

        void Place(double e)
        {
            var size = from.Width + (to.Width - from.Width) * e;
            BigCover.Width = size;
            BigCover.Height = size;
            // Wider than the card for a moment: let it stick out on the right instead of being clipped.
            BigCover.Margin = new System.Windows.Thickness(0, 0, Math.Min(0, NowPlayingPanel.ActualWidth - size), 0);
            // The spectrum narrows with the card too, rather than snapping to the new width.
            if (PanelSpectrum.IsVisible && spectrumFrom > 0 && spectrumTo > 0)
            {
                var width = spectrumFrom + (spectrumTo - spectrumFrom) * e;
                PanelSpectrum.Width = width;
                PanelSpectrum.Margin = new System.Windows.Thickness(_spectrumMargin.Left, _spectrumMargin.Top,
                    Math.Min(0, NowPlayingPanel.ActualWidth - width), _spectrumMargin.Bottom);
            }
            NowPlayingPanel.UpdateLayout();
            var at = BigCover.TranslatePoint(new System.Windows.Point(0, 0), TransitionLayer);
            shift.X += from.Left + (to.Left - from.Left) * e - at.X;
            shift.Y += from.Top + (to.Top - from.Top) * e - at.Y;
        }

        Place(0);
        System.Diagnostics.Stopwatch? clock = null; // starts on the first frame, after the slow layout one
        _cardGlide = (_, _) =>
        {
            if (NowPlayingPanel.RenderTransform != shift || _hero)
            {
                StopCardGlide();
                return;
            }
            clock ??= System.Diagnostics.Stopwatch.StartNew();
            var p = clock.Elapsed / length;
            if (p >= 1) StopCardGlide();
            else Place(ease.Ease(p));
        };
        System.Windows.Media.CompositionTarget.Rendering += _cardGlide;
    }

    /// <summary>Ends a card glide (finished or interrupted) and leaves the card at its real layout.</summary>
    private void StopCardGlide()
    {
        if (_cardGlide is null) return;
        System.Windows.Media.CompositionTarget.Rendering -= _cardGlide;
        _cardGlide = null;
        BigCover.ClearValue(MarginProperty);
        PanelSpectrum.ClearValue(WidthProperty);
        PanelSpectrum.ClearValue(HorizontalAlignmentProperty);
        PanelSpectrum.Margin = _spectrumMargin;
        if (NowPlayingPanel.RenderTransform == _cardShift)
            NowPlayingPanel.RenderTransform = System.Windows.Media.Transform.Identity;
        _cardShift = null;
        FitNowPlayingPanel();
    }
}
