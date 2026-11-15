using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// How the page adapts to its width: the wide and narrow arrangements, the play list's own width,
// and the transitions between them.
public partial class PlayView
{
    // --- responsive layout ---

    private bool? _compact;

    /// <summary>Adapts to the window width: when the
    /// lyrics no longer have room beside the big cover, the cover glides up into a strip above
    /// the lyrics. Switching back needs a little more room than switching in, so dragging around
    /// the threshold doesn't make it flip back and forth. While the window edge is being dragged,
    /// the layout switch waits until the drag pauses (see <see cref="_layoutSettle"/>), so the
    /// drag itself never stalls on building the other layout.</summary>
    private void ApplyResponsiveLayout(bool resizing = false)
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var playback = _viewModel.Playback;

        // The queue's width doesn't follow the window (the user sets it by dragging its edge):
        // re-laying out its grouped list at a new width cost ~100ms in one frame, which was the
        // stutter felt while resizing.
        var content = width - (playback.IsQueueVisible ? QueuePanel.Width : 0);
        var compact = playback.IsLyricsVisible && (_compact == true ? content < 800 : content < 760);

        // Lyrics hidden: the big cover alone, never wider than the space it has.
        NowPlayingPanel.MaxWidth = Math.Max(200, content - 64);

        if (_compact == compact)
        {
            _layoutSettle?.Stop();
            return;
        }
        if (resizing && _compact is not null)
        {
            // Mid-drag: switch once the window has stayed put for a moment.
            _layoutSettle ??= CreateSettleTimer();
            _layoutSettle.Stop();
            _layoutSettle.Start();
            return;
        }
        _layoutSettle?.Stop();

        var animate = _compact is not null && IsLoaded && NowPlayingGrid.IsVisible;
        _compact = compact;

        // Where the cover is now, before the layout changes.
        var from = compact ? BigCover : SmallCover;
        System.Windows.Rect? fromRect = animate && from.IsVisible ? BoundsInLayer(from) : null;

        NowPlayingGrid.Margin = compact ? new System.Windows.Thickness(24, 16, 24, 16) : new System.Windows.Thickness(48, 24, 32, 24);
        CompactHeader.Visibility = compact ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (compact) NowPlayingPanel.Visibility = System.Windows.Visibility.Collapsed;
        else NowPlayingPanel.ClearValue(VisibilityProperty);

        Grid.SetColumn(LyricsArea, compact ? 0 : 1);
        Grid.SetColumnSpan(LyricsArea, compact ? 2 : 1);
        Grid.SetRow(LyricsArea, compact ? 1 : 0);
        Grid.SetRowSpan(LyricsArea, compact ? 1 : 2);

        if (animate)
        {
            UpdateLayout();
            var to = compact ? SmallCover : BigCover;
            if (fromRect is { } start && to.IsVisible && _viewModel.Playback.CurrentCover is { } cover)
                GlideCover(cover, start, BoundsInLayer(to), to, compact ? 8 : 10);

            // Text and lyrics settle into place with a soft fade and slide. Lyrics only slide
            // (no fade): fading the whole lyrics area means drawing every line into an extra
            // full-size layer.
            FadeSlideIn(compact ? CompactHeader : NowPlayingPanel, compact ? 0 : -16, compact ? -10 : 0);
            FadeSlideIn(LyricsArea, compact ? 0 : 24, compact ? 24 : 0, fade: false);
        }
        ApplyIntroStage(animate: false); // hero card (wide) vs. pulse (narrow) during an intro
        Dispatcher.BeginInvoke(FollowCurrentLyric, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private System.Windows.Threading.DispatcherTimer? _layoutSettle;

    private System.Windows.Threading.DispatcherTimer CreateSettleTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyResponsiveLayout();
        };
        return timer;
    }

    /// <summary>Starts transition animations one frame late. The frame right after a layout
    /// switch is a slow one (WPF builds the new arrangement), so animations started at once lost
    /// their first part to it and visibly jumped; started a frame later they play from the top.</summary>
    private static void OnNextFrame(Action start)
    {
        var frames = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (++frames < 2) return;
            System.Windows.Media.CompositionTarget.Rendering -= handler;
            start();
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;
    }

    private static readonly TimeSpan LayoutTransition = TimeSpan.FromMilliseconds(450);

    // TransformToVisual, not TransformToAncestor: the layer is a sibling of the covers, not a parent.
    private System.Windows.Rect BoundsInLayer(System.Windows.FrameworkElement element) =>
        element.TransformToVisual(TransitionLayer).TransformBounds(new System.Windows.Rect(element.RenderSize));

    /// <summary>Takes away whatever an earlier transition left flying.</summary>
    private void ClearTransitionLayer()
    {
        foreach (var old in TransitionLayer.Children.OfType<System.Windows.FrameworkElement>().ToList())
            TransitionLayer.Children.Remove(old);
    }

    /// <summary>A copy of the cover flies from its old rectangle to its new one (like Android's
    /// shared-element transition) while the real one stays hidden until it lands.</summary>
    private void GlideCover(System.Windows.Media.ImageSource cover, System.Windows.Rect from, System.Windows.Rect to,
                            System.Windows.FrameworkElement target, double cornerRadius, TimeSpan? duration = null)
    {
        var length = duration ?? LayoutTransition;
        ClearTransitionLayer();
        BigCover.Opacity = 1;
        SmallCover.Opacity = 1;

        var flyer = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(cornerRadius),
            Background = new System.Windows.Media.ImageBrush(cover) { Stretch = System.Windows.Media.Stretch.UniformToFill },
            Width = from.Width,
            Height = from.Height,
        };
        Canvas.SetLeft(flyer, from.Left);
        Canvas.SetTop(flyer, from.Top);
        TransitionLayer.Children.Add(flyer);
        target.Opacity = 0;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        System.Windows.Media.Animation.DoubleAnimation Anim(double a, double b) => new(a, b, length) { EasingFunction = ease };
        var move = Anim(from.Left, to.Left);
        move.Completed += (_, _) =>
        {
            if (!TransitionLayer.Children.Contains(flyer)) return; // superseded by a newer switch
            TransitionLayer.Children.Remove(flyer);
            target.Opacity = 1;
        };
        // The flyer waits at its start for the slow layout frame to pass, then moves.
        OnNextFrame(() =>
        {
            flyer.BeginAnimation(Canvas.LeftProperty, move);
            flyer.BeginAnimation(Canvas.TopProperty, Anim(from.Top, to.Top));
            flyer.BeginAnimation(WidthProperty, Anim(from.Width, to.Width));
            flyer.BeginAnimation(HeightProperty, Anim(from.Height, to.Height));
        });
    }

    private static void FadeSlideIn(System.Windows.UIElement element, double fromX, double fromY, bool fade = true,
        TimeSpan? duration = null, TimeSpan? delay = null)
    {
        var length = duration ?? LayoutTransition;
        var begin = delay ?? TimeSpan.Zero;
        // Show the start state right away; the motion starts a frame later (OnNextFrame).
        var shift = new System.Windows.Media.TranslateTransform(fromX, fromY);
        element.RenderTransform = shift;
        if (fade) element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 0, TimeSpan.Zero));
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        OnNextFrame(() =>
        {
            if (fade)
                element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, length)
                {
                    EasingFunction = ease,
                    BeginTime = begin,
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                });
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, length) { EasingFunction = ease, BeginTime = begin });
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(fromY, 0, length) { EasingFunction = ease, BeginTime = begin });
        });
    }

    // ---- the play list's width: drag its edge; it snaps to the standard width ----

    private const double QueueStandardWidth = 320;
    private const double QueueMinWidth = 260;
    private const double QueueSnap = 16;

    /// <summary>As wide as the window allows while the player keeps enough room for its cover
    /// and controls.</summary>
    private double QueueMaxWidth => Math.Max(QueueMinWidth, Math.Min(760, ActualWidth - 460));

    private void QueueResizer_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // From the pointer, not the thumb's own deltas: the thumb moves with the edge it resizes.
        var wanted = ActualWidth - Mouse.GetPosition(this).X;
        var width = Math.Clamp(wanted, QueueMinWidth, QueueMaxWidth);
        var snapped = Math.Abs(width - QueueStandardWidth) <= QueueSnap && QueueStandardWidth <= QueueMaxWidth;
        if (snapped) width = QueueStandardWidth;
        QueueResizer.Tag = snapped ? "snapped" : null;
        if (Math.Abs(QueuePanel.Width - width) < 0.5) return;
        QueuePanel.Width = width;
        ApplyResponsiveLayout(resizing: true);
    }

    private void QueueResizer_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        QueueResizer.Tag = null;
        _viewModel.Playback.QueueWidth = QueuePanel.Width;
        ApplyResponsiveLayout();
        FitNowPlayingPanel();
    }

    private void QueueResizer_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        QueuePanel.Width = Math.Min(QueueStandardWidth, QueueMaxWidth);
        _viewModel.Playback.QueueWidth = QueuePanel.Width;
        ApplyResponsiveLayout();
        FitNowPlayingPanel();
    }

    /// <summary>A smaller window: the play list gives up width before the player does.</summary>
    private void ClampQueueWidth()
    {
        if (ActualWidth <= 0) return;
        var wanted = Math.Max(QueueMinWidth, _viewModel.Playback.QueueWidth);
        QueuePanel.Width = Math.Min(wanted, QueueMaxWidth);
    }
}
