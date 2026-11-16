using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Audio;

namespace TuneSync.App.Views;

/// <summary>The output level of each channel, as two sideways bars from -48 dBFS to full scale:
/// the average level filled in, the momentary peak in a lighter shade beyond it, and a tick
/// where the highest recent peak was (warm red when it reached full scale). A scale runs under
/// them. Fed a frame at a time by the panel (<see cref="Show"/>).</summary>
public sealed class EqLevelMeter : FrameworkElement
{
    private const double FloorDb = -48;
    private const double NameWidth = 16;
    private const double Thickness = 6;
    private const double RowGap = 6;
    private const double ScaleHeight = 16;
    private static readonly double[] Marks = [-36, -24, -12, -6, 0];
    private static readonly Brush ClipBrush = Frozen(Color.FromRgb(0xE5, 0x53, 0x4B));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(EqLevelMeter), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(EqLevelMeter), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(EqLevelMeter), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(EqLevelMeter), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly double[] _rms = [FloorDb, FloorDb], _peak = [FloorDb, FloorDb], _hold = [FloorDb, FloorDb];
    private readonly bool[] _clipped = new bool[2];

    public EqLevelMeter()
    {
        SetResourceReference(AccentProperty, PlayerPalette.AccentKey);
        SetResourceReference(TrackProperty, PlayerPalette.TrackKey);
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LabelBrushProperty, "TextFillColorTertiaryBrush");
        IsHitTestVisible = false;
    }

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    public void Show(LiveAnalyzer analyzer)
    {
        for (var channel = 0; channel < 2; channel++)
        {
            _rms[channel] = analyzer.RmsDb[channel];
            _peak[channel] = analyzer.PeakDb[channel];
            _hold[channel] = analyzer.HoldDb[channel];
            _clipped[channel] = analyzer.Clipped[channel];
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width, Thickness * 2 + RowGap + 4 + ScaleHeight);

    private static double Fraction(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var left = NameWidth;
        var span = Math.Max(1, ActualWidth - left - 1);
        double X(double db) => left + span * Fraction(db);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var fade = Accent.Clone();
        fade.Opacity = 0.4;
        fade.Freeze();

        for (var channel = 0; channel < 2; channel++)
        {
            var top = channel * (Thickness + RowGap);
            var name = new FormattedText(channel == 0 ? "L" : "R", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, LabelBrush, dpi);
            dc.DrawText(name, new Point(0, top + Thickness / 2 - name.Height / 2));

            var radius = Thickness / 2;
            dc.DrawRoundedRectangle(Track, null, new Rect(left, top, span, Thickness), radius, radius);
            var peak = X(_peak[channel]) - left;
            if (peak > 0.5) dc.DrawRoundedRectangle(fade, null, new Rect(left, top, peak, Thickness), radius, radius);
            var rms = X(_rms[channel]) - left;
            if (rms > 0.5) dc.DrawRoundedRectangle(Accent, null, new Rect(left, top, rms, Thickness), radius, radius);
            if (_hold[channel] > FloorDb + 0.5)
            {
                var x = Math.Min(left + span - 2, X(_hold[channel]) - 1);
                dc.DrawRectangle(_clipped[channel] ? ClipBrush : TextBrush, null, new Rect(x, top - 1, 2, Thickness + 2));
            }
        }

        // the scale
        var scaleTop = 2 * Thickness + RowGap + 4;
        foreach (var db in Marks)
        {
            var x = X(db);
            var text = new FormattedText(db == 0 ? "0" : ((int)db).ToString("+0;−0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 10, LabelBrush, dpi);
            var at = db == 0 ? x - text.Width : x - text.Width / 2;
            dc.DrawText(text, new Point(at, scaleTop));
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
