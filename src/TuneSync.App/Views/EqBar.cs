using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TuneSync.App.Views;

/// <summary>A sideways bar for the tone settings (저음, 고음, 폭, 균형): the name and the reading on
/// a line, and under them a track the full width that fills from its resting point (the tick) to
/// the value - out to either side, like a balance control. Click or drag anywhere on the track to
/// put the value there; mouse wheel or arrow keys step it; double-click goes back to rest. Drawn
/// in the player's colors.</summary>
public sealed class EqBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(EqBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (d, v) => ((EqBar)d).Coerce((double)v)));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(EqBar), new FrameworkPropertyMetadata(-12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(EqBar), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the fill starts (the tick) and what a double-click goes back to.</summary>
    public static readonly DependencyProperty RestProperty = DependencyProperty.Register(
        nameof(Rest), typeof(double), typeof(EqBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One wheel notch or arrow key press; drags snap to it too.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(EqBar), new PropertyMetadata(0.5));

    /// <summary>The reading on the right (formatted by the view model).</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(EqBar), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(EqBar), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(EqBar), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(EqBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThumbBrushProperty = DependencyProperty.Register(
        nameof(ThumbBrush), typeof(Brush), typeof(EqBar), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(EqBar), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(EqBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Thumb = 8;            // the handle's radius
    private const double TrackThickness = 4;
    private const double TextRow = 20;         // the name and the reading
    private const double TrackRow = 24;
    private bool _dragging;

    public EqBar()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        SetResourceReference(AccentProperty, PlayerPalette.AccentKey);
        SetResourceReference(TrackProperty, PlayerPalette.TrackKey);
        SetResourceReference(ThumbBrushProperty, PlayerPalette.OnAccentKey);
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
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush ThumbBrush { get => (Brush)GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private double Coerce(double value) => double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Rest;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width, TextRow + TrackRow);

    /// <summary>The track's ends: inset by the handle so it never pokes out.</summary>
    private (double Left, double Right) TrackSpan()
    {
        var left = Thumb + 1;
        var right = Math.Max(left + 1, ActualWidth - Thumb - 1);
        return (left, right);
    }

    private double Fraction(double value) => Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    private double XOf(double value)
    {
        var (left, right) = TrackSpan();
        return left + (right - left) * Fraction(value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // The whole box takes the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var (left, right) = TrackSpan();
        var middle = TextRow + TrackRow / 2;
        var hot = _dragging || IsMouseOver || IsKeyboardFocused;

        // track, the rest tick, then the fill from rest to value
        var track = new Rect(left - Thumb / 2, middle - TrackThickness / 2, right - left + Thumb, TrackThickness);
        dc.DrawRoundedRectangle(Track, null, track, TrackThickness / 2, TrackThickness / 2);
        double rest = XOf(Rest), at = XOf(Value);
        dc.DrawRectangle(Track, null, new Rect(Math.Round(rest) - 1, middle - 7, 2, 14));
        if (Math.Abs(at - rest) > 0.5)
        {
            var fill = new Rect(Math.Min(rest, at), middle - TrackThickness / 2, Math.Abs(at - rest), TrackThickness);
            dc.DrawRoundedRectangle(Accent, null, fill, TrackThickness / 2, TrackThickness / 2);
        }

        // the handle: an accent ring, a little larger while held or pointed at
        var radius = hot ? Thumb + 1 : Thumb;
        dc.DrawEllipse(Accent, null, new Point(at, middle), radius, radius);
        dc.DrawEllipse(ThumbBrush, null, new Point(at, middle), radius - 3.5, radius - 3.5);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(Accent, 1.5), new Point(at, middle), radius + 3, radius + 3);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var semibold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        // the reading at the right; the name takes what it leaves (shortened only when that's too little)
        var readingWidth = 0.0;
        if (Text.Length > 0)
        {
            var reading = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, semibold, 13, hot ? Accent : TextBrush, dpi)
                { MaxLineCount = 1 };
            readingWidth = reading.Width;
            dc.DrawText(reading, new Point(ActualWidth - 1 - reading.Width, TextRow - 2 - reading.Height));
        }
        if (Label.Length > 0)
        {
            var name = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, regular, 13, LabelBrush, dpi)
                { MaxTextWidth = Math.Max(1, ActualWidth - 2 - readingWidth - 6), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(name, new Point(1, TextRow - 2 - name.Height));
        }
    }

    private double Snap(double value) => Step > 0 ? Math.Round(value / Step) * Step : value;

    /// <summary>The value at a point along the track.</summary>
    private double ValueAt(double x)
    {
        var (left, right) = TrackSpan();
        var fraction = Math.Clamp((x - left) / (right - left), 0, 1);
        var value = Snap(Minimum + fraction * (Maximum - Minimum));
        // a touch of pull toward the rest point, so the middle is easy to hit
        return Math.Abs(XOf(value) - XOf(Rest)) < 4 ? Rest : value;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            Value = Rest;
            e.Handled = true;
            return;
        }
        var x = e.GetPosition(this).X;
        // a click on the name or the reading only focuses it; the track's row takes hold
        if (e.GetPosition(this).Y < TextRow - 2)
        {
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        Value = ValueAt(x);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Value = ValueAt(e.GetPosition(this).X);
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
