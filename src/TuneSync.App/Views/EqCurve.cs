using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Audio;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

/// <summary>The equalizer's frequency response: what the current settings do to each pitch,
/// worked out from the very filters the player runs (<see cref="ToneCurve"/>) - bands, bass and
/// treble together. Its horizontal scale puts each band's frequency right above that band's
/// slider. Redrawn only when a setting changes.</summary>
public sealed class EqCurve : FrameworkElement
{
    /// <summary>The scale for a set of bands: half a band's width below the first to half a
    /// band's width above the last - equal columns (ten or 31), each centered on its band, as
    /// the sliders over the curve are laid out.</summary>
    internal static (double Low, double High) Axis(int bandCount)
    {
        var centers = ToneCurve.Frequencies(bandCount);
        var half = Math.Sqrt(centers[1] / centers[0]);
        return (centers[0] / half, centers[^1] * half);
    }

    private double LowHz => Axis(BandCount).Low;
    private double HighHz => Axis(BandCount).High;
    private int BandCount => ToneCurve.BandCountOf(Settings?.BandsDb?.Length ?? 0);

    /// <summary>The top and bottom of the scale, in dB: 12 to run exactly along band sliders
    /// drawn over it (their ±12 dB ends), so each slider's handle sits on the curve.</summary>
    public static readonly DependencyProperty RangeProperty = DependencyProperty.Register(
        nameof(Range), typeof(double), typeof(EqCurve), new FrameworkPropertyMetadata(15.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Range { get => (double)GetValue(RangeProperty); set => SetValue(RangeProperty, value); }

    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings), typeof(ToneSettings), typeof(EqCurve), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bound to a counter that goes up whenever the settings change (the settings
    /// object itself stays the same one).</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(EqCurve), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Off: the curve is drawn faint, as what would apply.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(EqCurve), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(EqCurve), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(EqCurve), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public EqCurve()
    {
        SetResourceReference(AccentProperty, PlayerPalette.AccentKey);
        SetResourceReference(GridBrushProperty, PlayerPalette.TrackKey);
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public ToneSettings? Settings { get => (ToneSettings?)GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    private double X(double frequency) => Math.Log(frequency / LowHz) / Math.Log(HighHz / LowHz) * ActualWidth;
    private double Y(double db) => (0.5 - Math.Clamp(db, -Range - 1, Range + 1) / (2 * Range)) * ActualHeight;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 8 || height < 8) return;

        // Grid: a line per octave (per band of the ten), and at 0 and ±6/±12 dB.
        var faint = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        faint.Freeze();
        var zero = new Pen(GridBrush, 1.5);
        zero.Freeze();
        foreach (var frequency in ToneCurve.BandFrequencies)
        {
            if (frequency < LowHz || frequency > HighHz) continue;
            var x = Math.Round(X(frequency)) + 0.5;
            dc.DrawLine(faint, new Point(x, 0), new Point(x, height));
        }
        foreach (var db in new[] { -12.0, -6, 6, 12 })
        {
            var y = Math.Round(Y(db)) + 0.5;
            dc.DrawLine(faint, new Point(0, y), new Point(width, y));
        }
        var middle = Math.Round(Y(0)) + 0.5;
        dc.DrawLine(zero, new Point(0, middle), new Point(width, middle));

        if (Settings is not { } settings) return;

        // The real response (bands, bass and treble together), a point every 3 pixels.
        var count = Math.Max(2, (int)(width / 3) + 1);
        var frequencies = new double[count];
        var levels = new double[count];
        for (var i = 0; i < count; i++) frequencies[i] = LowHz * Math.Pow(HighHz / LowHz, (double)i / (count - 1));
        ToneCurve.ResponseDb(settings, frequencies, levels);
        var response = new List<Point>(count);
        for (var i = 0; i < count; i++) response.Add(new Point(width * i / (count - 1), Y(levels[i])));

        // Over band sliders, the main line runs smoothly through their handles (what the hand
        // set), and the real response - the tone bars and the bands' overlap included - is the
        // thin line beside it.
        var main = ThroughBands ? ThroughHandles(settings.BandsDb, width) : response;
        var line = Polyline(main, closeTo: null);
        var area = Polyline(main, closeTo: middle);

        dc.PushOpacity(IsActive ? 1 : 0.35);
        dc.PushOpacity(0.18);
        dc.DrawGeometry(Accent, null, area);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(Accent, 2) { LineJoin = PenLineJoin.Round }, line);
        if (ThroughBands)
        {
            dc.PushOpacity(0.55);
            dc.DrawGeometry(null, new Pen(Accent, 1) { LineJoin = PenLineJoin.Round, DashStyle = new DashStyle([3, 3], 0) }, Polyline(response, closeTo: null));
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>Draw the main line through the band sliders' handles instead of the real response.</summary>
    public static readonly DependencyProperty ThroughBandsProperty = DependencyProperty.Register(
        nameof(ThroughBands), typeof(bool), typeof(EqCurve), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ThroughBands { get => (bool)GetValue(ThroughBandsProperty); set => SetValue(ThroughBandsProperty, value); }

    /// <summary>A smooth line through each band's handle (Catmull-Rom), level out to the edges.</summary>
    private List<Point> ThroughHandles(double[] gains, double width)
    {
        var handles = new List<Point> { new(0, Y(gains[0])) };
        var centers = ToneCurve.Frequencies(gains.Length);
        for (var i = 0; i < gains.Length; i++) handles.Add(new Point(X(centers[i]), Y(gains[i])));
        handles.Add(new Point(width, Y(gains[^1])));

        var points = new List<Point>();
        for (var i = 0; i + 1 < handles.Count; i++)
        {
            Point p0 = handles[Math.Max(0, i - 1)], p1 = handles[i], p2 = handles[i + 1], p3 = handles[Math.Min(handles.Count - 1, i + 2)];
            const int steps = 14;
            for (var s = 0; s < steps; s++)
            {
                var t = (double)s / steps;
                double t2 = t * t, t3 = t2 * t;
                points.Add(new Point(
                    0.5 * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                    0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
            }
        }
        points.Add(handles[^1]);
        return points;
    }

    /// <summary>A line through the points, or (closeTo set) the area between it and that height.</summary>
    private static StreamGeometry Polyline(List<Point> points, double? closeTo)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (closeTo is { } baseline)
            {
                context.BeginFigure(new Point(points[0].X, baseline), isFilled: true, isClosed: true);
                foreach (var point in points) context.LineTo(point, isStroked: false, isSmoothJoin: true);
                context.LineTo(new Point(points[^1].X, baseline), isStroked: false, isSmoothJoin: false);
            }
            else
            {
                context.BeginFigure(points[0], isFilled: false, isClosed: false);
                for (var i = 1; i < points.Count; i++) context.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
