using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Audio;

namespace TuneSync.App.Views;

/// <summary>The live spectrum behind the equalizer's curve: what is coming out of the speakers
/// right now, as thin columns on the curve's own frequency scale (so a band's slider stands over
/// the pitches it moves), with a small mark where each column last peaked. Kept faint, so the
/// curve and the handles stay in front. Fed a frame at a time by the panel (<see cref="Show"/>).</summary>
public sealed class EqSpectrum : FrameworkElement
{
    /// <summary>Roughly this many pixels per column.</summary>
    private const double ColumnPixels = 6;

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(EqSpectrum), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private float[] _levels = [];
    private float[] _peaks = [];

    public EqSpectrum()
    {
        SetResourceReference(AccentProperty, PlayerPalette.AccentKey);
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    /// <summary>How many columns fit the current width.</summary>
    public int Columns => Math.Clamp((int)(ActualWidth / ColumnPixels), 16, 200);

    public void Show(LiveAnalyzer analyzer)
    {
        _levels = analyzer.Levels;
        _peaks = analyzer.Peaks;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        var count = _levels.Length;
        if (count == 0 || width < 8 || height < 8) return;
        var column = width / count;
        var bar = Math.Max(1, column - 2);

        var bars = new StreamGeometry();
        var marks = new StreamGeometry();
        using (StreamGeometryContext b = bars.Open(), m = marks.Open())
        {
            for (var i = 0; i < count; i++)
            {
                var x = i * column + (column - bar) / 2;
                var top = height * (1 - _levels[i]);
                if (_levels[i] > 0.002f) Rectangle(b, new Rect(x, top, bar, height - top));
                if (i < _peaks.Length && _peaks[i] > 0.01f)
                    Rectangle(m, new Rect(x, Math.Min(height - 1.5, height * (1 - _peaks[i])), bar, 1.5));
            }
        }
        bars.Freeze();
        marks.Freeze();

        // fading toward the bottom, so the low end of a column doesn't turn into a wall
        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var color = (Accent as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x5C, color.R, color.G, color.B), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, color.R, color.G, color.B), 1));
        fill.Freeze();
        dc.DrawGeometry(fill, null, bars);
        dc.PushOpacity(0.4);
        dc.DrawGeometry(Accent, null, marks);
        dc.Pop();
    }

    private static void Rectangle(StreamGeometryContext context, Rect rect)
    {
        context.BeginFigure(rect.TopLeft, isFilled: true, isClosed: true);
        context.LineTo(rect.TopRight, isStroked: false, isSmoothJoin: false);
        context.LineTo(rect.BottomRight, isStroked: false, isSmoothJoin: false);
        context.LineTo(rect.BottomLeft, isStroked: false, isSmoothJoin: false);
    }
}
