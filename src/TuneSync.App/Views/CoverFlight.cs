using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TuneSync.App.Views;

/// <summary>A copy of the cover flying from one rectangle to another over a canvas (like
/// Android's shared-element transition): the play page's layout switches and 음악 집중 모드's
/// entry and exit use it while the real cover stays hidden until the copy lands.</summary>
internal static class CoverFlight
{
    /// <param name="picture">What the copy shows (the cover, or a placeholder fill).</param>
    /// <param name="landed">Runs when the copy arrives; its argument is true when the copy was
    /// taken off the canvas meanwhile by a newer flight (superseded).</param>
    /// <param name="startLater">Delays the motion (the copy waits at its start), e.g. a frame.</param>
    public static void Fly(Canvas layer, Brush picture, Rect from, Rect to, double corner, TimeSpan length,
        Action<bool> landed, Action<Action>? startLater = null)
    {
        var flyer = new Border
        {
            CornerRadius = new CornerRadius(corner),
            Background = picture,
            Width = from.Width,
            Height = from.Height,
        };
        Canvas.SetLeft(flyer, from.Left);
        Canvas.SetTop(flyer, from.Top);
        layer.Children.Add(flyer);

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Anim(double a, double b) => new(a, b, length) { EasingFunction = ease };
        var move = Anim(from.Left, to.Left);
        move.Completed += (_, _) =>
        {
            var superseded = !layer.Children.Contains(flyer);
            layer.Children.Remove(flyer);
            landed(superseded);
        };
        void Start()
        {
            flyer.BeginAnimation(Canvas.LeftProperty, move);
            flyer.BeginAnimation(Canvas.TopProperty, Anim(from.Top, to.Top));
            flyer.BeginAnimation(FrameworkElement.WidthProperty, Anim(from.Width, to.Width));
            flyer.BeginAnimation(FrameworkElement.HeightProperty, Anim(from.Height, to.Height));
        }
        if (startLater is null) Start();
        else startLater(Start);
    }
}
