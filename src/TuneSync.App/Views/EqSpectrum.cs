using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Audio;

namespace TuneSync.App.Views;

/// <summary>The live spectrum behind the equalizer's curve: what is coming out of the speakers
/// right now, as thin columns on the curve's own frequency scale (so a band's slider stands over
/// the pitches it moves), with a small mark where each column last peaked. Kept faint, so the
/// curve and the handles stay in front. Fed a frame at a time by the panel (<see cref="Show"/>).
/// Drawn without anti-aliasing, on whole screen pixels: WPF works out anti-aliased edges on the
/// CPU, and for a few hundred columns redrawn sixty times a second over a maximized window that
/// alone held its render thread at 100% (frames 20-40 ms apart - the stutter). Bars on pixel
/// edges need no smoothing anyway.</summary>
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
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        UseLayoutRounding = true; // its own corner on a whole pixel too
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
        // in device pixels, so every edge lands on a pixel boundary
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        double widthPx = width * sx, heightPx = Math.Round(height * sy);
        var column = widthPx / count;
        var gap = column >= 4 ? Math.Round(2 * sx) : 1;
        var mark = Math.Max(1, Math.Round(1.5 * sy));

        var bars = new StreamGeometry();
        var marks = new StreamGeometry();
        using (StreamGeometryContext b = bars.Open(), m = marks.Open())
        {
            for (var i = 0; i < count; i++)
            {
                var left = Math.Round(i * column + gap / 2);
                var right = Math.Max(left + 1, Math.Round((i + 1) * column - gap / 2));
                if (_levels[i] > 0.002f)
                {
                    var top = Math.Round(heightPx * (1 - _levels[i]));
                    if (top < heightPx) Rectangle(b, left, top, right, heightPx, sx, sy);
                }
                if (i < _peaks.Length && _peaks[i] > 0.01f)
                {
                    var top = Math.Min(heightPx - mark, Math.Round(heightPx * (1 - _peaks[i])));
                    Rectangle(m, left, top, right, top + mark, sx, sy);
                }
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

    /// <summary>A rectangle given in device pixels, added in this element's units.</summary>
    private static void Rectangle(StreamGeometryContext context, double left, double top, double right, double bottom, double sx, double sy)
    {
        context.BeginFigure(new Point(left / sx, top / sy), isFilled: true, isClosed: true);
        context.LineTo(new Point(right / sx, top / sy), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(right / sx, bottom / sy), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(left / sx, bottom / sy), isStroked: false, isSmoothJoin: false);
    }
}
