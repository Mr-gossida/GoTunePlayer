using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// The seek bar looks added after the first ten: 반딧불, 전류, 꽃잎 and 음표. Each is particles
// over the played part, moved in UpdateNature and drawn by its own Draw method; brushes and pens
// come from the kept sets in WaveSeekBar.cs (Solid, SolidPen), so a frame makes none of its own.
public sealed partial class WaveSeekBar
{
    private readonly List<Spark> _flies = [];
    private readonly List<Spark> _bolts = [];
    private readonly List<Spark> _petals = [];
    private readonly List<Spark> _notes = [];

    /// <summary>How far above the strip petals start falling and notes may rise.</summary>
    private const double NatureOverflow = 8;

    /// <summary>A gust that pushes the petals sideways on a beat, dying away.</summary>
    private double _gust;

    private bool NatureAlive => _flies.Count > 0 || _bolts.Count > 0 || _petals.Count > 0 || _notes.Count > 0;

    private void ClearNature()
    {
        _flies.Clear();
        _bolts.Clear();
        _petals.Clear();
        _notes.Clear();
        _gust = 0;
    }

    /// <summary>The loudness, curved so quiet and loud passages look clearly different (as the
    /// stars do).</summary>
    private double Energy => Math.Pow(Math.Clamp(Loud(0, 1, _level), 0, 1.2), 1.6);

    private double PlayedX => Left + (Right - Left) * Math.Clamp(CurrentPosition() / Math.Max(Maximum, 1e-9), 0, 1);

    private bool Spawning(SeekBarStyle look) => Look == look && IsPlaying && _activity >= 0.3 && Maximum > 0;

    private void UpdateNature(double dt, bool beat)
    {
        UpdateFlies(dt);
        UpdateBolts(dt, beat);
        UpdatePetals(dt, beat);
        UpdateNotes(dt, beat);
    }

    // ---- 반딧불 ----

    /// <summary>Fireflies over the played part: each wanders on its own slow, curving path and
    /// blinks in its own rhythm - a soft glow that swells and goes out. Quiet music: a few, dim.
    /// Loud: a swarm, brighter, and every beat lights them all up at once.</summary>
    private void DrawFireflies(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        // firefly light: a yellow-green leaning toward the cover's color
        var light = Mix(Color.FromRgb(0xDC, 0xFF, 0x70), accent, 0.3);
        foreach (var f in _flies)
        {
            var b = FlyGlow(f) * (0.75 + 0.6 * _flash);
            if (b <= 0.03) continue;
            b = Math.Min(1, b);
            var p = new Point(f.X, f.Y);
            var r = f.Size;
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(55 * b), light.R, light.G, light.B)), null, p, r * 5, r * 5);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(150 * b), light.R, light.G, light.B)), null, p, r * 2.4, r * 2.4);
            var core = Mix(light, Colors.White, 0.55);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(255 * b), core.R, core.G, core.B)), null, p, r, r);
        }
    }

    /// <summary>A firefly's light now: its blink (mostly dark, a slow bright swell) inside the
    /// fade in and out of its life.</summary>
    private static double FlyGlow(Spark f)
    {
        var rate = 0.45 + 0.5 * f.Hue;
        var blink = Math.Pow(Math.Max(0, Math.Sin((f.Age * rate + f.Hue) * Math.Tau)), 3);
        var life = Math.Min(1, Math.Min(f.Age / 0.5, (f.Life - f.Age) / 0.6));
        return Math.Max(0, life) * (0.3 + 0.7 * blink);
    }

    private void UpdateFlies(double dt)
    {
        var height = ActualHeight;
        var thumbX = PlayedX;
        var energy = Energy;
        var speed = 10 + 16 * energy;
        for (var i = _flies.Count - 1; i >= 0; i--)
        {
            var f = _flies[i];
            // wandering: the heading turns a little this way or that, never sharply
            f.Vx += (Random.Shared.NextDouble() - 0.5) * 70 * dt;
            f.Vy += (Random.Shared.NextDouble() - 0.5) * 50 * dt;
            // kept over the played part and inside the strip, by a gentle push back
            if (f.X < Left + 3) f.Vx += 30 * dt;
            if (f.X > thumbX - 3) f.Vx -= 30 * dt;
            if (f.Y < 3) f.Vy += 30 * dt;
            if (f.Y > height - 3) f.Vy -= 30 * dt;
            var v = Math.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
            if (v > speed) { f.Vx *= speed / v; f.Vy *= speed / v; }
            f.X += f.Vx * dt;
            f.Y += f.Vy * dt;
            f.Age += dt;
            // the song jumped back past it: gone at once
            if (f.X > thumbX + 12) f.Age = f.Life;
            if (f.Age >= f.Life) _flies.RemoveAt(i);
            else _flies[i] = f;
        }
        if (!Spawning(SeekBarStyle.Fireflies) || thumbX - Left < 16) return;
        var wanted = (int)Math.Min(24, 3 + 18 * energy) * Math.Min(1, (thumbX - Left) / 120);
        if (_flies.Count < wanted && Random.Shared.NextDouble() < dt * 6)
        {
            var angle = Random.Shared.NextDouble() * Math.Tau;
            _flies.Add(new Spark
            {
                X = Left + 4 + Random.Shared.NextDouble() * (thumbX - Left - 8),
                Y = 3 + Random.Shared.NextDouble() * Math.Max(0, height - 6),
                Vx = Math.Cos(angle) * 6,
                Vy = Math.Sin(angle) * 4,
                Life = 3 + Random.Shared.NextDouble() * 4,
                Size = (1.15 + Random.Shared.NextDouble() * 0.5) * EffectScale,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }

    // ---- 전류 ----

    /// <summary>The played line as a live electric arc: it crackles - a new jagged path every
    /// frame - jumping higher the louder the music, with a fainter second arc beside it. Forks
    /// leap off it on the beats (and now and then in between), flicker and are gone. Pausing
    /// calms it to a still, glowing wire.</summary>
    private void DrawElectric(DrawingContext dc, double mid, double thumbX, Color accent)
    {
        var glow = Shift(accent, -12, 1.2);
        var length = thumbX - Left;
        var reach = Math.Max(0, mid - 2);
        var amplitude = Math.Min(reach, _activity * (0.6 + Loud(0.4, 3.6, _level) + 3.5 * _flash));
        var calm = SmoothStep(Math.Min(1, length / 40)); // it doesn't spark right at the start

        var arc = Arc(mid, thumbX, amplitude * calm, 5);
        var faint = Arc(mid, thumbX, amplitude * calm * 1.3, 7);
        dc.DrawGeometry(null, RoundPen(Color.FromArgb(0x55, glow.R, glow.G, glow.B), 6), arc);
        dc.DrawGeometry(null, RoundPen(Color.FromArgb(0x80, glow.R, glow.G, glow.B), 1), faint);
        dc.DrawGeometry(null, RoundPen(Color.FromArgb(0xE0, glow.R, glow.G, glow.B), 2.2), arc);
        dc.DrawGeometry(null, RoundPen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 0.9), arc);

        foreach (var bolt in _bolts)
        {
            if (bolt.X > thumbX) continue;
            var fade = 1 - bolt.Age / bolt.Life;
            var fork = new StreamGeometry();
            using (var g = fork.Open())
            {
                // a crooked line away from the arc, its kinks redrawn every frame
                var p = new Point(bolt.X, mid);
                g.BeginFigure(p, false, false);
                for (var k = 1; k <= 4; k++)
                {
                    p = new Point(p.X + bolt.Vx * bolt.Size / 4 + (Random.Shared.NextDouble() - 0.5) * 4,
                                  mid + bolt.Vy * bolt.Size * k / 4);
                    g.LineTo(p, true, false);
                }
            }
            fork.Freeze();
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0x50 * fade), glow.R, glow.G, glow.B), 3.5), fork);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0xE0 * fade), 0xF4, 0xFB, 0xFF), 0.9), fork);
        }
    }

    /// <summary>A jagged path along the line from the start to the thumb, pinned at both ends.</summary>
    private StreamGeometry Arc(double mid, double thumbX, double amplitude, double step)
    {
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(Left, mid), false, false);
            if (amplitude < 0.15)
            {
                g.LineTo(new Point(thumbX, mid), true, false);
            }
            else
            {
                var n = Math.Max(2, (int)((thumbX - Left) / step));
                var previous = 0.0;
                for (var i = 1; i < n; i++)
                {
                    var x = Left + (thumbX - Left) * i / n;
                    // half follows the last kink, half jumps: jagged, not noise
                    var y = previous * 0.45 + (Random.Shared.NextDouble() * 2 - 1) * 0.55;
                    previous = y;
                    var ends = Math.Min(1, Math.Min(x - Left, thumbX - x) / 10);
                    g.LineTo(new Point(x, mid + y * amplitude * ends), true, false);
                }
                g.LineTo(new Point(thumbX, mid), true, false);
            }
        }
        arc.Freeze();
        return arc;
    }

    private void UpdateBolts(double dt, bool beat)
    {
        for (var i = _bolts.Count - 1; i >= 0; i--)
        {
            var b = _bolts[i];
            b.Age += dt;
            if (b.Age >= b.Life) _bolts.RemoveAt(i);
            else _bolts[i] = b;
        }
        if (!Spawning(SeekBarStyle.Electric)) return;
        var thumbX = PlayedX;
        if (thumbX - Left < 30) return;
        var energy = Energy;
        var count = (beat ? 1 + (int)(4 * energy) : 0) + (Random.Shared.NextDouble() < dt * (0.6 + 5 * energy) ? 1 : 0);
        var reach = ActualHeight / 2 + 3;
        for (var n = 0; n < count && _bolts.Count < 10; n++)
        {
            _bolts.Add(new Spark
            {
                X = Left + 10 + Random.Shared.NextDouble() * (thumbX - Left - 20),
                Vx = (Random.Shared.NextDouble() - 0.5) * 1.6,  // how far it leans along the bar, per step
                Vy = Random.Shared.NextDouble() < 0.5 ? -1 : 1, // up or down
                Size = reach * (0.55 + 0.45 * Random.Shared.NextDouble()) * (0.7 + 0.4 * energy),
                Life = 0.08 + Random.Shared.NextDouble() * 0.14,
            });
        }
    }

    private readonly Dictionary<(uint Color, double Thickness), Pen> _roundPens = [];

    /// <summary>A kept pen with round joins and ends, for the jagged lines (mitred kinks spike).</summary>
    private Pen RoundPen(Color color, double thickness)
    {
        var brush = Solid(color);
        return Kept(_roundPens, (ColorKey(Stepped(color)), thickness), () => new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        });
    }

    // ---- 꽃잎 ----

    /// <summary>A petal one unit tall, its tip at the bottom and a little notch at the top, centered.</summary>
    private static readonly Geometry PetalShape = MakePetal();

    private static Geometry MakePetal()
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(0, 0.5), true, true);
            g.BezierTo(new Point(0.42, 0.25), new Point(0.34, -0.42), new Point(0.08, -0.5), true, true);
            g.LineTo(new Point(0, -0.38), true, true);
            g.LineTo(new Point(-0.08, -0.5), true, true);
            g.BezierTo(new Point(-0.34, -0.42), new Point(-0.42, 0.25), new Point(0, 0.5), true, true);
        }
        shape.Freeze();
        return shape;
    }

    /// <summary>Petals drifting down over the played part, swaying and tumbling - each turns and
    /// flips as it falls, showing its paler back. More fall when the music is loud, and a beat
    /// is a gust that sweeps them sideways.</summary>
    private void DrawPetals(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        var blossom = Mix(accent, Color.FromRgb(0xFF, 0xB4, 0xCB), 0.75);
        var height = ActualHeight;
        foreach (var p in _petals)
        {
            var fadeIn = Math.Min(1, p.Age / 0.35);
            var fadeOut = Math.Clamp((height + 2 - p.Y) / 8, 0, 1); // melts away at the bottom
            var alpha = fadeIn * fadeOut;
            if (alpha <= 0.03) continue;
            var spin = p.Hue * Math.Tau + p.Age * (1.2 + 1.6 * p.Hue) * (p.Hue < 0.5 ? 1 : -1);
            var flip = Math.Cos(p.Age * (2.2 + 1.5 * p.Hue) + p.Hue * 9);
            // front: the petal's color; back (flipped over): paler
            var tone = flip >= 0 ? Mix(blossom, Colors.White, 0.12 * p.Hue) : Mix(blossom, Colors.White, 0.45);
            var size = p.Size;
            var m = new Matrix();
            m.Scale(size * Math.Max(0.15, Math.Abs(flip)), size);
            m.Rotate(spin * 180 / Math.PI);
            m.Translate(p.X, p.Y);
            var place = new MatrixTransform(m);
            place.Freeze();
            dc.PushTransform(place);
            dc.DrawGeometry(Solid(Color.FromArgb((byte)(235 * alpha), tone.R, tone.G, tone.B)), null, PetalShape);
            dc.Pop();
        }
    }

    private void UpdatePetals(double dt, bool beat)
    {
        _gust *= Math.Exp(-dt * 1.6);
        if (beat && Look == SeekBarStyle.Petals) _gust = Math.Min(1.4, _gust + 0.4 + _flash);
        var height = ActualHeight;
        for (var i = _petals.Count - 1; i >= 0; i--)
        {
            var p = _petals[i];
            p.Age += dt;
            // swaying down, with the gust's push
            var sway = Math.Sin(p.Age * (1.6 + p.Hue) + p.Hue * 20) * (6 + 4 * p.Hue);
            p.X += (p.Vx + sway + 28 * _gust) * dt;
            p.Y += p.Vy * (0.75 + 0.35 * Math.Abs(Math.Sin(p.Age * 2.2 + p.Hue * 9))) * dt;
            if (p.Y > height + 4 || p.X > Right + 10 || p.Age > 12) _petals.RemoveAt(i);
            else _petals[i] = p;
        }
        if (!Spawning(SeekBarStyle.Petals)) return;
        var thumbX = PlayedX;
        if (thumbX - Left < 16) return;
        var energy = Energy;
        var count = (beat ? 1 + (int)(2 * energy) : 0) + (Random.Shared.NextDouble() < dt * (1.2 + 7 * energy) ? 1 : 0);
        for (var n = 0; n < count && _petals.Count < 32; n++)
        {
            _petals.Add(new Spark
            {
                X = Left + Random.Shared.NextDouble() * (thumbX - Left),
                Y = -NatureOverflow + Random.Shared.NextDouble() * 4,
                Vx = -2 + Random.Shared.NextDouble() * 4,
                Vy = 9 + Random.Shared.NextDouble() * 7 + 8 * energy,
                Size = (6 + Random.Shared.NextDouble() * 2.5) * EffectScale,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }

    // ---- 음표 ----

    /// <summary>♪ ♫ ♬ ♩ as shapes, one unit tall and centered on their middle, made once.</summary>
    private static readonly Geometry[] NoteShapes = MakeNotes();

    private static Geometry[] MakeNotes()
    {
        var face = new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return [.. new[] { "♪", "♫", "♬", "♩" }.Select(glyph =>
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 100, Brushes.White, 1.0);
            var shape = text.BuildGeometry(new Point(0, 0));
            var bounds = shape.Bounds;
            var unit = bounds.IsEmpty || bounds.Height <= 0 ? 1 : 1 / bounds.Height;
            var fit = new TransformGroup
            {
                Children =
                {
                    new TranslateTransform(-(bounds.Left + bounds.Width / 2), -(bounds.Top + bounds.Height / 2)),
                    new ScaleTransform(unit, unit),
                },
            };
            var placed = Geometry.Combine(shape, Geometry.Empty, GeometryCombineMode.Union, fit);
            placed.Freeze();
            return (Geometry)placed;
        })];
    }

    /// <summary>Musical notes rising from all along the played part - not just at the thumb - as
    /// if the music already heard were still singing: each pops up from the line, floats upward
    /// swaying and tilting, and fades. Loud music sends up more, and a beat a handful at once.</summary>
    private void DrawNotes(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        foreach (var n in _notes)
        {
            var t = n.Age / n.Life;
            var alpha = Math.Min(1, t / 0.12) * Math.Min(1, (1 - t) / 0.45);
            if (alpha <= 0.03) continue;
            var pop = 0.55 + 0.45 * SmoothStep(Math.Min(1, t / 0.18));
            var glyph = NoteShapes[(int)(n.Hue * NoteShapes.Length) % NoteShapes.Length];
            var whiteness = n.Hue * NoteShapes.Length % 1;
            var tone = Mix(Shift(accent, (whiteness - 0.5) * 50, 1.15), Colors.White, 0.25 + 0.45 * whiteness);
            var x = n.X + Math.Sin(n.Age * 3.1 + n.Hue * 30) * 3;
            var tilt = Math.Sin(n.Age * 2.3 + n.Hue * 17) * 14;
            var m = new Matrix();
            m.Scale(n.Size * pop, n.Size * pop);
            m.Rotate(tilt);
            m.Translate(x, n.Y);
            var place = new MatrixTransform(m);
            place.Freeze();
            dc.PushTransform(place);
            dc.DrawGeometry(Solid(Color.FromArgb((byte)(255 * alpha), tone.R, tone.G, tone.B)), null, glyph);
            dc.Pop();
        }
    }

    private void UpdateNotes(double dt, bool beat)
    {
        for (var i = _notes.Count - 1; i >= 0; i--)
        {
            var n = _notes[i];
            n.Age += dt;
            n.Y += n.Vy * dt;
            n.Vy *= Math.Exp(-dt * 0.6); // slowing as it rises
            if (n.Age >= n.Life) _notes.RemoveAt(i);
            else _notes[i] = n;
        }
        if (!Spawning(SeekBarStyle.Notes)) return;
        var thumbX = PlayedX;
        if (thumbX - Left < 16) return;
        var energy = Energy;
        var mid = ActualHeight / 2;
        var count = (beat ? 1 + (int)(3 * energy) : 0) + (Random.Shared.NextDouble() < dt * (1 + 6 * energy) ? 1 : 0);
        for (var k = 0; k < count && _notes.Count < 24; k++)
        {
            _notes.Add(new Spark
            {
                X = Left + 6 + Random.Shared.NextDouble() * (thumbX - Left - 10),
                Y = mid - 1 - Random.Shared.NextDouble() * 3,
                Vy = -(13 + Random.Shared.NextDouble() * 9 + 10 * energy),
                Life = 1.3 + Random.Shared.NextDouble() * 0.9,
                Size = (9 + Random.Shared.NextDouble() * 3 + 2 * energy) * EffectScale,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }
}
