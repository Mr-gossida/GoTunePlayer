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
    /// <summary>Half an octave below the first band to half an octave above the last: ten equal
    /// columns, each centered on its band.</summary>
    private static readonly double LowHz = ToneCurve.BandFrequencies[0] / Math.Sqrt(2);
    private static readonly double HighHz = ToneCurve.BandFrequencies[^1] * Math.Sqrt(2);

    /// <summary>The top and bottom of the scale, in dB.</summary>
    public const double Range = 15;

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

        // Grid: a line per band, and at 0 and ±6/±12 dB.
        var faint = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        faint.Freeze();
        var zero = new Pen(GridBrush, 1.5);
        zero.Freeze();
        foreach (var frequency in ToneCurve.BandFrequencies)
        {
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

        // The curve, a point every 3 pixels.
        var count = Math.Max(2, (int)(width / 3) + 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineContext = line.Open())
        using (var areaContext = area.Open())
        {
            areaContext.BeginFigure(new Point(0, middle), isFilled: true, isClosed: true);
            for (var i = 0; i < count; i++)
            {
                var x = width * i / (count - 1);
                var frequency = LowHz * Math.Pow(HighHz / LowHz, (double)i / (count - 1));
                var point = new Point(x, Y(ToneCurve.ResponseDb(settings, frequency)));
                if (i == 0) lineContext.BeginFigure(point, isFilled: false, isClosed: false);
                else lineContext.LineTo(point, isStroked: true, isSmoothJoin: true);
                areaContext.LineTo(point, isStroked: false, isSmoothJoin: true);
            }
            areaContext.LineTo(new Point(width, middle), isStroked: false, isSmoothJoin: false);
        }
        line.Freeze();
        area.Freeze();

        dc.PushOpacity(IsActive ? 1 : 0.35);
        dc.PushOpacity(0.18);
        dc.DrawGeometry(Accent, null, area);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(Accent, 2) { LineJoin = PenLineJoin.Round }, line);
        dc.Pop();
    }
}
