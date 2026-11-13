using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TuneSync.App.Views;

/// <summary>The little bouncing "now playing" bars shown on the playing song's thumbnail. They
/// only move while music is actually playing and the element is on screen, so a long queue
/// doesn't keep hundreds of idle animations running.</summary>
public sealed class EqualizerBars : StackPanel
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(EqualizerBars), new PropertyMetadata(false, (d, _) => ((EqualizerBars)d).Update()));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(EqualizerBars), new PropertyMetadata(Brushes.White, (d, e) =>
        {
            foreach (var bar in ((EqualizerBars)d)._bars) bar.Fill = (Brush)e.NewValue;
        }));

    private static readonly double[] Periods = [0.45, 0.62, 0.38];
    private static readonly double[] Rests = [0.35, 0.7, 0.5];
    private readonly List<Rectangle> _bars = [];

    public EqualizerBars()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Height = 14;
        for (var i = 0; i < 3; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = 14,
                Margin = new Thickness(1, 0, 1, 0),
                RadiusX = 1,
                RadiusY = 1,
                Fill = BarBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new ScaleTransform(1, Rests[i]),
            };
            _bars.Add(bar);
            Children.Add(bar);
        }
        OnScreen.Watch(this, Update);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    private void Update()
    {
        var run = IsActive && OnScreen.Check(this);
        for (var i = 0; i < _bars.Count; i++)
        {
            var scale = (ScaleTransform)_bars[i].RenderTransform;
            if (run)
            {
                var bounce = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(Periods[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                // A tiny icon that runs as long as music plays: 24 frames a second is plenty
                // (WPF would otherwise redraw for it at the monitor's full refresh rate).
                Timeline.SetDesiredFrameRate(bounce, 24);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = Rests[i];
            }
        }
    }
}
