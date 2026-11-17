using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TuneSync.App.Views;

/// <summary>A level slider for the equalizer: a thin track that fills from its resting point
/// (the middle for a ±dB level) to the thumb, so a boost and a cut read at a glance. Drag or
/// click to set, mouse wheel or arrow keys for fine steps, double-click to go back to the
/// resting value. Drawn in the player's colors.</summary>
public sealed class EqSlider : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(EqSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (d, v) => ((EqSlider)d).Coerce((double)v)));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(EqSlider), new FrameworkPropertyMetadata(-12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(EqSlider), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the fill starts and what a double-click goes back to.</summary>
    public static readonly DependencyProperty RestProperty = DependencyProperty.Register(
        nameof(Rest), typeof(double), typeof(EqSlider), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One wheel notch or arrow key press; drags snap to it too.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(EqSlider), new PropertyMetadata(0.5));

    public static readonly DependencyProperty IsVerticalProperty = DependencyProperty.Register(
        nameof(IsVertical), typeof(bool), typeof(EqSlider), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(EqSlider), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(EqSlider), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Slim, for the 31 bands' sliders (about 16 pixels apart at the panel's narrowest):
    /// a smaller handle and a thinner track. The track keeps its ends where they are, so a
    /// handle still sits on the response curve drawn behind.</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(EqSlider), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double Thumb = 7;      // radius, and how far the track's ends are set in
    // Slim, the handle fits its column (31 bands get as little as ~8 pixels each in a narrow
    // panel): never wider than the column, never smaller than can be seen and grabbed.
    private double ThumbRadius => IsCompact ? Math.Clamp(ActualWidth / 2 - 0.5, 3.5, 5) : Thumb;
    private double Thickness => IsCompact ? 2.5 : 4;  // of the track
    private bool _dragging;

    /// <summary>The pointer is on it, it is being dragged, or it has the keyboard.</summary>
    public bool IsActive => _dragging || IsMouseOver || IsKeyboardFocused;

    /// <summary>Raised when <see cref="IsActive"/> may have changed.</summary>
    public event EventHandler? ActiveChanged;

    public EqSlider()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        SetResourceReference(AccentProperty, PlayerPalette.AccentKey);
        SetResourceReference(TrackProperty, PlayerPalette.TrackKey);
        FocusVisualStyle = null;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Rest { get => (double)GetValue(RestProperty); set => SetValue(RestProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public bool IsVertical { get => (bool)GetValue(IsVerticalProperty); set => SetValue(IsVerticalProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    private double Coerce(double value) => double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Rest;

    protected override Size MeasureOverride(Size availableSize) =>
        // unbounded (measuring what an arrangement takes): no more than the band row's least height
        // slim: the whole column, so the pointer is always on one band or the next, never between
        IsVertical ? new Size(IsCompact ? (double.IsInfinity(availableSize.Width) ? 12 : availableSize.Width) : Thumb * 2 + 6, double.IsInfinity(availableSize.Height) ? 104 : availableSize.Height)
                   : new Size(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, Thumb * 2 + 6);

    /// <summary>The track's length in pixels, and where along it (0..1, from the low end) a value sits.</summary>
    private double Length => Math.Max(1, (IsVertical ? ActualHeight : ActualWidth) - Thumb * 2);

    private double Fraction(double value) => Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    /// <summary>A point on the track: along it for a value, centered across it.</summary>
    private Point At(double value)
    {
        var along = Fraction(value) * Length;
        return IsVertical ? new Point(ActualWidth / 2, ActualHeight - Thumb - along) : new Point(Thumb + along, ActualHeight / 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // The whole box takes the mouse, not only the thin line.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        Point low = At(Minimum), high = At(Maximum), rest = At(Rest), thumb = At(Value);
        dc.DrawLine(new Pen(Track, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, low, high);
        if (Rest > Minimum && Rest < Maximum)
        {
            // A small notch where the resting value is.
            var across = IsVertical ? new Vector(Thickness + 2, 0) : new Vector(0, Thickness + 2);
            dc.DrawLine(new Pen(Track, 1.5), rest - across, rest + across);
        }
        if (Math.Abs(Value - Rest) > 1e-9)
            dc.DrawLine(new Pen(Accent, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, rest, thumb);

        var radius = _dragging || IsMouseOver ? ThumbRadius : ThumbRadius - (IsCompact ? 1 : 1.5);
        dc.DrawEllipse(Accent, null, thumb, radius, radius);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(Accent, IsCompact ? 1 : 1.5), thumb, ThumbRadius + 2, ThumbRadius + 2);
    }

    private void SetFromMouse(Point point)
    {
        var along = IsVertical ? ActualHeight - Thumb - point.Y : point.X - Thumb;
        var value = Minimum + Math.Clamp(along / Length, 0, 1) * (Maximum - Minimum);
        if (Step > 0) value = Math.Round(value / Step) * Step;
        Value = value;
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
        _dragging = true;
        CaptureMouse();
        SetFromMouse(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromMouse(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Changed();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false;
        Changed();
    }

    private void Changed()
    {
        InvalidateVisual();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
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

    protected override void OnMouseEnter(MouseEventArgs e) => Changed();
    protected override void OnMouseLeave(MouseEventArgs e) => Changed();
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => Changed();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => Changed();
}
