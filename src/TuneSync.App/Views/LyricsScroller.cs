using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace TuneSync.App.Views;

/// <summary>Glides the synced-lyrics view so the current line sits in the vertical center.
/// ScrollViewer.VerticalOffset can't be animated directly (it's read-only), so an attached
/// property is animated instead and forwards each frame to ScrollToVerticalOffset.</summary>
internal static class LyricsScroller
{
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(LyricsScroller),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(650);

    public static void CenterOn(ScrollViewer viewer, ItemsControl items, int index)
    {
        if (index < 0 || index >= items.Items.Count) return;
        if (items.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) return;
        if (!container.IsDescendantOf(viewer)) return;
        viewer.UpdateLayout(); // measure against the current lines, not a stale layout

        var top = container.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        var target = viewer.VerticalOffset + top + container.ActualHeight / 2 - viewer.ViewportHeight / 2;
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);

        var animation = new DoubleAnimation(viewer.VerticalOffset, target, Duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        viewer.BeginAnimation(AnimatedOffsetProperty, animation);
    }

    /// <summary>Cancels a glide in progress (e.g. the user grabbed the wheel, or the track changed).</summary>
    public static void Stop(ScrollViewer viewer) => viewer.BeginAnimation(AnimatedOffsetProperty, null);

    /// <summary>Back to the first line with no glide (a new song). Clears the held end value of
    /// the last glide too, so the next one starts from here instead of jumping back.</summary>
    public static void Reset(ScrollViewer viewer)
    {
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        viewer.SetValue(AnimatedOffsetProperty, 0.0);
        viewer.ScrollToVerticalOffset(0);
    }
}
