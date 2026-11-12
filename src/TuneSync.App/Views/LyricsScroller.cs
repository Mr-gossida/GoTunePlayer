using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TuneSync.App.Views;

/// <summary>Glides the synced-lyrics view so the current line sits in the vertical center,
/// following the line frame by frame while it grows. AnimatedOffset holds the resting offset for
/// SnapTo/Reset (ScrollViewer.VerticalOffset itself is read-only).</summary>
internal static class LyricsScroller
{
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(LyricsScroller),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    /// <summary>How long a glide keeps tracking the line before settling (covers its size animation).</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(650);

    /// <summary>How quickly the view catches up with the line (seconds to close ~63% of the gap).</summary>
    private const double CatchUp = 0.12;

    /// <summary>A glide in progress: which line to keep centered, and where the view is now.</summary>
    private sealed class Glide
    {
        public required ItemsControl Items { get; init; }
        public int Index { get; set; }
        public double Position { get; set; }
        public DateTime FollowUntil { get; set; }
    }

    private static readonly Dictionary<ScrollViewer, Glide> Glides = [];
    private static TimeSpan _lastFrame;

    /// <summary>Glides to put line <paramref name="index"/> in the middle. The target is re-measured
    /// every frame instead of fixed up front: the current line grows (and may wrap onto another
    /// line in a narrow window) while the previous one shrinks, so a target taken at the start
    /// ended up off-center and the view jumped at the end.</summary>
    public static void CenterOn(ScrollViewer viewer, ItemsControl items, int index)
    {
        if (index < 0 || index >= items.Items.Count) return;
        if (items.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) return;
        if (!container.IsDescendantOf(viewer)) return;

        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        if (!Glides.TryGetValue(viewer, out var glide))
        {
            glide = new Glide { Items = items, Position = viewer.VerticalOffset };
            Glides[viewer] = glide;
        }
        glide.Index = index;
        // Keep following while the line's size animation runs (0.45-0.5 s), then settle.
        glide.FollowUntil = DateTime.UtcNow + Duration;

        if (Glides.Count == 1)
        {
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // Rendering can fire more than once per frame
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;

        foreach (var (viewer, glide) in Glides.ToList())
        {
            if (Target(viewer, glide.Items, glide.Index) is not { } target)
            {
                Remove(viewer);
                continue;
            }
            var gap = target - glide.Position;
            if (Math.Abs(gap) < 0.5 && DateTime.UtcNow > glide.FollowUntil)
            {
                viewer.ScrollToVerticalOffset(target);
                viewer.SetValue(AnimatedOffsetProperty, target);
                Remove(viewer);
                continue;
            }
            glide.Position += gap * (1 - Math.Exp(-dt / CatchUp));
            viewer.ScrollToVerticalOffset(glide.Position);
        }
    }

    /// <summary>The offset that puts the line's middle in the middle of the view, right now.</summary>
    private static double? Target(ScrollViewer viewer, ItemsControl items, int index)
    {
        if (index < 0 || index >= items.Items.Count
            || items.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container
            || !container.IsDescendantOf(items))
            return null;
        var top = container.TransformToAncestor(items).Transform(new Point(0, 0)).Y + items.Margin.Top;
        return Math.Clamp(top + container.ActualHeight / 2 - viewer.ViewportHeight / 2, 0, viewer.ScrollableHeight);
    }

    private static void Remove(ScrollViewer viewer)
    {
        if (Glides.Remove(viewer) && Glides.Count == 0) CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Cancels a glide in progress (e.g. the user grabbed the wheel, or the track changed).</summary>
    public static void Stop(ScrollViewer viewer)
    {
        Remove(viewer);
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
    }

    /// <summary>Keeps the current line centered with no glide while the view is being resized.
    /// The list is padded by half the viewport above and below, so the offset that centers a line
    /// is simply its own middle within the list - it doesn't depend on the viewport height.</summary>
    public static void SnapTo(ScrollViewer viewer, ItemsControl items, int index)
    {
        Remove(viewer);
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        var target = 0.0;
        if (index >= 0 && index < items.Items.Count
            && items.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container
            && container.IsDescendantOf(items))
        {
            target = container.TransformToAncestor(items).Transform(new Point(0, 0)).Y + container.ActualHeight / 2;
        }
        viewer.SetValue(AnimatedOffsetProperty, target);
        viewer.ScrollToVerticalOffset(target);
    }

    /// <summary>Back to the first line with no glide (a new song). Clears the held end value of
    /// the last glide too, so the next one starts from here instead of jumping back.</summary>
    public static void Reset(ScrollViewer viewer)
    {
        Remove(viewer);
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        viewer.SetValue(AnimatedOffsetProperty, 0.0);
        viewer.ScrollToVerticalOffset(0);
    }
}
