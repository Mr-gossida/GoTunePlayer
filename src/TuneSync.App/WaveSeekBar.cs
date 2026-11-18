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
        new FrameworkPropertyMetadata(SeekBarStyle.Wave, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WaveSeekBar)d).OnLookChanged()));

    private void OnLookChanged()
    {
        _sparks.Clear();
        _licks.Clear();
        _stars.Clear();
    }

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
    private struct Spark { public double X, Y, Vx, Vy, Life, Size, Age; }

    private bool Flames => Look == SeekBarStyle.Flame;
    private double _fireTime;
    private double _fireLevel;
    private readonly List<Spark> _licks = [];

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
        // Hidden (mini player in use) or minimized: no waves to draw.
        OnScreen.Watch(this, () => { if (OnScreen.Check(this)) UpdateAnimationState(); else StopAnimating(); });
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

        if (Look == SeekBarStyle.Bars)
        {
            // The song's loudness as bars: faint ahead of the thumb, the played part in color.
            DrawLevelBars(dc, mid, thumbX, accent);
        }
        else if (thumbX < Right)
        {
            var trackPen = new Pen(TrackBrush, LineThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(trackPen, new Point(thumbX, mid), new Point(Right, mid));
        }

        // The wave's colors drift around the cover's color - a warmer and a cooler neighbor
        // that slowly trade places - and brighten on each beat.
        var drift = 22 * Math.Sin(_phase * 0.35);
        var cool = Shift(accent, -38 + drift, 1.0);
        var warm = Shift(accent, 42 + drift, 1.12 + 0.25 * _flash);
        var thumbColor = Look switch
        {
            SeekBarStyle.Flame => Color.FromRgb(0xFF, 0x9A, 0x2E),
            SeekBarStyle.Electric => ElectricBlue,
            _ => accent,
        };

        if (thumbX > Left)
        {
            var maxHeight = mid - 2;
            // Height follows the music: a gentle ripple when quiet, a surge when loud.
            var fadeIn = Math.Min(1, (thumbX - Left) / 60);
            if (Look == SeekBarStyle.Neon)
            {
                DrawNeon(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Bars)
            {
                // Drawn above with the unplayed part.
            }
            else if (Look == SeekBarStyle.Electric)
            {
                DrawElectric(dc, mid, thumbX, maxHeight + 4, fadeIn);
            }
            else if (Look == SeekBarStyle.Sparkle)
            {
                DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
                DrawStars(dc, warm);
            }
            else if (Flames)
            {
                // Flames may rise a little above the bar's own strip.
                var flameRoom = maxHeight + FlameOverflow;
                var amplitude = FireAmplitude(thumbX);
                if (amplitude > 0.3) DrawFire(dc, mid, thumbX, flameRoom, amplitude);
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

    // ---- 네온 ----

    /// <summary>The played line as a glowing tube: its glow widens and brightens with the music and
    /// flares on each beat, and each beat sends a ring out from the thumb. The glow is a few wide,
    /// faint strokes under the line (a blur effect would be redrawn by the GPU every frame).</summary>
    private void DrawNeon(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        var energy = _activity * (0.35 + 0.65 * _level) + 0.5 * _flash;
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(Left, 0),
            EndPoint = new Point(Math.Max(Left + 1, thumbX), 0),
            GradientStops = { new GradientStop(cool, 0), new GradientStop(accent, 0.55), new GradientStop(warm, 1) },
        };
        brush.Freeze();
        for (var layer = 3; layer >= 1; layer--)
        {
            var width = LineThickness + layer * (2.2 + 3.2 * energy);
            var glow = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.PushOpacity(Math.Clamp((0.09 + 0.12 * energy) * (4 - layer) / 2.0, 0, 0.6));
            dc.DrawLine(glow, new Point(Left, mid), new Point(thumbX, mid));
            dc.Pop();
        }
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        // A white-hot core along the tube.
        var core = new Pen(new SolidColorBrush(Color.FromArgb((byte)(90 + 120 * Math.Min(1, energy)), 0xFF, 0xFF, 0xFF)), 1.2);
        dc.DrawLine(core, new Point(Left + 2, mid), new Point(thumbX, mid));

        foreach (var (distance, strength) in _ripples)
        {
            var r = ThumbRadius + 2 + distance * 0.17;
            var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, strength * 1.8)), warm.R, warm.G, warm.B)), 1.5);
            dc.DrawEllipse(null, ring, new Point(thumbX, mid), r, r);
        }
    }

    // ---- 파형 막대 ----

    private const double BarPitch = 4, BarThickness = 2.4;

    /// <summary>The whole song's loudness as thin bars mirrored around the line (from
    /// <see cref="Waveform"/>; a steady made-up pattern while it's being analyzed). Ahead of the
    /// thumb they stay faint; the played ones take the cover's colors, and those right behind the
    /// thumb bounce with what's playing now.</summary>
    private void DrawLevelBars(DrawingContext dc, double mid, double thumbX, Color accent)
    {
        var maxHeight = mid - 2;
        var waveform = Waveform;
        var played = new StreamGeometry();
        var ahead = new StreamGeometry();
        using (var p = played.Open())
        using (var a = ahead.Open())
        {
            var i = 0;
            for (var x = Left; x <= Right; x += BarPitch, i++)
            {
                var at = Maximum > 0 ? (x - Left) / Math.Max(1, Right - Left) * Maximum : 0;
                var level = waveform is not null ? waveform.LevelAt(at) : 0.25 + 0.5 * Hash(i);
                var h = maxHeight * (0.12 + 0.88 * level);
                var isPlayed = x <= thumbX;
                if (isPlayed)
                {
                    // Close behind the thumb, the bars jump with the music playing now.
                    var near = SmoothStep(Math.Clamp(1 - (thumbX - x) / 70, 0, 1));
                    var bounce = 0.55 + 0.45 * Math.Sin(_phase * 3.1 + i * 1.7);
                    h *= 1 + near * _activity * (0.35 * _level * bounce + 0.5 * _flash);
                }
                h = Math.Clamp(h, 1, maxHeight + 3);
                var ctx = isPlayed ? p : a;
                ctx.BeginFigure(new Point(x - BarThickness / 2, mid - h), true, true);
                ctx.PolyLineTo([new Point(x + BarThickness / 2, mid - h), new Point(x + BarThickness / 2, mid + h), new Point(x - BarThickness / 2, mid + h)], false, false);
            }
        }
        played.Freeze();
        ahead.Freeze();

        dc.PushOpacity(0.45);
        dc.DrawGeometry(TrackBrush, null, ahead);
        dc.Pop();
        if (thumbX > Left)
        {
            var drift = 22 * Math.Sin(_phase * 0.35);
            var fill = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(Left, 0),
                EndPoint = new Point(Math.Max(Left + 1, thumbX), 0),
                GradientStops =
                {
                    new GradientStop(Shift(accent, -38 + drift, 1.0), 0),
                    new GradientStop(accent, 0.55),
                    new GradientStop(Shift(accent, 42 + drift, 1.12 + 0.25 * _flash), 1),
                },
            };
            fill.Freeze();
            dc.DrawGeometry(fill, null, played);
        }
    }

    // ---- 번개 ----

    private static readonly Color ElectricBlue = Color.FromRgb(0x8C, 0xD8, 0xFF);
    private int _boltSeed;
    private double _sinceBolt;

    /// <summary>A crackling bolt of lightning along the played part: a jagged line that redraws
    /// itself several times a second, wilder when loud, with forks that split off on beats.
    /// A pale blue glow under a white core.</summary>
    private void DrawElectric(DrawingContext dc, double mid, double thumbX, double room, double fadeIn)
    {
        var amplitude = room * fadeIn * (0.12 + 0.55 * _level * _activity + 0.35 * _flash);
        var bolt = new StreamGeometry();
        using (var g = bolt.Open())
        {
            g.BeginFigure(new Point(Left, mid), false, false);
            var i = 0;
            for (var x = Left + 7; x < thumbX; x += 7, i++)
            {
                var envelope = SmoothStep(Math.Min(1, (x - Left) / 24)) * SmoothStep(Math.Min(1, (thumbX - x) / 14));
                var jag = (Hash(i * 7 + _boltSeed * 131) - 0.5) * 2;
                g.LineTo(new Point(x, mid + jag * amplitude * envelope), true, false);
            }
            g.LineTo(new Point(thumbX, mid), true, false);

            // Forks: short branches off the bolt, more and longer on a beat.
            var forks = (int)(1 + 4 * _flash + 2 * _level);
            for (var f = 0; f < forks && thumbX - Left > 40; f++)
            {
                var fx = Left + 20 + Hash(f * 13 + _boltSeed * 17) * (thumbX - Left - 30);
                var fy = mid + (Hash(f * 29 + _boltSeed) - 0.5) * amplitude;
                var up = Hash(f * 5 + _boltSeed * 3) < 0.5 ? -1 : 1;
                var length = room * (0.5 + 0.8 * _flash) * (0.5 + Hash(f + _boltSeed * 7));
                g.BeginFigure(new Point(fx, fy), false, false);
                g.LineTo(new Point(fx + length * 0.4, fy + up * length * 0.5), true, false);
                g.LineTo(new Point(fx + length * 0.55, fy + up * length * 0.95), true, false);
            }
        }
        bolt.Freeze();

        var glowAlpha = (byte)(40 + 70 * Math.Min(1, _level + _flash));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(glowAlpha, 0x4A, 0x9C, 0xFF)), 6) { LineJoin = PenLineJoin.Round }, bolt);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xC0, ElectricBlue.R, ElectricBlue.G, ElectricBlue.B)), 2.6) { LineJoin = PenLineJoin.Round }, bolt);
        dc.DrawGeometry(null, new Pen(Brushes.White, 1.1) { LineJoin = PenLineJoin.Round }, bolt);
    }

    // ---- 별빛 ----

    private readonly List<Spark> _stars = [];

    /// <summary>Little four-pointed stars that twinkle as they drift up off the played line.</summary>
    private void DrawStars(DrawingContext dc, Color tint)
    {
        foreach (var star in _stars)
        {
            // Fade in, twinkle, fade out.
            var age = star.Age;
            var fade = Math.Min(1, age / 0.25) * Math.Clamp(star.Life / 0.5, 0, 1);
            var twinkle = 0.6 + 0.4 * Math.Sin(age * 9 + star.X);
            var s = star.Size * (0.7 + 0.3 * twinkle);
            var color = Color.FromArgb((byte)(255 * fade * twinkle),
                (byte)((tint.R + 255 * 2) / 3), (byte)((tint.G + 255 * 2) / 3), (byte)((tint.B + 255 * 2) / 3));
            var shape = new StreamGeometry();
            using (var g = shape.Open())
            {
                double x = star.X, y = star.Y, w = s * 0.22;
                g.BeginFigure(new Point(x, y - s), true, true);
                g.PolyLineTo([
                    new Point(x + w, y - w), new Point(x + s, y), new Point(x + w, y + w), new Point(x, y + s),
                    new Point(x - w, y + w), new Point(x - s, y), new Point(x - w, y - w)], false, false);
            }
            shape.Freeze();
            dc.DrawGeometry(new SolidColorBrush(color), null, shape);
        }
    }

    private void UpdateStars(double dt, bool beat)
    {
        for (var i = _stars.Count - 1; i >= 0; i--)
        {
            var s = _stars[i];
            s.Y += s.Vy * dt;
            s.Age += dt;
            s.Life -= dt;
            if (s.Life <= 0) _stars.RemoveAt(i);
            else _stars[i] = s;
        }
        if (Look != SeekBarStyle.Sparkle || !IsPlaying || _activity < 0.3 || Maximum <= 0) return;

        var mid = ActualHeight / 2;
        var thumbX = Left + (Right - Left) * Math.Clamp(CurrentPosition() / Maximum, 0, 1);
        if (thumbX - Left < 20) return;
        var count = beat ? 2 + (int)(_level * 4) : Random.Shared.NextDouble() < dt * (3 + 8 * _level) ? 1 : 0;
        for (var n = 0; n < count && _stars.Count < 36; n++)
        {
            // Anywhere along the played part, a few more toward the thumb.
            var back = Math.Pow(Random.Shared.NextDouble(), 1.4) * (thumbX - Left - 6);
            _stars.Add(new Spark
            {
                X = thumbX - 4 - back,
                Y = mid - 2 - Random.Shared.NextDouble() * (mid - 2),
                Vy = -(4 + Random.Shared.NextDouble() * 8),
                Life = 0.9 + Random.Shared.NextDouble() * 0.9,
                Size = 1.8 + Random.Shared.NextDouble() * 2.2 + (beat ? 1.2 : 0),
            });
        }
    }

    /// <summary>How far above the strip the flames may reach (they're drawn outside the bar).</summary>
    private const double FlameOverflow = 9;
    private const double TongueSpacing = 6.5;

    /// <summary>How tall the fire burns: follows the music, but through <see cref="_fireLevel"/>,
    /// which rises and falls more slowly than the meter, so the fire swells and settles instead of
    /// pumping with every beat.</summary>
    private double FireAmplitude(double thumbX)
    {
        var room = ActualHeight / 2 - 2 + FlameOverflow;
        var fadeIn = Math.Min(1, (thumbX - Left) / 60);
        return room * _activity * (0.42 + 0.58 * _fireLevel) * fadeIn * (1 + 0.12 * _flash);
    }

    /// <summary>The part of a tongue's flicker cycle where it stops stretching and its tip breaks off.</summary>
    private const double PinchAt = 0.72;

    /// <summary>A tongue's height through one flicker cycle (0..1): it stretches up slowly, then
    /// its tip pinches off and the rest drops back at once - the way a real flame flickers -
    /// rather than rising and falling evenly.</summary>
    private static double FlickerCycle(double p) => p < PinchAt
        ? 0.42 + 0.58 * SmoothStep(p / PinchAt)
        : 1 - 0.58 * (1 - Math.Pow(1 - (p - PinchAt) / (1 - PinchAt), 2));

    /// <summary>How many flicker cycles tongue <paramref name="i"/> goes through per unit of fire time.</summary>
    private static double FlickerRate(int i) => 2.3 * (0.8 + 0.4 * Hash(i + 97));

    /// <summary>The draft at <paramref name="x"/>: one slowly shifting field all tongues lean in,
    /// so neighbors sway together.</summary>
    private static double Draft(double x, double t) => 0.55 * Math.Sin(t * 0.6 + x * 0.010) + 0.45 * Math.Sin(t * 1.4 - x * 0.022 + 1.1);

    /// <summary>Tongue <paramref name="i"/> at fire time <paramref name="t"/>: where it stands, how
    /// tall (<paramref name="h"/>, and <paramref name="full"/> at the top of its cycle), how wide and
    /// how far its tip leans. False past the thumb.</summary>
    private bool TongueAt(int i, double t, double endX, double room, double amplitude,
                          out double x, out double h, out double full, out double w, out double lean)
    {
        var seed = Hash(i);
        x = Left + i * TongueSpacing + (seed - 0.5) * 3;
        h = full = w = lean = 0;
        if (x > endX - 1) return false;

        var envelope = SmoothStep(Math.Min(1, (x - Left) / 18)) * SmoothStep(Math.Min(1, (endX - x) / 8));
        var nearThumb = 0.62 + 0.38 * SmoothStep(Math.Clamp(1 - (endX - x) / 160, 0, 1));
        // Taller and lower stretches that drift along the fire, so neighbors rise together.
        var body = 0.90 + 0.18 * Math.Sin((x - Left) * 0.045 - t * 1.9 + Math.Sin(t * 0.7) * 1.5)
                        + 0.06 * Math.Sin((x - Left) * 0.11 + t * 3.1);
        var cycle = t * FlickerRate(i) + seed;
        var g = FlickerCycle(cycle - Math.Floor(cycle));
        var shimmer = 1 + 0.05 * Math.Sin(t * 21 + seed * 60);
        full = amplitude * envelope * nearThumb * body * (0.78 + 0.22 * Hash(i + 31));
        h = full * g * shimmer;
        // A stretched tongue gets thinner; one that just dropped is short and broad.
        w = TongueSpacing * (1.6 + 0.8 * Hash(i + 31)) * (1.15 - 0.25 * g);
        var reach = h / room;
        lean = Draft(x, t) * w * 0.9 * (reach + 0.3) + Math.Sin(t * 7.3 + seed * 31) * 0.14 * w * reach;
        return true;
    }

    /// <summary>Fire along the played line, built tongue by tongue like a real flame front. Each
    /// tongue flickers in a cycle - it stretches up and thins, then its tip pinches off and rises
    /// away while the rest drops back (<see cref="FlickerCycle"/>) - all leaning in one shared
    /// draft, with taller stretches drifting along the fire. Three nested layers give the colour of
    /// real fire: a deep red-orange body, an orange-yellow middle and a pale hot core that stays
    /// steadier near the root. Louder music makes the fire grow and flicker faster; the tallest
    /// tongues burn right behind the thumb.</summary>
    private void DrawFire(DrawingContext dc, double mid, double endX, double room, double amplitude)
    {
        var t = _fireTime;
        var outer = new StreamGeometry();
        var middle = new StreamGeometry();
        var core = new StreamGeometry();
        using (var o = outer.Open())
        using (var m = middle.Open())
        using (var c = core.Open())
        {
            for (var i = 0; TongueAt(i, t, endX, room, amplitude, out var x, out var h, out var full, out var w, out var lean); i++)
            {
                if (h < 1.2) continue;
                Tongue(o, x, mid + 1, w, h, lean);
                Tongue(m, x + lean * 0.15, mid + 1, w * 0.62, h * 0.7, lean * 0.7);
                Tongue(c, x + lean * 0.1, mid + 1, w * 0.34, h * 0.24 + full * 0.12, lean * 0.4);
            }
        }
        outer.Freeze();
        middle.Freeze();
        core.Freeze();

        // A warm glow on the line under the fire.
        var glow = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(Left, 0),
            EndPoint = new Point(Math.Max(Left + 1, endX), 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0x70, 0x20), 0),
                new GradientStop(Color.FromArgb((byte)(0x50 + 0x40 * _flash), 0xFF, 0x80, 0x20), 0.85),
                new GradientStop(Color.FromArgb((byte)(0x70 + 0x50 * _flash), 0xFF, 0xB0, 0x40), 1),
            },
        };
        glow.Freeze();
        dc.DrawRectangle(glow, null, new Rect(Left, mid - 4, Math.Max(0, endX - Left), 6));

        dc.DrawGeometry(FireBrush(mid, room, [(0, 0xF0, 0xC8, 0x22, 0x08), (0.3, 0xE0, 0xFF, 0x52, 0x10), (0.65, 0x99, 0xFF, 0x94, 0x22), (1, 0x00, 0xFF, 0xD0, 0x60)]), null, outer);
        dc.DrawGeometry(FireBrush(mid, room * 0.75, [(0, 0xF5, 0xFF, 0xA8, 0x2E), (0.55, 0xB0, 0xFF, 0xC8, 0x48), (1, 0x00, 0xFF, 0xE8, 0x90)]), null, middle);
        dc.DrawGeometry(FireBrush(mid, room * 0.42, [(0, 0xFA, 0xFF, 0xFB, 0xE6), (0.6, 0xC0, 0xFF, 0xF0, 0xB0), (1, 0x00, 0xFF, 0xF4, 0xC0)]), null, core);
        DrawLicks(dc);
    }

    /// <summary>One teardrop flame: a wide root on the line narrowing to a leaning, pointed tip.</summary>
    private static void Tongue(StreamGeometryContext ctx, double x, double baseY, double width, double height, double lean)
    {
        var half = width / 2;
        var tip = new Point(x + lean, baseY - height);
        ctx.BeginFigure(new Point(x - half, baseY), isFilled: true, isClosed: true);
        ctx.BezierTo(new Point(x - half * 1.05, baseY - height * 0.38), new Point(x + lean * 0.55 - half * 0.45, baseY - height * 0.78), tip, true, true);
        ctx.BezierTo(new Point(x + lean * 0.55 + half * 0.45, baseY - height * 0.78), new Point(x + half * 1.05, baseY - height * 0.38), new Point(x + half, baseY), true, true);
    }

    private static Brush FireBrush(double baseY, double reach, (double Offset, byte A, byte R, byte G, byte B)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, baseY),
            EndPoint = new Point(0, baseY - Math.Max(1, reach)),
        };
        foreach (var (offset, a, r, g, b) in stops) brush.GradientStops.Add(new GradientStop(Color.FromArgb(a, r, g, b), offset));
        brush.Freeze();
        return brush;
    }

    /// <summary>A steady pseudo-random number in 0..1 for tongue <paramref name="i"/>.</summary>
    private static double Hash(int i)
    {
        var v = Math.Sin(i * 12.9898 + 78.233) * 43758.5453;
        return v - Math.Floor(v);
    }

    /// <summary>Flame tips that pinched off: small teardrops that keep rising in the draft,
    /// shrinking, turning yellow and fading.</summary>
    private void DrawLicks(DrawingContext dc)
    {
        foreach (var lick in _licks)
        {
            var k = lick.Life / (lick.Life + lick.Age); // 1 when it breaks off, 0 when gone
            var brush = new SolidColorBrush(Color.FromArgb((byte)(210 * Math.Sqrt(k)), 0xFF, (byte)(0x78 + 0x60 * (1 - k)), (byte)(0x20 + 0x30 * (1 - k))));
            var height = lick.Size * 1.7 * (0.45 + 0.55 * k);
            var half = lick.Size * (0.35 + 0.35 * k) / 2;
            var lean = lick.Vx * 0.08;
            double x = lick.X, y = lick.Y;
            var shape = new StreamGeometry();
            using (var g = shape.Open())
            {
                g.BeginFigure(new Point(x - half, y - height * 0.3), true, true);
                g.BezierTo(new Point(x - half, y - height * 0.65), new Point(x + lean * 0.5 - half * 0.3, y - height * 0.9), new Point(x + lean, y - height), true, true);
                g.BezierTo(new Point(x + lean * 0.5 + half * 0.3, y - height * 0.9), new Point(x + half, y - height * 0.65), new Point(x + half, y - height * 0.3), true, true);
                g.BezierTo(new Point(x + half, y + height * 0.05), new Point(x - half, y + height * 0.05), new Point(x - half, y - height * 0.3), true, true);
            }
            shape.Freeze();
            dc.DrawGeometry(brush, null, shape);
        }
    }

    /// <summary>Tongues whose flicker cycle passed the pinch since the last frame throw their tip
    /// off as a lick (most of them; some just drop back).</summary>
    private void SpawnLicks(double previousTime)
    {
        if (!Flames || !IsPlaying || _activity < 0.3 || Maximum <= 0) return;
        var mid = ActualHeight / 2;
        var thumbX = Left + (Right - Left) * Math.Clamp(CurrentPosition() / Maximum, 0, 1);
        var room = mid - 2 + FlameOverflow;
        var amplitude = FireAmplitude(thumbX);
        var t = _fireTime;
        for (var i = 0; _licks.Count < 40; i++)
        {
            if (!TongueAt(i, t, thumbX, room, amplitude, out var x, out _, out var full, out var w, out var lean)) break;
            var phase = Hash(i) - PinchAt;
            var crossed = Math.Floor(t * FlickerRate(i) + phase) > Math.Floor(previousTime * FlickerRate(i) + phase);
            if (!crossed || full < 6 || Random.Shared.NextDouble() > 0.35 + 0.35 * _flash) continue;
            _licks.Add(new Spark
            {
                X = x + lean * 0.75,
                Y = mid + 1 - full * 0.72, // where the tip was
                Vx = Draft(x, t) * 14,
                Vy = -(room * (1.6 + Random.Shared.NextDouble())),
                Life = 0.10 + Random.Shared.NextDouble() * 0.10,
                // Only the tip breaks off: a small piece, never bigger than the tongue's top.
                Size = Math.Min(full * 0.2, w * 0.35),
            });
        }
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
        if (IsLoaded && OnScreen.Check(this) && (IsPlaying || _activity > 0.002 || _ripples.Count > 0 || _sparks.Count > 0 || _licks.Count > 0 || _stars.Count > 0)) StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        FrameTicker.Add(OnRendering);
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        FrameTicker.Remove(OnRendering);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = FrameTicker.Now;
        if (now == _lastFrame) return;
        var dt = _lastFrame == TimeSpan.Zero ? 0 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        var target = !IsPlaying ? 0 : Waveform is { } waveform ? waveform.LevelAt(CurrentPosition()) : FallbackLevel;
        _level += (target - _level) * Math.Min(1, dt * (target > _level ? 18 : 4));
        _baseline += (target - _baseline) * Math.Min(1, dt * 1.2);
        _activity += ((IsPlaying ? 1.0 : 0.0) - _activity) * Math.Min(1, dt * 3);

        // Louder music flows faster.
        _phase += dt * (1.4 + 2.4 * _level) * Math.Max(_activity, 0.15);
        // The fire grows and settles more slowly than the meter; it flickers faster when loud.
        _fireLevel += (target - _fireLevel) * Math.Min(1, dt * (target > _fireLevel ? 5 : 2));
        var previousFireTime = _fireTime;
        _fireTime += dt * (0.9 + 0.8 * _fireLevel + 0.8 * _flash) * Math.Max(_activity, 0.2);

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
        SpawnLicks(previousFireTime);
        UpdateStars(dt, beat);
        // The lightning redraws itself every so often - sooner when loud, at once on a beat.
        _sinceBolt += dt;
        if (beat || _sinceBolt > 0.11 - 0.06 * _level)
        {
            _sinceBolt = 0;
            _boltSeed++;
        }
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var (distance, strength) = _ripples[i];
            strength *= Math.Exp(-dt * 2.2);
            if (strength < 0.02) _ripples.RemoveAt(i);
            else _ripples[i] = (distance + dt * 150, strength);
        }

        if (!IsPlaying && _activity < 0.002 && _ripples.Count == 0 && _sparks.Count == 0 && _licks.Count == 0 && _stars.Count == 0)
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
        for (var i = _licks.Count - 1; i >= 0; i--)
        {
            var l = _licks[i];
            l.X += l.Vx * dt;
            l.Y += l.Vy * dt;
            l.Vy *= Math.Exp(-dt * 2); // the draft carries it up, ever more gently
            l.Life -= dt;
            l.Age += dt;
            if (l.Life <= 0) _licks.RemoveAt(i);
            else _licks[i] = l;
        }
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
