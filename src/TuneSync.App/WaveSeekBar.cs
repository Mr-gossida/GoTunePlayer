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
public sealed partial class WaveSeekBar : FrameworkElement
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
        _drops.Clear();
        _splashes.Clear();
        _droplets.Clear();
        _jets.Clear();
        _spouts.Clear();
        _spoutQueue.Clear();
        _rockets.Clear();
        _rocketQueue.Clear();
        _embers.Clear();
        ClearNature();
        UpdateAnimationState();
        if (Look == SeekBarStyle.None) StopAnimating();
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

    /// <summary>The effects are drawn this much bigger than they were first tuned.</summary>
    private const double EffectScale = 1.1;

    /// <summary>The gap between how an effect looks in quiet and in loud passages is this much
    /// wider than first tuned (+50%; was +15%, and the effects barely told quiet from loud).</summary>
    private const double RangeBoost = 1.5;

    /// <summary>Waves and bars may reach this far past the strip's own half height (was 3: loud
    /// passages were cut flat right at the bar's top edge).</summary>
    private const double Overshoot = 8;

    /// <summary><paramref name="value"/> as it is up to <paramref name="knee"/>, then bending
    /// smoothly toward <paramref name="limit"/> without ever reaching it - so a loud peak comes
    /// out rounded, not sliced flat as a hard limit leaves it.</summary>
    private static double SoftCap(double value, double knee, double limit)
    {
        if (value <= knee || limit <= knee) return Math.Min(value, Math.Max(knee, limit));
        return knee + (limit - knee) * Math.Tanh((value - knee) / (limit - knee));
    }

    /// <summary>An effect's size at loudness <paramref name="level"/> (0..1): tuned as
    /// <paramref name="floor"/> in silence up to floor + <paramref name="span"/> when loud, with
    /// that range widened by <see cref="RangeBoost"/> around its middle (quiet gets quieter, loud
    /// louder) and the whole scaled by <see cref="EffectScale"/>.</summary>
    private static double Loud(double floor, double span, double level) =>
        EffectScale * Math.Max(0, floor + span * (0.5 + (level - 0.5) * RangeBoost));

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

    /// <summary>Beats so far, which steps the fountain's and the fireworks' patterns along, and
    /// how far the last one jumped above the recent average (0.12 and up; 0.25+ is a strong one).</summary>
    private int _beatCount;
    private double _beatStrength;

    /// <summary>A beat well above the usual: it sets off a whole volley at once.</summary>
    private const double StrongBeat = 0.25;

    /// <summary>Long enough without a beat that the music has no clear pulse here: the fountain and
    /// the fireworks then go on at random, so they don't stand still.</summary>
    private bool NoPulse => _sinceBeat > 1.4;

    private readonly List<(double Distance, double Strength)> _ripples = [];

    // _flash: a beat's brief brightening (decays fast). _sparks: embers rising off the flames.
    private double _flash;
    private readonly List<Spark> _sparks = [];
    private struct Spark { public double X, Y, Vx, Vy, Life, Size, Age, Hue; }

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
            // The song's loudness as bars - only where the music has played: ahead lies the plain line.
            if (thumbX < Right)
            {
                var trackPen = new Pen(TrackBrush, LineThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(trackPen, new Point(thumbX, mid), new Point(Right, mid));
            }
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
        var thumbColor = Look == SeekBarStyle.Flame ? Color.FromRgb(0xFF, 0x9A, 0x2E) : accent;

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
            else if (Look == SeekBarStyle.Fountain)
            {
                DrawFountain(dc, mid, thumbX, maxHeight + FountainOverflow, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Fireworks)
            {
                DrawFireworks(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.None)
            {
                // just the line: nothing moves, nothing is redrawn but the thumb
                DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Rain)
            {
                DrawRain(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Aurora)
            {
                DrawAurora(dc, mid, thumbX, maxHeight + AuroraOverflow, fadeIn, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Fireflies)
            {
                DrawFireflies(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Electric)
            {
                DrawElectric(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Plasma)
            {
                DrawPlasma(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Petals)
            {
                DrawPetals(dc, mid, thumbX, cool, accent, warm);
            }
            else if (Look == SeekBarStyle.Notes)
            {
                DrawNotes(dc, mid, thumbX, cool, accent, warm);
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
                var amplitude = maxHeight * _activity * Loud(0.18, 0.82, _level) * fadeIn;
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
                        y += maxHeight * EffectScale * strength * Math.Exp(-d * d) * envelope;
                    }
                }

                ctx.LineTo(new Point(x, mid - SoftCap(y, maxHeight * 0.8, maxHeight + Overshoot)), true, true);
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
        var energy = _activity * Loud(0.35, 0.65, _level) + 0.5 * EffectScale * _flash;
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
    /// <summary>The played part's loudness as bars mirrored around the line, drawn as the thumb
    /// passes: each bar springs up out of the line (a little past its height, then settles) just
    /// behind the thumb. The song's quiet and loud passages are pulled further apart than their
    /// plain loudness, and the bars nearest the thumb keep jumping with what is playing now -
    /// higher with the music's level and on every beat.</summary>
    private void DrawLevelBars(DrawingContext dc, double mid, double thumbX, Color accent)
    {
        var maxHeight = mid - 2;
        var waveform = Waveform;
        var played = new StreamGeometry();
        using (var p = played.Open())
        {
            var i = 0;
            for (var x = Left; x <= thumbX; x += BarPitch, i++)
            {
                var at = Maximum > 0 ? (x - Left) / Math.Max(1, Right - Left) * Maximum : 0;
                var level = waveform is not null ? waveform.LevelAt(at) : 0.25 + 0.5 * Hash(i);
                // more contrast: quiet passages lower, loud ones as high as ever
                var h = maxHeight * Loud(0.06, 0.94, Math.Pow(level, 1.6));
                var behind = thumbX - x;
                // springing up as the thumb passes (ease-out-back over ~18 px)
                var reveal = Math.Clamp(behind / 18, 0, 1);
                const double c1 = 1.70158, c3 = c1 + 1;
                h *= 1 + c3 * Math.Pow(reveal - 1, 3) + c1 * Math.Pow(reveal - 1, 2);
                // the bars just behind the thumb jump with the music playing now
                var near = SmoothStep(Math.Clamp(1 - behind / 110, 0, 1));
                var bounce = 0.5 + 0.5 * Math.Sin(_phase * 3.7 + i * 1.9);
                h *= 1 + near * _activity * (0.75 * Loud(0, 1, _level) * bounce + 0.9 * EffectScale * _flash);
                h = Math.Max(0.8, SoftCap(h, maxHeight * 0.8, maxHeight + Overshoot + 2));
                p.BeginFigure(new Point(x - BarThickness / 2, mid - h), true, true);
                p.PolyLineTo([new Point(x + BarThickness / 2, mid - h), new Point(x + BarThickness / 2, mid + h), new Point(x - BarThickness / 2, mid + h)], false, false);
            }
        }
        played.Freeze();

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

    // ---- 불꽃놀이 ----

    /// <summary>How far above the strip the fireworks may burst.</summary>
    private const double FireworksOverflow = 10;

    /// <summary>Rockets climbing (X, Y, Vy; Hue picks the color), and the sparks of the bursts
    /// (X, Y, Vx, Vy, Life left, Size, Age, Hue).</summary>
    private readonly List<Spark> _rockets = [], _embers = [];

    /// <summary>Fireworks over the played part: rockets launched on the beats (and now and then
    /// between them, more often when it's loud) climb from the line, burst into a ring of sparks
    /// in the cover's colors that drift down and fade - bigger, higher bursts when the music is
    /// louder.</summary>
    private void DrawFireworks(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        Color Tint(double hue) => hue < 0.34 ? cool : hue < 0.67 ? accent : warm;

        foreach (var rocket in _rockets)
        {
            var tint = Mix(Tint(rocket.Hue), Colors.White, 0.5);
            DrawStreak(dc, new Point(rocket.X, rocket.Y + 7), new Point(rocket.X, rocket.Y),
                Color.FromArgb(0xC0, tint.R, tint.G, tint.B), 1.2, roundHead: false);
            dc.DrawEllipse(Brushes.White, null, new Point(rocket.X, rocket.Y), 1.1, 1.1);
        }
        foreach (var ember in _embers)
        {
            var left = Math.Clamp(ember.Life / 0.35, 0, 1);       // fades out over its last moments
            var twinkle = ember.Age > 0.4 ? 0.65 + 0.35 * Math.Sin(ember.Age * 40 + ember.Hue * 50) : 1;
            // white-hot at first (in steps of a tenth, so the colors - and their brushes - are few)
            var tint = Mix(Tint(ember.Hue), Colors.White, Math.Round(Math.Max(0, 0.6 - ember.Age * 1.5) * 10) / 10);
            var a = (byte)(255 * left * twinkle);
            // a short streak along its flight
            var tail = new Point(ember.X - ember.Vx * 0.035, ember.Y - ember.Vy * 0.035);
            DrawStreak(dc, tail, new Point(ember.X, ember.Y), Color.FromArgb(a, tint.R, tint.G, tint.B), ember.Size);
        }
    }

    private void UpdateFireworks(double dt, bool beat)
    {
        const double gravity = 140;
        for (var i = _embers.Count - 1; i >= 0; i--)
        {
            var e = _embers[i];
            var drag = Math.Exp(-dt * 2.2);
            e.Vx *= drag;
            e.Vy = e.Vy * drag + gravity * 0.35 * dt;
            e.X += e.Vx * dt;
            e.Y += e.Vy * dt;
            e.Life -= dt;
            e.Age += dt;
            if (e.Life <= 0) _embers.RemoveAt(i);
            else _embers[i] = e;
        }
        var loud = Loud(0, 1, _level);
        for (var i = _rockets.Count - 1; i >= 0; i--)
        {
            var r = _rockets[i];
            r.Y += r.Vy * dt;
            r.Vy += gravity * dt;
            if (r.Vy < -3) { _rockets[i] = r; continue; }
            // the top of its climb: burst
            _rockets.RemoveAt(i);
            var count = (int)(12 + 14 * r.Size);
            var speed = 24 + 34 * r.Size;
            // (260, was 180: a fan of five bursts at once)
            for (var n = 0; n < count && _embers.Count < 260; n++)
            {
                var angle = 2 * Math.PI * (n + Random.Shared.NextDouble() * 0.5) / count;
                var s = speed * (0.75 + 0.35 * Random.Shared.NextDouble());
                _embers.Add(new Spark
                {
                    X = r.X, Y = r.Y,
                    Vx = Math.Cos(angle) * s, Vy = Math.Sin(angle) * s * 0.75,
                    Life = 0.55 + 0.45 * Random.Shared.NextDouble() + 0.3 * r.Size,
                    Size = (1.0 + 0.6 * Random.Shared.NextDouble()) * EffectScale,
                    Hue = Random.Shared.NextDouble() < 0.75 ? r.Hue : Random.Shared.NextDouble(),
                });
            }
        }
        if (Look != SeekBarStyle.Fireworks || !IsPlaying || _activity < 0.3 || Maximum <= 0) return;

        var mid = ActualHeight / 2;
        var thumbX = Left + (Right - Left) * Math.Clamp(CurrentPosition() / Maximum, 0, 1);
        if (thumbX - Left < 30) return;
        var room = mid - 2 + FireworksOverflow;
        // how high a rocket bursts follows the music: low in quiet passages, near the top when
        // loud (curved so the two ends differ clearly)
        var lift = Math.Pow(Math.Clamp(loud, 0, 1.1), 1.4);
        var span = thumbX - Left - 20;

        // The show keeps time with the music: each beat sends up a volley in a pattern that moves
        // on with every beat - a single rocket, a pair, a chain of three going up one after the
        // other - and a strong beat a whole fan of them at once. Only where the music has no clear
        // pulse do rockets go up at random, as before.
        if (beat)
        {
            var strong = _beatStrength >= StrongBeat;
            var kind = strong ? Show.Fan : FireworksRound[_beatCount % FireworksRound.Length];
            var hue = Random.Shared.NextDouble(); // one color for the whole volley
            switch (kind)
            {
                case Show.Single:
                    _rocketQueue.Add((0, Math.Pow(Random.Shared.NextDouble(), 1.5) * Math.Min(120, span), 0.1, 0.2, hue));
                    break;
                case Show.Pair:
                    var at = Random.Shared.NextDouble() * Math.Min(60, span);
                    _rocketQueue.Add((0, at, 0.05, 0.15, hue));
                    _rocketQueue.Add((0, at + Math.Min(70, span * 0.5), 0.05, 0.15, hue));
                    break;
                case Show.Chain:
                    for (var k = 0; k < 3; k++) // stepping back from the thumb, each a little higher
                        _rocketQueue.Add((0.09 * k, Math.Min(span, 15 + 45 * k), -0.05 + 0.08 * k, 0.1, hue));
                    break;
                default: // a fan across the played part, all at once, high
                    var fan = Math.Clamp((int)(span / 50), 2, 5);
                    for (var k = 0; k < fan; k++)
                        _rocketQueue.Add((0, span * (k + 0.5) / fan * Math.Min(1, 260 / Math.Max(1, span)), 0.15, 0.35, hue));
                    break;
            }
        }
        for (var i = _rocketQueue.Count - 1; i >= 0; i--)
        {
            var q = _rocketQueue[i];
            q.Wait -= dt;
            if (q.Wait > 0) { _rocketQueue[i] = q; continue; }
            _rocketQueue.RemoveAt(i);
            if (_rockets.Count < MaxRockets) Launch(thumbX - 6 - Math.Clamp(q.Back, 0, Math.Max(0, span)), q.Higher, q.Bigger, q.Hue);
        }
        if (NoPulse && Random.Shared.NextDouble() < dt * Loud(0.25, 2.2, _level) && _rockets.Count < MaxRockets)
            Launch(thumbX - 6 - Random.Shared.NextDouble() * span, 0, 0, Random.Shared.NextDouble());

        void Launch(double x, double higher, double bigger, double hue)
        {
            // every rocket a bit different
            var height = room * Math.Clamp((0.3 + 0.7 * lift) * (0.82 + 0.36 * Random.Shared.NextDouble()) + higher, 0.3, 1.15);
            _rockets.Add(new Spark
            {
                X = x,
                Y = mid - 1,
                Vy = -Math.Sqrt(2 * gravity * height + 3 * 3), // bursts at the top of its climb, that high
                Size = Math.Clamp(0.25 + 0.75 * loud + bigger, 0.2, 1.2), // how big it bursts
                Hue = hue,
            });
        }
    }

    /// <summary>The most rockets in the air at once (a fan sends up five).</summary>
    private const int MaxRockets = 9;

    private enum Show { Single, Pair, Chain, Fan }

    /// <summary>The fireworks' patterns, one per beat, round and round.</summary>
    private static readonly Show[] FireworksRound = [Show.Single, Show.Pair, Show.Single, Show.Chain];

    /// <summary>Rockets waiting to go up: how long until they do, how far back from the thumb,
    /// how much higher and bigger than usual they burst, and their color.</summary>
    private readonly List<(double Wait, double Back, double Higher, double Bigger, double Hue)> _rocketQueue = [];

    // ---- 분수 ----

    /// <summary>How far above the strip the fountain may throw its water.</summary>
    private const double FountainOverflow = 8;

    /// <summary>Gravity on the droplets, in pixels a second per second.</summary>
    private const double FountainGravity = 320;

    /// <summary>Droplets in the air. X, Y: where; Vx, Vy: their speed; Size; Age; Life: a random
    /// 0..1 that picks the tint.</summary>
    private readonly List<Spark> _jets = [];

    /// <summary>Jets that are spouting. X: where on the line; Age, Life: how long it has run and
    /// how long it runs; Size: how strong it is (1 = normal).</summary>
    private readonly List<Spark> _spouts = [];

    /// <summary>How far along its short life a jet is, as its strength: it surges up, holds, then
    /// dies down to nothing.</summary>
    private static double SpoutEnvelope(Spark s)
    {
        var t = Math.Clamp(s.Age / s.Life, 0, 1);
        return Math.Min(1, t / 0.18) * Math.Clamp((1 - t) / 0.4, 0, 1);
    }

    /// <summary>A fountain show along the played part: jets spring up at random spots, throw
    /// droplets that arc over and fall back onto the line, then die down and vanish while new ones
    /// spring up elsewhere. Louder music: more jets, higher and fuller; each beat sets off a few at
    /// once. The water takes the cover's colors, lightened, with a short streak behind each drop.</summary>
    private void DrawFountain(DrawingContext dc, double mid, double thumbX, double room, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);

        // a soft glow where each jet leaves the line
        var strength = Math.Min(1, _activity * Loud(0.2, 0.8, _level) + 0.6 * _flash);
        foreach (var spout in _spouts)
        {
            var s = strength * SpoutEnvelope(spout);
            if (s <= 0.05) continue;
            // mapped in pixels: a brush mapped to the ellipse's own box, whose size changes every
            // frame, is rebuilt by the GPU every frame (CLAUDE.md: per-frame drawing)
            var center = new Point(spout.X, mid - 1);
            double rx = 7 + 5 * s, ry = 3 + 3 * s;
            var glow = new RadialGradientBrush(Color.FromArgb((byte)(120 * s), warm.R, warm.G, warm.B), Color.FromArgb(0, warm.R, warm.G, warm.B))
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = rx,
                RadiusY = ry,
            };
            glow.Freeze();
            dc.DrawEllipse(glow, null, center, rx, ry);
        }

        foreach (var drop in _jets)
        {
            // each drop's own tint (in sixths, so the colors - and their brushes - are few)
            var tint = Mix(drop.Life < 0.5 ? accent : warm, Colors.White, 0.35 + 0.3 * Math.Round(drop.Life * 6) / 6);
            // fades as it nears the line again, and in its first moment
            var fade = Math.Clamp((mid - drop.Y) / 6, 0, 1) * Math.Min(1, drop.Age / 0.05);
            if (fade <= 0.02) continue;
            var tail = new Point(drop.X - drop.Vx * 0.03, drop.Y - drop.Vy * 0.03);
            DrawStreak(dc, tail, new Point(drop.X, drop.Y), Color.FromArgb((byte)(200 * fade), tint.R, tint.G, tint.B), drop.Size);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(235 * fade), 0xFF, 0xFF, 0xFF)), null,
                new Point(drop.X, drop.Y), drop.Size * 0.55, drop.Size * 0.55);
        }
    }

    private void UpdateFountain(double dt, bool beat)
    {
        var mid = ActualHeight / 2;
        for (var i = _jets.Count - 1; i >= 0; i--)
        {
            var d = _jets[i];
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            d.Vy += FountainGravity * dt;
            d.Age += dt;
            if (d.Y > mid + 1 && d.Vy > 0) _jets.RemoveAt(i);
            else _jets[i] = d;
        }
        var thumbX = Left + (Right - Left) * Math.Clamp(CurrentPosition() / Maximum, 0, 1);
        // Jets run out on their own; one left past the thumb (after seeking back) goes at once.
        for (var i = _spouts.Count - 1; i >= 0; i--)
        {
            var s = _spouts[i];
            s.Age += dt;
            if (s.Age >= s.Life || s.X > thumbX - 4) _spouts.RemoveAt(i);
            else _spouts[i] = s;
        }
        if (Look != SeekBarStyle.Fountain || !IsPlaying || _activity < 0.3 || Maximum <= 0) return;

        if (thumbX - Left < 16) return;
        var room = mid - 2 + FountainOverflow;
        var loud = Loud(0, 1, _level);

        // The show keeps time with the music: each beat sets off a volley in a pattern, and the
        // pattern moves on with every beat (odd jets, then even ones, a wave running right, one
        // running left, from the middle out...), a strong beat sets them all off at once. Only
        // where the music has no clear pulse do jets spring up at random, as before.
        if (beat) QueueVolley(thumbX);
        for (var i = _spoutQueue.Count - 1; i >= 0; i--)
        {
            var (wait, at, strong) = _spoutQueue[i];
            wait -= dt;
            if (wait > 0) { _spoutQueue[i] = (wait, at, strong); continue; }
            _spoutQueue.RemoveAt(i);
            if (_spouts.Count < MaxSpouts) SpawnSpoutAt(Left + 8 + at * (thumbX - Left - 16), strong);
        }
        if (NoPulse && Random.Shared.NextDouble() < dt * Loud(1.2, 5, _level) && _spouts.Count < MaxSpouts)
            SpawnSpout(thumbX, strong: false);

        // Each jet throws water while it lives, more and higher at its peak.
        var lift = Math.Pow(Math.Clamp(loud, 0, 1.1), 1.2);
        foreach (var spout in _spouts)
        {
            var env = SpoutEnvelope(spout);
            if (env <= 0.02) continue;
            var height = room * (0.3 + 0.7 * lift) * spout.Size * (0.35 + 0.65 * env);
            var rate = dt * Loud(9, 26, _level) * env;
            var count = (Random.Shared.NextDouble() < rate - Math.Floor(rate) ? 1 : 0) + (int)rate;
            for (var n = 0; n < count; n++)
                Throw(spout.X, mid, height, spread: 14, size: 0.85);
        }
    }

    /// <summary>The most jets spouting at once.</summary>
    private const int MaxSpouts = 9;

    /// <summary>Jets waiting to spring up, a moment apart so a volley runs along the bar: how long
    /// until it does, where (a share of the played part, so it stays put as the thumb moves on),
    /// and whether it's from a strong beat.</summary>
    private readonly List<(double Wait, double At, bool Strong)> _spoutQueue = [];

    private enum Volley { Odd, Even, SweepRight, SweepLeft, CenterOut, All }

    /// <summary>The fountain's patterns, one per beat, round and round: two beats of jets taking
    /// turns, then the waves, then everything.</summary>
    private static readonly Volley[] FountainRound =
        [Volley.Odd, Volley.Even, Volley.Odd, Volley.Even, Volley.SweepRight, Volley.SweepLeft, Volley.CenterOut, Volley.All];

    /// <summary>Lines up this beat's volley: evenly spaced jets along the played part, the ones
    /// the pattern picks, each after its own short wait.</summary>
    private void QueueVolley(double thumbX)
    {
        var strong = _beatStrength >= StrongBeat;
        var kind = strong ? Volley.All : FountainRound[_beatCount % FountainRound.Length];
        var slots = Math.Clamp((int)((thumbX - Left) / 34), 3, 7);
        var middle = (slots - 1) / 2.0;
        for (var j = 0; j < slots; j++)
        {
            var (on, wait) = kind switch
            {
                Volley.Odd => (j % 2 == 0, 0.0),
                Volley.Even => (j % 2 == 1, 0.0),
                Volley.SweepRight => (true, 0.055 * j),
                Volley.SweepLeft => (true, 0.055 * (slots - 1 - j)),
                Volley.CenterOut => (true, 0.07 * Math.Abs(j - middle)),
                _ => (true, 0.0),
            };
            if (on) _spoutQueue.Add((wait, (j + 0.5) / slots, strong || kind == Volley.All));
        }
    }

    /// <summary>A beat's jet at <paramref name="x"/>: up at once and short-lived, so the jets
    /// pulse with the beats rather than blur into one another.</summary>
    private void SpawnSpoutAt(double x, bool strong) => _spouts.Add(new Spark
    {
        X = x,
        Life = 0.42 + Random.Shared.NextDouble() * 0.12,
        Size = strong ? 1.15 + 0.15 * Random.Shared.NextDouble() : 0.95 + 0.15 * Random.Shared.NextDouble(),
        Age = 0.06,
    });

    /// <summary>Starts a jet at a random spot on the played part, not right on top of another one.</summary>
    private void SpawnSpout(double thumbX, bool strong)
    {
        var span = thumbX - Left - 12;
        if (span <= 0) return;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var x = Left + 6 + Random.Shared.NextDouble() * span;
            var clear = true;
            foreach (var s in _spouts)
                if (Math.Abs(s.X - x) < 18) { clear = false; break; }
            if (!clear) continue;
            _spouts.Add(new Spark
            {
                X = x,
                Life = 0.7 + Random.Shared.NextDouble() * 1.1,
                Size = strong ? 1.05 + 0.2 * Random.Shared.NextDouble() : 0.7 + 0.35 * Random.Shared.NextDouble(),
                // a beat's jet is up at once instead of surging up
                Age = strong ? 0.1 : 0,
            });
            return;
        }
    }

    /// <summary>One droplet from <paramref name="x"/>, to reach about <paramref name="height"/> pixels up.</summary>
    private void Throw(double x, double mid, double height, double spread, double size)
    {
        if (_jets.Count >= 220) return;
        var h = Math.Max(2, height * (0.7 + 0.5 * Random.Shared.NextDouble()));
        _jets.Add(new Spark
        {
            X = x + (Random.Shared.NextDouble() - 0.5) * 2,
            Y = mid - 1.5,
            Vx = (Random.Shared.NextDouble() - 0.5) * 2 * spread,
            Vy = -Math.Sqrt(2 * FountainGravity * h),
            Size = (size + 0.35 * Random.Shared.NextDouble()) * EffectScale,
            Life = Random.Shared.NextDouble(),
        });
    }

    /// <summary>A brush running from <paramref name="start"/> at the bar's left end to
    /// <paramref name="end"/> at the thumb.</summary>
    private LinearGradientBrush AlongBar(double thumbX, Color start, Color end)
    {
        var brush = new LinearGradientBrush(start, end, new Point(Left, 0), new Point(Math.Max(Left + 1, thumbX), 0))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    // ---- 빗방울 ----

    /// <summary>How far above the strip the raindrops start falling.</summary>
    private const double RainOverflow = 8;

    /// <summary>Falling raindrops. X, Y: the drop's tip; Vx, Vy: its speed; Size: its thickness;
    /// Life: a random 0..1 that picks its tint.</summary>
    private readonly List<Spark> _drops = [];

    /// <summary>Rings spreading on the line where a drop landed. X: where; Size: how wide the ring
    /// grows; Age, Life: how far along, and how long it lasts; Y: how strong it starts.</summary>
    private readonly List<Spark> _splashes = [];

    /// <summary>Tiny droplets thrown up by a landing drop; they arc and fall back.</summary>
    private readonly List<Spark> _droplets = [];

    /// <summary>Rain falling onto the played line as if onto water: thin slanting streaks that
    /// land with a small flash, send a flat ring (and a fainter second one) spreading along the
    /// line, and throw up a droplet or two. Louder music brings more, faster and heavier rain with
    /// wider rings; each beat lets a shower fall around the thumb.</summary>
    private void DrawRain(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);

        var ringColor = Mix(accent, Colors.White, 0.55);
        foreach (var splash in _splashes)
        {
            var p = Math.Clamp(splash.Age / splash.Life, 0, 1);
            var grow = 1 - (1 - p) * (1 - p);
            var alpha = splash.Y * (1 - p);
            if (p < 0.3)
            {
                // The moment of impact: a small bright spot on the line.
                dc.DrawEllipse(Solid(Color.FromArgb((byte)(230 * (1 - p / 0.3)), 0xFF, 0xFF, 0xFF)), null, new Point(splash.X, mid), 2.2, 1.6);
            }
            var rx = 1.5 + splash.Size * grow;
            dc.DrawEllipse(null, SolidPen(Color.FromArgb((byte)(255 * alpha), ringColor.R, ringColor.G, ringColor.B), 1.1),
                new Point(splash.X, mid), rx, rx * 0.32);
            if (p > 0.18)
            {
                var inner = rx * 0.55;
                dc.DrawEllipse(null, SolidPen(Color.FromArgb((byte)(150 * alpha), ringColor.R, ringColor.G, ringColor.B), 0.9),
                    new Point(splash.X, mid), inner, inner * 0.32);
            }
        }

        foreach (var drop in _droplets)
        {
            var a = (byte)(220 * Math.Clamp(drop.Life / 0.15, 0, 1));
            dc.DrawEllipse(Solid(Color.FromArgb(a, 0xFF, 0xFF, 0xFF)), null, new Point(drop.X, drop.Y), drop.Size, drop.Size);
        }

        foreach (var drop in _drops)
        {
            var tint = Mix(drop.Life < 0.5 ? cool : accent, Colors.White, 0.55);
            var length = 3 + drop.Vy * 0.065;
            var top = new Point(drop.X - drop.Vx / drop.Vy * length, drop.Y - length);
            DrawStreak(dc, top, new Point(drop.X, drop.Y), Color.FromArgb(0xE0, tint.R, tint.G, tint.B), drop.Size);
        }
    }

    private void UpdateRain(double dt, bool beat)
    {
        var mid = ActualHeight / 2;
        for (var i = _splashes.Count - 1; i >= 0; i--)
        {
            var s = _splashes[i];
            s.Age += dt;
            if (s.Age >= s.Life) _splashes.RemoveAt(i);
            else _splashes[i] = s;
        }
        for (var i = _droplets.Count - 1; i >= 0; i--)
        {
            var d = _droplets[i];
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            d.Vy += 260 * dt;
            d.Life -= dt;
            if (d.Life <= 0 || d.Y > mid + 1) _droplets.RemoveAt(i);
            else _droplets[i] = d;
        }
        var loud = Loud(0, 1, _level);
        for (var i = _drops.Count - 1; i >= 0; i--)
        {
            var d = _drops[i];
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            d.Vy += 120 * dt;
            if (d.Y < mid - 1)
            {
                _drops[i] = d;
                continue;
            }
            // Landed: a ring, and now and then a droplet or two thrown up.
            _drops.RemoveAt(i);
            if (_splashes.Count < 40)
                _splashes.Add(new Spark { X = d.X, Size = 3 + 2.5 * d.Size + 4 * loud, Life = 0.45 + 0.15 * loud, Y = Math.Min(1, 0.55 + 0.35 * loud) });
            var thrown = (Random.Shared.NextDouble() < 0.35 + 0.4 * loud ? 1 : 0) + (d.Size > 1.6 ? 1 : 0);
            for (var n = 0; n < thrown && _droplets.Count < 40; n++)
            {
                var side = Random.Shared.NextDouble() < 0.5 ? -1 : 1;
                _droplets.Add(new Spark
                {
                    X = d.X, Y = mid - 1,
                    Vx = side * (14 + 20 * Random.Shared.NextDouble()),
                    Vy = -(28 + 26 * Random.Shared.NextDouble()) * (0.75 + 0.45 * loud),
                    Life = 0.3 + 0.15 * Random.Shared.NextDouble(),
                    Size = 0.6 + 0.4 * Random.Shared.NextDouble(),
                });
            }
        }
        if (Look != SeekBarStyle.Rain || !IsPlaying || _activity < 0.3 || Maximum <= 0) return;

        var thumbX = Left + (Right - Left) * Math.Clamp(CurrentPosition() / Maximum, 0, 1);
        if (thumbX - Left < 24) return;
        var room = mid - 2 + RainOverflow;
        var count = (beat ? 3 + (int)Loud(0, 4, _level) : 0) + (Random.Shared.NextDouble() < dt * Loud(7, 30, _level) ? 1 : 0);
        for (var n = 0; n < count && _drops.Count < 50; n++)
        {
            // Anywhere over the played part; a beat's shower falls around the thumb.
            var back = beat && n < count - 1
                ? Math.Pow(Random.Shared.NextDouble(), 1.5) * Math.Min(140, thumbX - Left - 12)
                : Random.Shared.NextDouble() * (thumbX - Left - 12);
            _drops.Add(new Spark
            {
                X = thumbX - 6 - back,
                Y = mid - room - Random.Shared.NextDouble() * 8,
                Vx = 6,
                Vy = 70 + 80 * loud + 25 * Random.Shared.NextDouble(),
                Size = (1.0 + 0.6 * Random.Shared.NextDouble() + (beat ? 0.4 : 0)) * EffectScale,
                Life = Random.Shared.NextDouble(),
            });
        }
    }

    /// <summary><paramref name="a"/> moved toward <paramref name="b"/> by <paramref name="t"/> (0..1).</summary>
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ---- 입자 그리기 ----
    // A brush and a pen made for every particle in every frame (a couple of hundred of them, 30
    // times a second) had the GPU realize a new gradient for each, each time (CLAUDE.md:
    // per-frame drawing). Brushes and pens are kept instead, one per color - the alpha in steps of
    // 1/15, which a fade at 30 fps doesn't show - and a streak is a rectangle over a brush drawn
    // once, laid along the streak by a transform.

    /// <summary>How many brushes (or pens) of each kind are kept before starting afresh (the cover's
    /// colors change with every song).</summary>
    private const int MaxKept = 1024;

    private readonly Dictionary<uint, Brush> _streakBrushes = [];
    private readonly Dictionary<uint, Brush> _solidBrushes = [];
    private readonly Dictionary<(uint Color, double Thickness), Pen> _solidPens = [];

    private static Color Stepped(Color color) =>
        Color.FromArgb((byte)(Math.Round(color.A / 17.0) * 17), color.R, color.G, color.B);

    private static uint ColorKey(Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);

    private static T Kept<TKey, T>(Dictionary<TKey, T> kept, TKey key, Func<T> make) where TKey : notnull where T : Freezable
    {
        if (kept.TryGetValue(key, out var value)) return value;
        if (kept.Count >= MaxKept) kept.Clear();
        value = make();
        value.Freeze();
        return kept[key] = value;
    }

    private Brush Solid(Color color)
    {
        color = Stepped(color);
        return Kept(_solidBrushes, ColorKey(color), () => new SolidColorBrush(color));
    }

    private Pen SolidPen(Color color, double thickness)
    {
        var brush = Solid(color);
        return Kept(_solidPens, (ColorKey(Stepped(color)), thickness), () => new Pen(brush, thickness));
    }

    /// <summary>A streak <paramref name="width"/> across, clear at <paramref name="tail"/> and
    /// <paramref name="color"/> at <paramref name="head"/> - rounded there unless
    /// <paramref name="roundHead"/> is false.</summary>
    private void DrawStreak(DrawingContext dc, Point tail, Point head, Color color, double width, bool roundHead = true)
    {
        color = Stepped(color);
        if (color.A == 0) return;
        var along = head - tail;
        var length = along.Length;
        if (length > 0.05)
        {
            // clear at x = -1, the color at x = 0
            var brush = Kept(_streakBrushes, ColorKey(color), () => new LinearGradientBrush(
                Color.FromArgb(0, color.R, color.G, color.B), color, new Point(-1, 0), new Point(0, 0))
            {
                MappingMode = BrushMappingMode.Absolute,
            });
            // x runs along the streak (-1 at the tail, 0 at the head), y across it in pixels
            var across = new Vector(-along.Y, along.X) / length;
            var place = new MatrixTransform(along.X, along.Y, across.X, across.Y, head.X, head.Y);
            place.Freeze();
            dc.PushTransform(place);
            dc.DrawRectangle(brush, null, new Rect(-1, -width / 2, 1, width));
            dc.Pop();
        }
        if (roundHead) dc.DrawEllipse(Solid(color), null, head, width / 2, width / 2);
    }

    // ---- 오로라 ----

    /// <summary>How far above the strip the aurora may hang. It was 0 because, reaching past the
    /// bar, it ran under the player's controls and looked cut off; the seek bar now draws on top
    /// of them with room to spare, so it may rise a little.</summary>
    private const double AuroraOverflow = 8;

    /// <summary>Northern lights filling the strip over the played part, above and below the line.
    /// Like a real aurora it is a hanging curtain: a bright lower hem that sways up and down in slow
    /// waves, folds that drift sideways along the top edge, and fine vertical rays whose brightness
    /// ripples along the curtain in travelling pulses. A dimmer second curtain hangs behind, higher
    /// and out of step, for depth. Colors run through the cover's hues along the bar, shading to a
    /// neighboring hue toward the top. It rises with the music and flares on each beat.</summary>
    private void DrawAurora(DrawingContext dc, double mid, double thumbX, double room, double fadeIn, Color cool, Color accent, Color warm)
    {
        var t = _phase;
        var lift = _activity * Loud(0.22, 0.78, _level) + 0.4 * EffectScale * _flash;
        // (may be above the control's own top: the curtain rises past the bar by AuroraOverflow.
        // It was held at y = 1, which kept it inside the bar whatever the overflow said.)
        var top = mid - room;
        var bottom = Math.Max(mid + 1, ActualHeight - 1);
        var below = bottom - mid;
        // how full the curtain hangs: quiet - a thin band; loud - the whole strip
        var fill = fadeIn * Math.Min(1.1, lift * 1.25);

        double Envelope(double x) => SmoothStep(Math.Min(1, (x - Left) / 36)) * SmoothStep(Math.Clamp((thumbX - x) / 18, 0, 1));
        // the hem: below the line, swaying up and down in long slow waves
        double HemAt(double x, double phase, double raise)
        {
            var sway = 0.5 + 0.28 * Math.Sin(x * 0.026 - t * 0.55 + phase) + 0.17 * Math.Sin(x * 0.069 + t * 1.2 + phase * 1.7)
                       + 0.05 * Math.Sin(x * 0.19 - t * 2.3 + phase);
            return Math.Clamp(mid + below * (sway * (0.45 + 0.55 * Math.Min(1, fill)) - raise), top + 2, bottom);
        }
        // the top edge: folds that drift sideways, the curtain taller where they bunch up
        double TopAt(double x, double hem, double phase)
        {
            var folds = 0.6 + 0.26 * Math.Sin(x * 0.033 - t * 0.8 + phase) + 0.14 * Math.Sin(x * 0.091 + t * 1.8 + phase);
            // tanh, not Math.Min(1, …): loud, every fold reached the ceiling and the top ran flat
            // along it for long stretches - it looked cut off. Now the folds keep their shape and
            // only come close to the ceiling.
            return hem - (hem - top) * Math.Tanh((0.35 + 0.75 * fill) * Envelope(x) * folds * 1.25);
        }

        StreamGeometry Curtain(double phase, double raise)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(Left, HemAt(Left, phase, raise)), true, true);
                for (var x = Left; x <= thumbX; x += 3) c.LineTo(new Point(x, TopAt(x, HemAt(x, phase, raise), phase)), true, true);
                c.LineTo(new Point(thumbX, HemAt(thumbX, phase, raise)), true, true);
                for (var x = thumbX - 3; x > Left; x -= 3) c.LineTo(new Point(x, HemAt(x, phase, raise)), true, true);
            }
            g.Freeze();
            return g;
        }

        var back = Curtain(2.3, 0.15);
        var front = Curtain(0, 0);

        // Rays standing on the front curtain's hem, their brightness pulsing along it (the pulses
        // travel, as the light does along a real curtain): three steps, the darkest not drawn.
        var rays = new[] { new StreamGeometry(), new StreamGeometry() };
        var hem = new StreamGeometry();
        using (StreamGeometryContext dim = rays[0].Open(), lit = rays[1].Open(), line = hem.Open())
        {
            line.BeginFigure(new Point(Left, HemAt(Left, 0, 0)), false, false);
            for (var x = Left + 3; x <= thumbX; x += 3) line.LineTo(new Point(x, HemAt(x, 0, 0)), true, true);
            for (var j = 0; ; j++)
            {
                var x = Left + 3 + j * 3.6 + 1.6 * Math.Sin(t * 0.5 + j * 0.9);
                if (x >= thumbX - 2) break;
                var foot = HemAt(x, 0, 0);
                // nearly the curtain's full height, so they read as shafts of light, not bars
                var h = (foot - TopAt(x, foot, 0)) * (0.85 + 0.15 * Math.Sin(t * 1.1 + j * 1.7));
                if (h < 1.5) continue;
                var pulse = 0.5 + 0.35 * Math.Sin(j * 0.42 - t * 2.6) + 0.15 * Math.Sin(j * 1.9 + t * 4.3);
                if (pulse < 0.42) continue;
                var ctx = pulse > 0.72 ? lit : dim;
                // wide at the hem, tapering upward
                var foot2 = 0.8 + 0.5 * pulse;
                ctx.BeginFigure(new Point(x - foot2, foot), true, true);
                ctx.PolyLineTo([new Point(x - 0.2, foot - h), new Point(x + 0.2, foot - h), new Point(x + foot2, foot)], false, false);
            }
        }
        rays[0].Freeze();
        rays[1].Freeze();
        hem.Freeze();

        // Soft at the very bottom, strongest just above the hem, gone at the top.
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, bottom),
            EndPoint = new Point(0, top),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x80, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.18),
                new GradientStop(Color.FromArgb(0xE0, 0, 0, 0), 0.55),
                new GradientStop(Color.FromArgb(0x80, 0, 0, 0), 0.85),
                new GradientStop(Color.FromArgb(0x10, 0, 0, 0), 1),
            },
        };
        mask.Freeze();
        var hue = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(Left, 0),
            EndPoint = new Point(Math.Max(Left + 1, thumbX), 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x50, cool.R, cool.G, cool.B), 0),
                new GradientStop(Color.FromArgb(0xA0, accent.R, accent.G, accent.B), 0.5),
                new GradientStop(Color.FromArgb((byte)(0xB8 + 0x40 * _flash), warm.R, warm.G, warm.B), 1),
            },
        };
        hue.Freeze();
        // toward the top the light shades to a neighboring hue, as a real aurora's upper fringe does
        var fringeColor = Shift(accent, 70, 1.1);
        var fringe = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, mid + below * 0.3),
            EndPoint = new Point(0, top),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, fringeColor.R, fringeColor.G, fringeColor.B), 0),
                new GradientStop(Color.FromArgb(0x80, fringeColor.R, fringeColor.G, fringeColor.B), 1),
            },
        };
        fringe.Freeze();
        var glimmer1 = AlongBar(thumbX, Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        var glimmer2 = AlongBar(thumbX, Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF), Color.FromArgb((byte)(0x60 + 0x50 * _flash), 0xFF, 0xFF, 0xFF));

        // the hem glows softly (no hard edge), fading out at both ends of the curtain
        var span = Math.Max(1, thumbX - Left);
        var hemTint = Mix(accent, Colors.White, 0.35);
        var hemAlpha = (byte)(255 * Math.Clamp(fill, 0, 1));
        var hemBrush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(Left, 0),
            EndPoint = new Point(Left + span, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, hemTint.R, hemTint.G, hemTint.B), 0),
                new GradientStop(Color.FromArgb(hemAlpha, hemTint.R, hemTint.G, hemTint.B), Math.Min(0.5, 36 / span)),
                new GradientStop(Color.FromArgb(hemAlpha, hemTint.R, hemTint.G, hemTint.B), Math.Max(0.5, 1 - 18 / span)),
                new GradientStop(Color.FromArgb(0, hemTint.R, hemTint.G, hemTint.B), 1),
            },
        };
        hemBrush.Freeze();

        dc.PushOpacityMask(mask);
        dc.PushOpacity(0.45);
        dc.DrawGeometry(hue, null, back);
        dc.DrawGeometry(fringe, null, back);
        dc.Pop();
        dc.DrawGeometry(hue, null, front);
        dc.DrawGeometry(fringe, null, front);
        dc.DrawGeometry(glimmer1, null, rays[0]);
        dc.DrawGeometry(glimmer2, null, rays[1]);
        dc.PushOpacity(0.18);
        dc.DrawGeometry(null, new Pen(hemBrush, 4.5), hem);
        dc.Pop();
        dc.PushOpacity(0.3);
        dc.DrawGeometry(null, new Pen(hemBrush, 2), hem);
        dc.Pop();
        dc.Pop();

        // The played line, over the curtain.
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
    }

    // ---- 별빛 ----

    private readonly List<Spark> _stars = [];

    /// <summary>Camera flashes, like photographers' flashes along a red carpet: each one pops on
    /// the spot - a white-hot core, a halo and four long rays - and dies away in a moment, while
    /// others pop elsewhere. X, Y: where; Age, Life: how long since it popped (below 0: waiting to
    /// pop), and how long it lasts; Size: how big; Hue: how much of the cover's color it carries.</summary>
    private void DrawStars(DrawingContext dc, Color tint)
    {
        foreach (var star in _stars)
        {
            if (star.Age < 0) continue;
            // a near-instant pop, then a quick fall-off (squared, like a real flash)
            var t = Math.Clamp(star.Age / star.Life, 0, 1);
            var glow = t < 0.08 ? t / 0.08 : Math.Pow(1 - (t - 0.08) / 0.92, 2);
            if (glow <= 0.02) continue;
            // each flash a soft color of its own: a hue picked round the wheel from the cover's
            // (Vy, in twelve steps), washed toward white (in thirds) - so the colors, and their
            // brushes, stay few
            var color = Mix(Shift(tint, star.Vy * 30, 1.0), Colors.White, 0.08 + 0.2 * Math.Round(star.Hue * 3) / 3);
            double x = star.X, y = star.Y;
            var r = star.Size;

            // halo
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(60 * glow), color.R, color.G, color.B)), null,
                new Point(x, y), r * 1.9 * (0.6 + 0.4 * glow), r * 1.9 * (0.6 + 0.4 * glow));
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(120 * glow), color.R, color.G, color.B)), null,
                new Point(x, y), r * 0.9, r * 0.9);

            // four long, thin rays that shrink back as it fades, turned at the flash's own angle
            var s = r * (1.2 + 2.0 * glow);
            var w = r * 0.16;
            double cos = Math.Cos(star.Vx), sin = Math.Sin(star.Vx);
            Point At(double dx, double dy) => new(x + dx * cos - dy * sin, y + dx * sin + dy * cos);
            var shape = new StreamGeometry();
            using (var g = shape.Open())
            {
                g.BeginFigure(At(0, -s), true, true);
                g.PolyLineTo([
                    At(w, -w), At(s, 0), At(w, w), At(0, s),
                    At(-w, w), At(-s, 0), At(-w, -w)], false, false);
            }
            shape.Freeze();
            dc.DrawGeometry(Solid(Color.FromArgb((byte)(255 * glow), color.R, color.G, color.B)), null, shape);

            // white-hot core
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(255 * glow), 0xFF, 0xFF, 0xFF)), null,
                new Point(x, y), r * 0.38, r * 0.38);
        }
    }

    private void UpdateStars(double dt, bool beat)
    {
        for (var i = _stars.Count - 1; i >= 0; i--)
        {
            var s = _stars[i];
            s.Age += dt;
            if (s.Age >= s.Life) _stars.RemoveAt(i);
            else _stars[i] = s;
        }
        if (Look != SeekBarStyle.Sparkle || !IsPlaying || _activity < 0.3 || Maximum <= 0) return;

        var height = ActualHeight;
        var thumbX = Left + (Right - Left) * Math.Clamp(CurrentPosition() / Maximum, 0, 1);
        if (thumbX - Left < 20) return;
        // Quiet: a flash here and there. Loud: a constant crackle of them, bigger - and every
        // beat a volley at once, all over the played part. (The loudness is curved so the two
        // ends look clearly different, not just a little busier.)
        var energy = Math.Pow(Math.Clamp(Loud(0, 1, _level), 0, 1.2), 1.6);
        var count = (beat ? 2 + (int)(5 * energy) : 0) + (Random.Shared.NextDouble() < dt * (2 + 20 * energy) ? 1 : 0);
        for (var n = 0; n < count && _stars.Count < 40; n++)
        {
            var x = Left + 4 + Random.Shared.NextDouble() * (thumbX - Left - 8);
            var y = 2 + Random.Shared.NextDouble() * Math.Max(0, height - 4);
            var size = (0.9 + Random.Shared.NextDouble() * 0.8 + 1.4 * energy + (beat ? 0.5 : 0)) * EffectScale;
            // a volley's flashes go off a split second apart, not all in the same frame
            var delay = beat ? -Random.Shared.NextDouble() * 0.12 : 0;
            // each camera's flash at an angle of its own (the rays repeat every quarter turn)
            var angle = Random.Shared.NextDouble() * Math.PI / 2;
            var tone = Random.Shared.Next(12);
            AddFlash(x, y, size, delay, angle, tone);
            // now and then a camera fires twice in quick succession (the same flash, the same color)
            if (Random.Shared.NextDouble() < 0.18) AddFlash(x, y, size * 0.85, delay - 0.12 - Random.Shared.NextDouble() * 0.06, angle, tone);
        }
    }

    /// <summary>Queues a flash; its angle is kept in Vx and its color's step round the wheel in
    /// Vy (flashes don't move).</summary>
    private void AddFlash(double x, double y, double size, double delay, double angle, int tone)
    {
        if (_stars.Count >= 44) return;
        _stars.Add(new Spark
        {
            X = x,
            Y = y,
            Vx = angle,
            Vy = tone,
            Age = delay,
            Life = 0.22 + Random.Shared.NextDouble() * 0.25,
            Size = size,
            Hue = Random.Shared.NextDouble(),
        });
    }

    /// <summary>How far above the strip the flames may reach (they're drawn outside the bar).</summary>
    private const double FlameOverflow = 9;
    private const double TongueSpacing = 6.5;

    /// <summary>How much bigger the fire is drawn than the strip's room: height, width and the
    /// reach of its colors alike.</summary>
    private const double FireSize = 1.1;

    /// <summary>How tall the fire burns: follows the music, but through <see cref="_fireLevel"/>,
    /// which rises and falls more slowly than the meter, so the fire swells and settles instead of
    /// pumping with every beat.</summary>
    private double FireAmplitude(double thumbX)
    {
        var room = ActualHeight / 2 - 2 + FlameOverflow;
        var fadeIn = Math.Min(1, (thumbX - Left) / 60);
        // FireSize: 10% bigger than the other looks' scale. The span is wide and the floor low
        // (0.35 + 0.72, was 0.42 + 0.58: the same height at middling loudness), so quiet
        // passages burn clearly lower and loud ones clearly higher.
        return room * FireSize * _activity * Loud(0.35, 0.72, _fireLevel) * fadeIn * (1 + 0.12 * _flash);
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

    /// <summary>How one tongue moves this flicker cycle (<see cref="Ribbon"/>): how far waves
    /// swing its body (Sway, in widths), how many of them run up it (Waves), how fast they run
    /// (Rate), where they start (Phase), how hard and which way its tip hooks over (Hook, -1..1),
    /// how far it reaches this time (Reach) and how pinched its neck is just before the tip
    /// breaks off (Neck, 0..1).</summary>
    private readonly record struct FlameForm(double Sway, double Waves, double Rate, double Phase, double Hook, double Reach, double Neck);

    /// <summary>The form drawn for one flicker cycle (<paramref name="round"/>) of tongue
    /// <paramref name="i"/>: some cycles a tall lick that whips and hooks over, some a short
    /// one that barely sways.</summary>
    private static FlameForm FormFor(int i, double round)
    {
        var k = i * 7 + (int)(round % 10000) * 131;
        var hookOn = Hash(k + 13) < 0.45 ? 1.0 : 0.25;
        return new FlameForm(
            Sway: 0.3 + 0.75 * Hash(k + 3),
            Waves: 0.5 + 0.9 * Hash(k + 5),
            Rate: 0.9 + 0.8 * Hash(i + 211),
            Phase: Hash(i + 223),
            Hook: (Hash(k + 11) * 2 - 1) * hookOn,
            // mostly middling, now and then a tall lick that leaps well above the rest
            Reach: 0.6 + 0.9 * Math.Pow(Hash(k + 17), 1.8),
            Neck: 0);
    }

    private static FlameForm Blend(FlameForm a, FlameForm b, double u) => new(
        a.Sway + (b.Sway - a.Sway) * u, a.Waves + (b.Waves - a.Waves) * u, b.Rate, b.Phase,
        a.Hook + (b.Hook - a.Hook) * u, a.Reach + (b.Reach - a.Reach) * u, 0);

    /// <summary>Tongue <paramref name="i"/> at fire time <paramref name="t"/>: where it stands, how
    /// tall (<paramref name="h"/>, and <paramref name="full"/> at the top of its cycle), how wide,
    /// how far its tip leans and how it moves (<paramref name="form"/>). Each flicker cycle it
    /// takes a new form - eased in from the last one over the first third of the cycle, so
    /// nothing jumps - and the fire never repeats a look. False past the thumb.</summary>
    private bool TongueAt(int i, double t, double endX, double room, double amplitude,
                          out double x, out double h, out double full, out double w, out double lean,
                          out FlameForm form)
    {
        var seed = Hash(i);
        x = Left + i * TongueSpacing + (seed - 0.5) * 3;
        h = full = w = lean = 0;
        form = default;
        if (x > endX - 1) return false;

        var envelope = SmoothStep(Math.Min(1, (x - Left) / 18)) * SmoothStep(Math.Min(1, (endX - x) / 8));
        var nearThumb = 0.62 + 0.38 * SmoothStep(Math.Clamp(1 - (endX - x) / 160, 0, 1));
        // Taller and lower stretches that drift along the fire, so neighbors rise together.
        var body = 0.90 + 0.18 * Math.Sin((x - Left) * 0.045 - t * 1.9 + Math.Sin(t * 0.7) * 1.5)
                        + 0.06 * Math.Sin((x - Left) * 0.11 + t * 3.1);
        var cycle = t * FlickerRate(i) + seed;
        var round = Math.Floor(cycle);
        var p = cycle - round;
        var g = FlickerCycle(p);
        form = Blend(FormFor(i, round - 1), FormFor(i, round), SmoothStep(Math.Min(1, p / 0.35)));
        // the neck pinches in over the last stretch before the tip breaks off
        form = form with { Neck = p < PinchAt ? SmoothStep(Math.Clamp((p - (PinchAt - 0.2)) / 0.2, 0, 1)) : 0 };
        var shimmer = 1 + 0.05 * Math.Sin(t * 21 + seed * 60);
        full = amplitude * envelope * nearThumb * body * form.Reach * (0.8 + 0.2 * Hash(i + 31));
        h = full * g * shimmer;
        // A stretched tongue gets thinner; one that just dropped is short and broad. Narrower
        // than the spacing would suggest, so the tongues' own shapes show, not just one front.
        w = TongueSpacing * FireSize * (1.15 + 0.6 * Hash(i + 31)) * (1.15 - 0.3 * g);
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
        // Nonzero: overlapping tongues merge into one flame front (even-odd would cut their
        // overlaps out as holes).
        var bloomWide = new StreamGeometry { FillRule = FillRule.Nonzero };
        var bloomNear = new StreamGeometry { FillRule = FillRule.Nonzero };
        var outer = new StreamGeometry { FillRule = FillRule.Nonzero };
        var feather = new StreamGeometry { FillRule = FillRule.Nonzero };
        var middle = new StreamGeometry { FillRule = FillRule.Nonzero };
        var core = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var bw = bloomWide.Open())
        using (var bn = bloomNear.Open())
        using (var o = outer.Open())
        using (var f = feather.Open())
        using (var m = middle.Open())
        using (var c = core.Open())
        {
            for (var i = 0; TongueAt(i, t, endX, room, amplitude, out var x, out var h, out var full, out var w, out var lean, out var form); i++)
            {
                if (h < 1.2) continue;
                // The bloom: two larger, fainter ghosts of each tongue, so the fire's edge melts
                // into a warm haze instead of ending at a hard outline (cheaper than a blur, which
                // the GPU would redo every frame). The wide ghost is a plain teardrop - it's
                // haze - but the near one moves with the tongue, or it would blur the shape away.
                TeardropTongue(bw, x, mid + 2, w * 2.4, h * 0.85 + 2, lean * 0.8);
                if (h < room * 0.3)
                {
                    // A short tongue barely sways: a plain teardrop looks the same and costs a
                    // fraction of a ribbon (most of the fire's tongues are short at any moment).
                    TeardropTongue(bn, x, mid + 1.5, w * 1.6, h * 1.04 + 1, lean);
                    TeardropTongue(o, x, mid + 1, w, h, lean);
                    TeardropTongue(f, x + lean * 0.15, mid + 1, w * 0.78, h * 0.8, lean * 0.8);
                    TeardropTongue(m, x + lean * 0.15, mid + 1, w * 0.6, h * 0.72, lean * 0.72);
                }
                else
                {
                    Ribbon(bn, x, mid + 1.5, w * 1.6, h * 1.04 + 1, lean, form, t, w, h);
                    Ribbon(o, x, mid + 1, w, h, lean, form, t, w, h);
                    // The inner layers run along the same swaying path (the same w and h passed
                    // as the frame), only narrower and shorter. The middle layer's edge is
                    // feathered by a slightly larger, fainter copy.
                    Ribbon(f, x, mid + 1, w * 0.78, h * 0.8, lean, form, t, w, h);
                    Ribbon(m, x, mid + 1, w * 0.6, h * 0.72, lean, form, t, w, h);
                }
                // The core stays a small teardrop at the root: the steady hot part.
                TeardropTongue(c, x + lean * 0.1, mid + 1, w * 0.4, h * 0.24 + full * 0.12, lean * 0.4);
            }
        }
        bloomWide.Freeze();
        bloomNear.Freeze();
        outer.Freeze();
        feather.Freeze();
        middle.Freeze();
        core.Freeze();
        // Tongues can stretch past the strip's room now that the effects are bigger; the colors
        // reach that far too, so the tips still show.
        room *= EffectScale * FireSize;

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

        var bloom = 1 + 0.6 * _flash;
        // A light, warm haze (dark reds would read as brown smoke on the dark player).
        // Bright at the root and gone well below the tips: a haze lit by the flames, not an outline.
        dc.DrawGeometry(FireBrush(mid, room * 0.8, [(0, (byte)Math.Min(255, 0x58 * bloom), 0xFF, 0x8C, 0x30), (0.45, (byte)(0x22 * bloom), 0xFF, 0xA0, 0x40), (1, 0x00, 0xFF, 0xB0, 0x50)]), null, bloomWide);
        dc.DrawGeometry(FireBrush(mid, room * 0.9, [(0, (byte)Math.Min(255, 0x90 * bloom), 0xFF, 0x9A, 0x38), (0.4, (byte)(0x3C * bloom), 0xFF, 0xB0, 0x48), (0.75, 0x00, 0xFF, 0xC4, 0x60)]), null, bloomNear);
        // The outer body a little see-through, so its edge blends into the bloom.
        dc.DrawGeometry(FireBrush(mid, room, [(0, 0xD8, 0xC8, 0x22, 0x08), (0.3, 0xC4, 0xFF, 0x52, 0x10), (0.65, 0x80, 0xFF, 0x94, 0x22), (1, 0x00, 0xFF, 0xD0, 0x60)]), null, outer);
        dc.DrawGeometry(FireBrush(mid, room * 0.82, [(0, 0x70, 0xFF, 0xA8, 0x2E), (0.55, 0x48, 0xFF, 0xC8, 0x48), (1, 0x00, 0xFF, 0xE8, 0x90)]), null, feather);
        dc.DrawGeometry(FireBrush(mid, room * 0.75, [(0, 0xF5, 0xFF, 0xA8, 0x2E), (0.55, 0xB0, 0xFF, 0xC8, 0x48), (1, 0x00, 0xFF, 0xE8, 0x90)]), null, middle);
        dc.DrawGeometry(FireBrush(mid, room * 0.42, [(0, 0xFA, 0xFF, 0xFB, 0xE6), (0.6, 0xC0, 0xFF, 0xF0, 0xB0), (1, 0x00, 0xFF, 0xF4, 0xC0)]), null, core);
        DrawLicks(dc);
    }

    /// <summary>Points along a ribbon flame's side (<see cref="Ribbon"/>).</summary>
    private const int RibbonSteps = 6;

    /// <summary>A ribbon's half width at each step, as a share of its full width: wide at the
    /// root, a slight belly low down, tapering to the tip. Worked out once (the steps are fixed),
    /// as the fire builds a few hundred ribbons a frame.</summary>
    private static readonly double[] RibbonTaper = Enumerable.Range(0, RibbonSteps)
        .Select(k => (double)k / RibbonSteps)
        .Select(s => 0.5 * Math.Pow(1 - s, 0.85) * (1 + 0.3 * Math.Sin(Math.PI * s)))
        .ToArray();

    /// <summary>The pinch of a ribbon's neck at each step (how much of it a full pinch takes away).</summary>
    private static readonly double[] RibbonNeck = Enumerable.Range(0, RibbonSteps)
        .Select(k => ((double)k / RibbonSteps - 0.62) / 0.1)
        .Select(z => 0.7 * Math.Exp(-z * z))
        .ToArray();

    /// <summary>One flame tongue as a living ribbon, not a fixed outline: its centerline sways
    /// with waves that run up from the root (so the body ripples and the tip whips about), its
    /// tip may hook over to one side, and it tapers from a wide root to a pointed tip, with a
    /// neck that pinches in just before the tip breaks off. The sway is laid out on the outer
    /// tongue's frame (<paramref name="frameW"/> wide, <paramref name="frameH"/> tall), so the
    /// inner, smaller layers follow the same path inside it.</summary>
    private static void Ribbon(StreamGeometryContext ctx, double x, double baseY, double width, double height, double lean,
                               in FlameForm form, double t, double frameW, double frameH)
    {
        const int n = RibbonSteps;
        Span<Point> left = stackalloc Point[n];
        Span<Point> right = stackalloc Point[n];
        Point tip = default;
        Point previous = default;
        var share = height / Math.Max(1, frameH);
        var swingAll = frameH * 0.3 * form.Sway;
        var wave = Math.Tau * (form.Phase - t * form.Rate);
        for (var k = 0; k <= n; k++)
        {
            var s = (double)k / n;   // 0 at the root, 1 at this layer's tip
            var u = s * share;       // the same point on the outer tongue's frame
            var rise = u * Math.Sqrt(u); // u^1.5: little movement low down, most near the tip
            // sideways moves scale with the flame's height: a short one barely stirs (swung as far
            // as a tall one, it lay flat along the line), a tall one whips about
            var sway = swingAll * rise * Math.Sin(Math.Tau * form.Waves * u + wave);
            var bend = Math.Max(0, u - 0.55) / 0.45;
            var hook = form.Hook * frameH * 0.4 * bend * bend;
            var center = new Point(x + lean * rise + sway + hook, baseY - height * s);
            if (k == n) { tip = center; break; }

            var half = width * RibbonTaper[k] * (1 - form.Neck * RibbonNeck[k]);
            // across the ribbon: square to the path from the point below (straight up at the root)
            double nx = 1, ny = 0;
            if (k > 0)
            {
                var dx = center.X - previous.X;
                var dy = center.Y - previous.Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-6) { nx = -dy / len; ny = dx / len; }
            }
            // never below the root's line (a tilted ribbon's side would dip under it)
            left[k] = new Point(center.X - nx * half, Math.Min(baseY, center.Y - ny * half));
            right[k] = new Point(center.X + nx * half, Math.Min(baseY, center.Y + ny * half));
            previous = center;
        }

        // Smoothed through the midpoints of the side points, with a sharp tip. Each side goes
        // in as one call (a call per segment made the fire four times as costly to build).
        var path = RibbonPath;
        var j = 0;
        for (var k = 1; k < n; k++)
        {
            path[j++] = left[k];
            path[j++] = Mid(left[k], k + 1 < n ? left[k + 1] : tip);
        }
        // up into the tip and out of it: a straight-sided point
        path[j++] = tip;
        path[j++] = tip;
        path[j++] = Mid(tip, right[n - 1]);
        path[j++] = Mid(tip, right[n - 1]);
        for (var k = n - 1; k >= 1; k--)
        {
            path[j++] = right[k];
            path[j++] = Mid(right[k], right[k - 1]);
        }
        path[j++] = right[0];
        path[j++] = right[0];
        ctx.BeginFigure(left[0], isFilled: true, isClosed: true);
        ctx.PolyQuadraticBezierTo(path, true, true);
    }

    /// <summary>The control and end points of one ribbon's outline, reused for every ribbon
    /// (the fire is built on the UI thread only).</summary>
    private static readonly Point[] RibbonPath = new Point[4 * RibbonSteps + 2];

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>The plain teardrop: a wide root on the line narrowing to a leaning, pointed tip.</summary>
    private static void TeardropTongue(StreamGeometryContext ctx, double x, double baseY, double width, double height, double lean)
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
            if (!TongueAt(i, t, thumbX, room, amplitude, out var x, out _, out var full, out var w, out var lean, out _)) break;
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
        if (IsLoaded && OnScreen.Check(this) && (IsPlaying || _activity > 0.002 || _ripples.Count > 0 || _sparks.Count > 0 || _licks.Count > 0 || _stars.Count > 0 || _drops.Count > 0 || _splashes.Count > 0 || _droplets.Count > 0 || _jets.Count > 0 || _spouts.Count > 0 || _rockets.Count > 0 || _embers.Count > 0 || NatureAlive)
            && Look != SeekBarStyle.None) StartAnimating();
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
        Step(dt);
    }

    /// <summary>Moves everything on by <paramref name="dt"/> seconds and redraws.</summary>
    private void Step(double dt)
    {
        var target = !IsPlaying ? 0 : Waveform is { } waveform ? waveform.LevelAt(CurrentPosition()) : FallbackLevel;
        // up at once, down quickly enough that a drop in the music shows (was 4: a slow sink)
        _level += (target - _level) * Math.Min(1, dt * (target > _level ? 18 : 7));
        _baseline += (target - _baseline) * Math.Min(1, dt * 1.2);
        _activity += ((IsPlaying ? 1.0 : 0.0) - _activity) * Math.Min(1, dt * 3);

        // Louder music flows faster.
        _phase += dt * (1.4 + 2.4 * _level) * Math.Max(_activity, 0.15);
        // The fire grows and settles more slowly than the meter; it flickers faster when loud.
        // (8 up / 3 down, was 5 / 2: still smoother than the meter, but follows the music visibly)
        _fireLevel += (target - _fireLevel) * Math.Min(1, dt * (target > _fireLevel ? 8 : 3));
        var previousFireTime = _fireTime;
        _fireTime += dt * (0.9 + 0.8 * _fireLevel + 0.8 * _flash) * Math.Max(_activity, 0.2);

        // A beat: loudness jumping well above its recent average sends a ripple back from the thumb.
        _sinceBeat += dt;
        var beat = false;
        // (threshold 0.12, was 0.16; flash and ripple about a third stronger: more of the beats
        // show, and clearly)
        if (IsPlaying && Waveform is not null && target - _baseline > 0.12 && _sinceBeat > 0.15)
        {
            _sinceBeat = 0;
            beat = true;
            _beatCount++;
            _beatStrength = target - _baseline;
            _flash = Math.Max(_flash, Math.Min(1, (target - _baseline) * 4));
            _ripples.Add((0, Math.Min(0.75, (target - _baseline) * 2.4)));
            if (_ripples.Count > 6) _ripples.RemoveAt(0);
        }
        _flash *= Math.Exp(-dt * 5);
        UpdateSparks(dt, beat);
        SpawnLicks(previousFireTime);
        UpdateStars(dt, beat);
        UpdateRain(dt, beat);
        UpdateFountain(dt, beat);
        UpdateFireworks(dt, beat);
        UpdateNature(dt, beat);
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var (distance, strength) = _ripples[i];
            strength *= Math.Exp(-dt * 2.2);
            if (strength < 0.02) _ripples.RemoveAt(i);
            else _ripples[i] = (distance + dt * 150, strength);
        }

        if (!IsPlaying && _activity < 0.002 && _ripples.Count == 0 && _sparks.Count == 0 && _licks.Count == 0 && _stars.Count == 0 && _drops.Count == 0 && _splashes.Count == 0 && _droplets.Count == 0 && _jets.Count == 0 && _spouts.Count == 0 && _rockets.Count == 0 && _embers.Count == 0 && !NatureAlive)
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
