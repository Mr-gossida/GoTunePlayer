using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TuneSync.App.Views;

/// <summary>A round control for the tone settings (프리앰프, 저음, 고음, 폭, 균형): a 270° arc that
/// fills from its resting point to the value, the reading in the middle and the name under it.
/// Drag up/down (or sideways), mouse wheel or arrow keys to turn it, double-click for the
/// resting value. Drawn in the player's colors, and only when the value changes.</summary>
public sealed class EqKnob : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(EqKnob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (d, v) => ((EqKnob)d).Coerce((double)v)));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(EqKnob), new FrameworkPropertyMetadata(-12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(EqKnob), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the fill starts and what a double-click goes back to.</summary>
    public static readonly DependencyProperty RestProperty = DependencyProperty.Register(
        nameof(Rest), typeof(double), typeof(EqKnob), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One wheel notch or arrow key press; drags snap to it too.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(EqKnob), new PropertyMetadata(0.5));

    /// <summary>The reading in the middle (formatted by the view model).</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(EqKnob), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(EqKnob), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The dial's diameter; the name goes under it.</summary>
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(EqKnob), new FrameworkPropertyMetadata(76.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(EqKnob), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(EqKnob), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(
        nameof(Face), typeof(Brush), typeof(EqKnob), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(EqKnob), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(EqKnob), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Sweep = 270;          // degrees the dial turns through
    private const double Start = 135;          // where the low end sits (clockwise from 3 o'clock)
    private const double Ring = 5;             // thickness of the arc
    private const double LabelGap = 6;
    private const double DragPixels = 180;     // a drag this long goes from one end to the other
    private Point _dragFrom;
    private double _dragValue;
    private bool _dragging;

    public EqKnob()
    {
        Focusable = true;
        Cursor = Cursors.SizeNS;
        FocusVisualStyle = null;
        SetResourceReference(AccentProperty, PlayerPalette.AccentKey);
        SetResourceReference(TrackProperty, PlayerPalette.TrackKey);
        SetResourceReference(FaceProperty, PlayerPalette.HoverKey);
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LabelBrushProperty, "TextFillColorSecondaryBrush");
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Rest { get => (double)GetValue(RestProperty); set => SetValue(RestProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Face { get => (Brush)GetValue(FaceProperty); set => SetValue(FaceProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private double Coerce(double value) => double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Rest;

    private double LabelHeight => Label.Length > 0 ? 16 + LabelGap : 0;

    protected override Size MeasureOverride(Size availableSize) => new(Diameter, Diameter + LabelHeight);

    private double Fraction(double value) => Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    private static Point OnCircle(Point center, double radius, double degrees)
    {
        var r = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(r), center.Y + radius * Math.Sin(r));
    }

    /// <summary>An arc of the dial between two fractions of its sweep.</summary>
    private static Geometry Arc(Point center, double radius, double from, double to)
    {
        if (to < from) (from, to) = (to, from);
        var a = Start + Sweep * from;
        var b = Start + Sweep * to;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(OnCircle(center, radius, a), isFilled: false, isClosed: false);
            context.ArcTo(OnCircle(center, radius, b), new Size(radius, radius), 0, b - a > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Diameter;
        // The whole box takes the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var center = new Point(ActualWidth / 2, size / 2);
        var radius = size / 2 - Ring / 2 - 1;
        var round = new Func<Brush, Pen>(brush => new Pen(brush, Ring) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

        // face, track, then the fill from rest to value
        var faceRadius = radius - Ring - 3;
        dc.DrawEllipse(Face, null, center, faceRadius, faceRadius);
        dc.DrawGeometry(null, round(Track), Arc(center, radius, 0, 1));
        double rest = Fraction(Rest), value = Fraction(Value);
        if (Math.Abs(value - rest) > 1e-6) dc.DrawGeometry(null, round(Accent), Arc(center, radius, rest, value));

        // the pointer: a dot on the face, toward the value
        var angle = Start + Sweep * value;
        var dot = OnCircle(center, faceRadius - 6, angle);
        dc.DrawEllipse(Accent, null, dot, _dragging || IsMouseOver ? 3.5 : 3, _dragging || IsMouseOver ? 3.5 : 3);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(Accent, 1.5), center, radius + Ring, radius + Ring);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        if (Text.Length > 0)
        {
            var reading = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                Math.Max(10, size * 0.16), TextBrush, dpi) { MaxTextWidth = faceRadius * 2, TextAlignment = TextAlignment.Center, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(reading, new Point(center.X - faceRadius, center.Y - reading.Height / 2));
        }
        if (Label.Length > 0)
        {
            var name = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(typeface.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, LabelBrush, dpi)
                { TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(ActualWidth, size) };
            dc.DrawText(name, new Point(ActualWidth / 2 - Math.Max(ActualWidth, size) / 2, size + LabelGap));
        }
    }

    private double Snap(double value) => Step > 0 ? Math.Round(value / Step) * Step : value;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            Value = Rest;
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragFrom = e.GetPosition(this);
        _dragValue = Value;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var at = e.GetPosition(this);
        // up or right turns it up
        var moved = (_dragFrom.Y - at.Y) + (at.X - _dragFrom.X);
        Value = Snap(_dragValue + moved / DragPixels * (Maximum - Minimum));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value += Math.Sign(e.Delta) * Step;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up or Key.Right: Value += Step; break;
            case Key.Down or Key.Left: Value -= Step; break;
            case Key.PageUp: Value += Step * 4; break;
            case Key.PageDown: Value -= Step * 4; break;
            case Key.Home or Key.Delete or Key.Back: Value = Rest; break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();
    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
}
