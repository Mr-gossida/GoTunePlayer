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

    /// <summary>Waves (in the cover's colors) or flames.</summary>
    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(SeekBarStyle), typeof(WaveSeekBar),
        new FrameworkPropertyMetadata(SeekBarStyle.Wave, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WaveSeekBar)d)._sparks.Clear()));

    public SeekBarStyle Look { get => (SeekBarStyle)GetValue(LookProperty); set => SetValue(LookProperty, value); }

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

    // _flash: a beat's brief brightening (decays fast). _sparks: embers rising off the flames.
    private double _flash;
    private readonly List<Spark> _sparks = [];
    private struct Spark { public double X, Y, Vx, Vy, Life, Size; }

    private bool Flames => Look == SeekBarStyle.Flame;

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

        // The wave's colors drift around the cover's color - a warmer and a cooler neighbor
        // that slowly trade places - and brighten on each beat.
        var drift = 22 * Math.Sin(_phase * 0.35);
        var cool = Shift(accent, -38 + drift, 1.0);
        var warm = Shift(accent, 42 + drift, 1.12 + 0.25 * _flash);
        var thumbColor = Flames ? Color.FromRgb(0xFF, 0x9A, 0x2E) : accent;

        if (thumbX > Left)
        {
            var maxHeight = mid - 2;
            // Height follows the music: a gentle ripple when quiet, a surge when loud.
            var fadeIn = Math.Min(1, (thumbX - Left) / 60);
            if (Flames)
            {
                var amplitude = maxHeight * _activity * (0.32 + 0.68 * _level) * fadeIn * (1 + 0.25 * _flash);
                if (amplitude > 0.3)
                {
                    DrawFlameLayer(dc, mid, thumbX, maxHeight, amplitude, seed: 0.0, scale: 1.0, core: false);
                    DrawFlameLayer(dc, mid, thumbX, maxHeight, amplitude * 0.62, seed: 3.7, scale: 0.8, core: true);
                }
                DrawSparks(dc);
                DrawPlayedLine(dc, mid, thumbX, Color.FromRgb(0xC2, 0x2E, 0x12), Color.FromRgb(0xFF, 0x7A, 0x1A), Color.FromRgb(0xFF, 0xD2, 0x5A));
            }
            else
            {
                var amplitude = maxHeight * _activity * (0.18 + 0.82 * _level) * fadeIn;
                if (amplitude > 0.3 || _ripples.Count > 0)
                {
                    DrawWaveLayer(dc, cool, warm, mid, thumbX, maxHeight, amplitude * 0.8, wavelength: 64, speed: 1.6, offset: 0, alpha: 0.45, withRipples: false);
                    DrawWaveLayer(dc, Shift(accent, drift * 0.5, 1.0), warm, mid, thumbX, maxHeight, amplitude, wavelength: 110, speed: -1.1, offset: 2.1,
                        alpha: 0.72 + 0.2 * _flash, withRipples: true);
                }
                DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
            }
        }

        var radius = _hover || _dragging ? ThumbRadius + 1.5 : ThumbRadius;
        if (_flash > 0.05 && IsPlaying)
        {
            // A soft halo that swells with the beat.
            var halo = new SolidColorBrush(Color.FromArgb((byte)(90 * _flash), thumbColor.R, thumbColor.G, thumbColor.B));
            dc.DrawEllipse(halo, null, new Point(thumbX, mid), radius + 6 * _flash, radius + 6 * _flash);
        }
        dc.DrawEllipse(new SolidColorBrush(thumbColor), new Pen(Brushes.White, 2.5), new Point(thumbX, mid), radius, radius);

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
    private void DrawWaveLayer(DrawingContext dc, Color from, Color to, double mid, double endX, double maxHeight,
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

        // Along the bar from one hue to the other, getting stronger toward the thumb.
        var fill = new LinearGradientBrush(
            Color.FromArgb((byte)(255 * Math.Min(1, alpha * 0.45)), from.R, from.G, from.B),
            Color.FromArgb((byte)(255 * Math.Min(1, alpha)), to.R, to.G, to.B),
            new Point(0, 0), new Point(1, 0));
        fill.Freeze();
        dc.DrawGeometry(fill, null, geometry);
    }

    /// <summary>The played part of the line, its color running from <paramref name="start"/>
    /// through <paramref name="middle"/> to <paramref name="end"/> at the thumb.</summary>
    private void DrawPlayedLine(DrawingContext dc, double mid, double thumbX, Color start, Color middle, Color end)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(Left, 0),
            EndPoint = new Point(Math.Max(Left + 1, thumbX), 0),
            GradientStops = { new GradientStop(start, 0), new GradientStop(middle, 0.55), new GradientStop(end, 1) },
        };
        brush.Freeze();
        var pen = new Pen(brush, LineThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pen, new Point(Left, mid), new Point(thumbX, mid));
    }

    /// <summary>A row of flame tongues licking up from the played line: pointed tips that sway
    /// sideways and flicker in height, deep red at the root through orange to a fading yellow
    /// (or, for the inner layer, a hot pale core). Height follows the music like the waves.</summary>
    private void DrawFlameLayer(DrawingContext dc, double mid, double endX, double maxHeight, double amplitude,
                                double seed, double scale, bool core)
    {
        var t = _phase * 1.6 + seed;
        var k = 2 * Math.PI / (15 * scale);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(Left, mid), isFilled: true, isClosed: true);
            for (var x = Left; x <= endX; x += 1.5)
            {
                var envelope = SmoothStep(Math.Min(1, (x - Left) / 24)) * SmoothStep(Math.Min(1, (endX - x) / 10));
                // Sway: the tongues lean and drift, never in step.
                var sway = 6 * Math.Sin(x * 0.023 + t * 1.3) + 3.5 * Math.Sin(x * 0.061 - t * 2.2 + seed);
                var tongue = Math.Pow(0.5 + 0.5 * Math.Cos((x + sway) * k - t * 0.9), 2.6);
                var flicker = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(x * 0.17 + t * 7.1 + seed))
                                         * (0.5 + 0.5 * Math.Sin(x * 0.043 - t * 4.3 + seed * 1.9));
                // Tallest just behind the thumb, where the music is now.
                var nearThumb = 0.75 + 0.25 * SmoothStep(Math.Clamp(1 - (endX - x) / 140, 0, 1));
                var y = amplitude * envelope * nearThumb * (0.28 + 0.72 * tongue * flicker);
                ctx.LineTo(new Point(x, mid - Math.Min(maxHeight + 3, y)), true, true);
            }
            ctx.LineTo(new Point(endX, mid), true, true);
        }
        geometry.Freeze();

        var fill = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, mid),
            EndPoint = new Point(0, mid - maxHeight - 3),
            GradientStops = core
                ? new GradientStopCollection
                {
                    new(Color.FromArgb(0xF2, 0xFF, 0xF4, 0xC8), 0),
                    new(Color.FromArgb(0xC8, 0xFF, 0xC8, 0x4A), 0.45),
                    new(Color.FromArgb(0x00, 0xFF, 0xA8, 0x30), 1),
                }
                : new GradientStopCollection
                {
                    new(Color.FromArgb(0xE6, 0xD4, 0x2A, 0x10), 0),
                    new(Color.FromArgb(0xD0, 0xFF, 0x6A, 0x14), 0.4),
                    new(Color.FromArgb(0x90, 0xFF, 0xB0, 0x2E), 0.75),
                    new(Color.FromArgb(0x00, 0xFF, 0xE0, 0x70), 1),
                },
        };
        fill.Freeze();
        dc.DrawGeometry(fill, null, geometry);
    }

    private void DrawSparks(DrawingContext dc)
    {
        foreach (var spark in _sparks)
        {
            var alpha = (byte)(255 * Math.Clamp(spark.Life, 0, 1));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, (byte)(0xB0 + 0x40 * spark.Life), 0x50));
            dc.DrawEllipse(brush, null, new Point(spark.X, spark.Y), spark.Size, spark.Size);
        }
    }

    /// <summary>A neighbor of <paramref name="color"/> on the color wheel (degrees), a little
    /// brighter or darker by <paramref name="light"/>. Greys stay grey.</summary>
    private static Color Shift(Color color, double degrees, double light)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        double h = 0, s = 0;
        if (max - min > 1e-6)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        h = ((h + degrees) % 360 + 360) % 360;
        l = Math.Clamp(l * light, 0, 0.92);
        s = Math.Max(s, s > 0.08 ? 0.45 : s); // a muted cover still gets visibly different neighbors

        double Hue(double p, double q, double t)
        {
            t = (t % 1 + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        var q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p2 = 2 * l - q2;
        var hh = h / 360;
        return s < 1e-6
            ? Color.FromRgb((byte)(l * 255), (byte)(l * 255), (byte)(l * 255))
            : Color.FromRgb((byte)(255 * Hue(p2, q2, hh + 1 / 3.0)), (byte)(255 * Hue(p2, q2, hh)), (byte)(255 * Hue(p2, q2, hh - 1 / 3.0)));
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
        if (IsLoaded && (IsPlaying || _activity > 0.002 || _ripples.Count > 0 || _sparks.Count > 0)) StartAnimating();
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
        var beat = false;
        if (IsPlaying && Waveform is not null && target - _baseline > 0.16 && _sinceBeat > 0.15)
        {
            _sinceBeat = 0;
            beat = true;
            _flash = Math.Max(_flash, Math.Min(1, (target - _baseline) * 3));
            _ripples.Add((0, Math.Min(0.6, (target - _baseline) * 1.8)));
            if (_ripples.Count > 6) _ripples.RemoveAt(0);
        }
        _flash *= Math.Exp(-dt * 5);
        UpdateSparks(dt, beat);
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var (distance, strength) = _ripples[i];
            strength *= Math.Exp(-dt * 2.2);
            if (strength < 0.02) _ripples.RemoveAt(i);
            else _ripples[i] = (distance + dt * 150, strength);
        }

        if (!IsPlaying && _activity < 0.002 && _ripples.Count == 0 && _sparks.Count == 0)
        {
            _activity = 0;
            _level = 0;
            StopAnimating();
        }
        InvalidateVisual();
    }

    /// <summary>Flames throw off embers: a few all the time, a burst on each beat, mostly near
    /// the thumb. They rise, drift and fade.</summary>
    private void UpdateSparks(double dt, bool beat)
    {
        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            s.Vy += 14 * dt; // they slow as they rise
            s.Life -= dt * 1.4;
            if (s.Life <= 0 || s.Y < -4) _sparks.RemoveAt(i);
            else _sparks[i] = s;
        }
        if (!Flames || !IsPlaying || _activity < 0.3 || Maximum <= 0) return;

        var mid = ActualHeight / 2;
        var thumbX = Left + (Right - Left) * Math.Clamp(CurrentPosition() / Maximum, 0, 1);
        if (thumbX - Left < 30) return;
        var count = beat ? 3 + (int)(_level * 4) : Random.Shared.NextDouble() < dt * (2 + 6 * _level) ? 1 : 0;
        for (var n = 0; n < count && _sparks.Count < 40; n++)
        {
            var back = Math.Pow(Random.Shared.NextDouble(), 2) * Math.Min(160, thumbX - Left);
            _sparks.Add(new Spark
            {
                X = thumbX - 4 - back,
                Y = mid - 2 - Random.Shared.NextDouble() * 6,
                Vx = (Random.Shared.NextDouble() - 0.5) * 18,
                Vy = -(22 + Random.Shared.NextDouble() * 30 + 30 * _level),
                Life = 0.6 + Random.Shared.NextDouble() * 0.5,
                Size = 0.7 + Random.Shared.NextDouble() * 0.9,
            });
        }
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
