using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

/// <summary>Seek bar in the spirit of One UI / Android's music players: the played part carries
/// layered, continuously drifting waves whose height and speed follow the music - quiet passages
/// ripple gently, loud ones surge - and each beat sends a ripple back along the wave from the
/// thumb. The loudness comes from <see cref="Waveform"/> at the current playback moment. Pausing
/// settles the waves flat. Clicking jumps, dragging scrubs; the seek is committed on release.</summary>
public sealed class WaveSeekBar : FrameworkElement
{
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(WaveSeekBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(WaveSeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WaveSeekBar)d).AnchorPosition()));

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(WaveSeekBar),
        new FrameworkPropertyMetadata(false, (d, _) => ((WaveSeekBar)d).OnIsPlayingChanged()));

    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(
        nameof(Waveform), typeof(WaveformData), typeof(WaveSeekBar),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveSeekBar),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(WaveSeekBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public WaveformData? Waveform { get => (WaveformData?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <summary>The user pressed on the bar - stop following playback until <see cref="SeekCompleted"/>.</summary>
    public event EventHandler? SeekStarted;

    /// <summary>The user released the bar (or used the arrow keys) at this position, in seconds.</summary>
    public event EventHandler<double>? SeekCompleted;

    private const double ThumbRadius = 7;
    private const double LineThickness = 3.5;
    private const double KeyStepSeconds = 5;

    /// <summary>Loudness used while a track is still being analyzed or can't be (e.g. OGG), so the
    /// wave still flows at a moderate height.</summary>
    private const double FallbackLevel = 0.55;

    private bool _dragging;
    private double _dragValue;
    private bool _hover;

    // _level: loudness driving wave height (fast attack, slower release, like a level meter).
    // _baseline: slow average of the loudness; a jump well above it counts as a beat.
    // _activity: 1 while playing, eases to 0 when paused so the waves settle flat.
    private double _level;
    private double _baseline;
    private double _activity;
    private double _phase;
    private double _sinceBeat = 1;
    private readonly List<(double Distance, double Strength)> _ripples = [];

    // Value only updates a few times a second; interpolate between updates so the thumb (and
    // the loudness lookup) move smoothly.
    private double _anchorValue;
    private readonly Stopwatch _sinceAnchor = Stopwatch.StartNew();

    private TimeSpan _lastFrame;
    private bool _animating;

    public WaveSeekBar()
    {
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = null;
        Loaded += (_, _) => UpdateAnimationState();
        Unloaded += (_, _) => StopAnimating();
    }

    private double Left => ThumbRadius + 1;
    private double Right => Math.Max(Left, ActualWidth - ThumbRadius - 1);

    private double ValueFromX(double x)
    {
        var fraction = Math.Clamp((x - Left) / Math.Max(1, Right - Left), 0, 1);
        return fraction * Math.Max(0, Maximum);
    }

    /// <summary>Where playback is right now, between the coarse Value updates.</summary>
    private double CurrentPosition()
    {
        if (_dragging) return _dragValue;
        if (!IsPlaying) return Value;
        var estimate = _anchorValue + Math.Min(_sinceAnchor.Elapsed.TotalSeconds, 0.6);
        return Math.Min(estimate, Math.Max(Value, Maximum));
    }

    private void AnchorPosition()
    {
        _anchorValue = Value;
        _sinceAnchor.Restart();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Transparent fill so the whole strip (not just the drawn line) takes mouse input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var mid = height / 2;
        var position = CurrentPosition();
        var fraction = Maximum > 0 ? Math.Clamp(position / Maximum, 0, 1) : 0;
        var thumbX = Left + (Right - Left) * fraction;
        var accent = (WaveBrush as SolidColorBrush)?.Color ?? Colors.DodgerBlue;

        if (thumbX < Right)
        {
            var trackPen = new Pen(TrackBrush, LineThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(trackPen, new Point(thumbX, mid), new Point(Right, mid));
        }

        if (thumbX > Left)
        {
            var maxHeight = mid - 2;
            // Height follows the music: a gentle ripple when quiet, a surge when loud.
            var amplitude = maxHeight * _activity * (0.18 + 0.82 * _level) * Math.Min(1, (thumbX - Left) / 60);
            if (amplitude > 0.3 || _ripples.Count > 0)
            {
                DrawWaveLayer(dc, accent, mid, thumbX, maxHeight, amplitude * 0.8, wavelength: 64, speed: 1.6, offset: 0, alpha: 0.45, withRipples: false);
                DrawWaveLayer(dc, accent, mid, thumbX, maxHeight, amplitude, wavelength: 110, speed: -1.1, offset: 2.1, alpha: 0.7, withRipples: true);
            }

            var playedPen = new Pen(new SolidColorBrush(accent), LineThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(playedPen, new Point(Left, mid), new Point(thumbX, mid));
        }

        var radius = _hover || _dragging ? ThumbRadius + 1.5 : ThumbRadius;
        dc.DrawEllipse(new SolidColorBrush(accent), new Pen(Brushes.White, 2.5), new Point(thumbX, mid), radius, radius);

        if ((_hover || _dragging) && Maximum > 1) DrawTimePreview(dc, mid);
    }

    private double _hoverX;

    /// <summary>Hovering (or dragging): a thin marker at the pointer and a small bubble above the
    /// bar with the time it points at, so a jump can be aimed precisely.</summary>
    private void DrawTimePreview(DrawingContext dc, double mid)
    {
        var x = _dragging ? Left + (Right - Left) * Math.Clamp(_dragValue / Maximum, 0, 1) : Math.Clamp(_hoverX, Left, Right);
        var seconds = _dragging ? _dragValue : ValueFromX(x);

        if (!_dragging)
        {
            var marker = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1.5);
            dc.DrawLine(marker, new Point(x, mid - 7), new Point(x, mid + 7));
        }

        var time = TimeSpan.FromSeconds(seconds);
        var label = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var w = text.Width + 14;
        var h = text.Height + 6;
        // Above the bar, kept inside the bar's horizontal extent.
        var left = Math.Clamp(x - w / 2, 0, Math.Max(0, ActualWidth - w));
        var top = mid - 14 - h;
        var bubble = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1C));
        dc.DrawRoundedRectangle(bubble, null, new Rect(left, top, w, h), 6, 6);
        dc.DrawText(text, new Point(left + 7, top + 3));
    }

    /// <summary>One translucent layer of drifting hills above the played line: a short ripple
    /// modulated by a long swell, tapered at both ends. The front layer also carries the beat
    /// ripples travelling back from the thumb.</summary>
    private void DrawWaveLayer(DrawingContext dc, Color accent, double mid, double endX, double maxHeight,
                               double amplitude, double wavelength, double speed, double offset, double alpha, bool withRipples)
    {
        var k = 2 * Math.PI / wavelength;
        var k2 = 2 * Math.PI / (wavelength * 2.7);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(Left, mid), isFilled: true, isClosed: true);
            for (var x = Left; x <= endX; x += 2)
            {
                var envelope = SmoothStep(Math.Min(1, (x - Left) / 28)) * SmoothStep(Math.Min(1, (endX - x) / 16));
                var ripple = 0.5 + 0.5 * Math.Sin(x * k + _phase * speed + offset);
                var swell = 0.7 + 0.3 * Math.Sin(x * k2 - _phase * 0.6 + offset * 1.7);
                var y = amplitude * envelope * ripple * swell;

                if (withRipples)
                {
                    foreach (var (distance, strength) in _ripples)
                    {
                        var d = (endX - 10 - distance - x) / 16;
                        y += maxHeight * strength * Math.Exp(-d * d) * envelope;
                    }
                }

                ctx.LineTo(new Point(x, mid - Math.Min(maxHeight, y)), true, true);
            }
            ctx.LineTo(new Point(endX, mid), true, true);
        }
        geometry.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb((byte)(255 * alpha * 0.45), accent.R, accent.G, accent.B),
            Color.FromArgb((byte)(255 * alpha), accent.R, accent.G, accent.B),
            new Point(0, 0), new Point(1, 0));
        fill.Freeze();
        dc.DrawGeometry(fill, null, geometry);
    }

    private static double SmoothStep(double t) => t * t * (3 - 2 * t);

    // --- animation: runs while playing, and briefly afterwards while the waves settle ---

    private void OnIsPlayingChanged()
    {
        AnchorPosition();
        UpdateAnimationState();
    }

    private void UpdateAnimationState()
    {
        if (IsLoaded && (IsPlaying || _activity > 0.002 || _ripples.Count > 0)) StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // Rendering can fire more than once per frame
        var dt = _lastFrame == TimeSpan.Zero ? 0 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        var target = !IsPlaying ? 0 : Waveform is { } waveform ? waveform.LevelAt(CurrentPosition()) : FallbackLevel;
        _level += (target - _level) * Math.Min(1, dt * (target > _level ? 18 : 4));
        _baseline += (target - _baseline) * Math.Min(1, dt * 1.2);
        _activity += ((IsPlaying ? 1.0 : 0.0) - _activity) * Math.Min(1, dt * 3);

        // Louder music flows faster.
        _phase += dt * (1.4 + 2.4 * _level) * Math.Max(_activity, 0.15);

        // A beat: loudness jumping well above its recent average sends a ripple back from the thumb.
        _sinceBeat += dt;
        if (IsPlaying && Waveform is not null && target - _baseline > 0.16 && _sinceBeat > 0.15)
        {
            _sinceBeat = 0;
            _ripples.Add((0, Math.Min(0.6, (target - _baseline) * 1.8)));
            if (_ripples.Count > 6) _ripples.RemoveAt(0);
        }
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var (distance, strength) = _ripples[i];
            strength *= Math.Exp(-dt * 2.2);
            if (strength < 0.02) _ripples.RemoveAt(i);
            else _ripples[i] = (distance + dt * 150, strength);
        }

        if (!IsPlaying && _activity < 0.002 && _ripples.Count == 0)
        {
            _activity = 0;
            _level = 0;
            StopAnimating();
        }
        InvalidateVisual();
    }

    // --- input ---

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (!CaptureMouse()) return;
        _dragging = true;
        _dragValue = ValueFromX(e.GetPosition(this).X);
        SeekStarted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        if (_dragging) _dragValue = ValueFromX(_hoverX);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragValue = ValueFromX(e.GetPosition(this).X);
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragging) FinishDrag(); // capture taken away (Alt+Tab etc.): commit where it was
    }

    private void FinishDrag()
    {
        _dragging = false; // before releasing capture, which re-enters OnLostMouseCapture
        if (IsMouseCaptured) ReleaseMouseCapture();
        SeekCompleted?.Invoke(this, _dragValue);
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch { Key.Left => -KeyStepSeconds, Key.Right => KeyStepSeconds, _ => 0 };
        if (step == 0 || Maximum <= 0) return;
        SeekStarted?.Invoke(this, EventArgs.Empty);
        SeekCompleted?.Invoke(this, Math.Clamp(Value + step, 0, Maximum));
        e.Handled = true;
    }
}
