using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// The seek bar looks added after the first ten: 반딧불, 테슬라 코일, 꽃잎 and 음표 (비눗방울,
// 실크 리본 and 별똥별 have files of their own). Each is particles
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

    /// <summary>Which way the gusts blow (+1 right, -1 left): picked anew when the wind has
    /// died down, and every few seconds anyway (with a beat every half second the gusts never
    /// die down, and the petals blew one way for half a minute).</summary>
    private double _gustDirection = 1;
    private double _gustTurnIn;

    /// <summary>The petals' breeze: a gentle wind that slowly turns from one side to the other.</summary>
    private double _breezeTime;

    private bool NatureAlive => _flies.Count > 0 || _bolts.Count > 0 || _petals.Count > 0 || _notes.Count > 0 || _bubbles.Count > 0 || _pops.Count > 0
                               || _meteors.Count > 0;

    private void ClearNature()
    {
        _flies.Clear();
        _bolts.Clear();
        _petals.Clear();
        _notes.Clear();
        _bubbles.Clear();
        _pops.Clear();
        _meteors.Clear();
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
        UpdateBubbles(dt, beat);
        UpdateMeteors(dt, beat);
    }

    // ---- 반딧불 ----

    /// <summary>Fireflies over the played part: each wanders on its own slow, curving path and
    /// blinks in its own rhythm - a soft glow that swells and goes out. Quiet music: a few, dim.
    /// Loud: a swarm, brighter, and every beat lights them all up at once.</summary>
    private void DrawFireflies(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        foreach (var f in _flies)
        {
            var b = FlyGlow(f) * (0.75 + 0.6 * _flash);
            if (b <= 0.03) continue;
            b = Math.Min(1, b);
            var p = new Point(f.X, f.Y);
            var r = f.Size;
            // each firefly its own light, from a small palette (so the kept brushes stay few),
            // leaning a little toward the cover's color
            var light = Mix(FlyColors[FlyColorIndex(f)], accent, 0.15);
            // soft: a wide faint halo, a gentle middle, and a core that keeps its color
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(45 * b), light.R, light.G, light.B)), null, p, r * 5, r * 5);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(120 * b), light.R, light.G, light.B)), null, p, r * 2.4, r * 2.4);
            var core = Mix(light, Colors.White, 0.35);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(230 * b), core.R, core.G, core.B)), null, p, r, r);
        }
    }

    /// <summary>The fireflies' lights: soft pastels (many hues, none of them loud) - lime, butter,
    /// peach, rose, lilac, periwinkle, aqua and mint.</summary>
    private static readonly Color[] FlyColors =
    [
        Color.FromRgb(0xD8, 0xF0, 0xA0),
        Color.FromRgb(0xF4, 0xE4, 0xA8),
        Color.FromRgb(0xF6, 0xC8, 0xA8),
        Color.FromRgb(0xF2, 0xB8, 0xC8),
        Color.FromRgb(0xD8, 0xC0, 0xF0),
        Color.FromRgb(0xB8, 0xC8, 0xF4),
        Color.FromRgb(0xA8, 0xE4, 0xEC),
        Color.FromRgb(0xB0, 0xEC, 0xC8),
    ];

    /// <summary>Which of <see cref="FlyColors"/> a firefly glows in: taken from its Hue, but
    /// scrambled so the color doesn't follow its blink rate (which Hue also sets).</summary>
    private static int FlyColorIndex(Spark f)
    {
        var x = f.Hue * 97.13;
        return Math.Clamp((int)((x - Math.Floor(x)) * FlyColors.Length), 0, FlyColors.Length - 1);
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

    // ---- 테슬라 코일 ----

    /// <summary>The played line as a charged wire, discharging all along it like a Tesla coil:
    /// arcs of lightning crackle off it at random spots into the air above and below - jagged,
    /// forking into thinner branches, each lasting a few frames and redrawn on every frame so
    /// they flicker - and on a beat, bright arcs jump from one spot on the line to another.
    /// Louder music: more, longer, brighter arcs. The light is the cover's color leaning toward
    /// a coil's violet, white-hot at the core. Paused, the wire goes quiet.
    /// (In a bolt: X - where on the line it starts; Vx, Vy - where it reaches, from there;
    /// Size 1 for an arc that lands back on the line; Hue - how bright.)</summary>
    private void DrawElectric(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        var glow = Mix(accent, Color.FromRgb(0xB8, 0xA4, 0xFF), 0.55);
        // The arcs keep to a band a little taller than the strip. They used to reach well past
        // it and get bounced back at its edges, where they ran flat along them - a hard
        // straight border above and below the bar.
        var limit = ElectricReach(mid);
        var top = mid - limit;
        var bottom = mid + limit;

        // the wire's own faint charge glow
        if (_activity > 0.05)
        {
            var charge = (0.5 + 0.5 * Energy + 0.5 * _flash) * _activity;
            dc.DrawLine(SolidPen(Color.FromArgb((byte)(0x30 * Math.Min(1, charge)), glow.R, glow.G, glow.B), 5), new Point(Left, mid), new Point(thumbX, mid));
        }

        foreach (var bolt in _bolts)
        {
            if (bolt.X > thumbX - 2) continue; // the song jumped back past it
            var fade = (1 - bolt.Age / bolt.Life) * (0.7 + 0.3 * Random.Shared.NextDouble());
            if (fade <= 0.04) continue;
            var start = new Point(bolt.X, mid);
            var arc = bolt.Size > 0;

            var trunk = new StreamGeometry();
            var forks = new StreamGeometry();
            Point end;
            using (var t = trunk.Open())
            using (var f = forks.Open())
            {
                List<Point> path;
                if (arc)
                {
                    // from one spot on the line up (or down) and over, back onto the line
                    end = new Point(Math.Clamp(bolt.X + bolt.Vx, Left, thumbX - 2), mid);
                    var apex = new Point((start.X + end.X) / 2 + (Random.Shared.NextDouble() - 0.5) * Math.Abs(bolt.Vx) * 0.3,
                                         Math.Clamp(mid + bolt.Vy, top, bottom));
                    path = Jagged(start, apex, 0.25, top, bottom);
                    path.AddRange(Jagged(apex, end, 0.25, top, bottom).Skip(1));
                }
                else
                {
                    end = new Point(Math.Clamp(bolt.X + bolt.Vx, Left, thumbX), Math.Clamp(mid + bolt.Vy, top, bottom));
                    path = Jagged(start, end, 0.3, top, bottom);
                }
                Polyline(t, path);
                // forks off the trunk, now and then a fork off a fork
                var span = (end - start).Length + Math.Abs(bolt.Vy);
                for (var i = 2; i < path.Count - 2; i++)
                {
                    if (Random.Shared.NextDouble() > (arc ? 0.1 : 0.08)) continue;
                    var from = path[i];
                    var along = path[i + 1] - path[i - 1];
                    if (along.Length < 0.01) continue;
                    along.Normalize();
                    var turn = (Random.Shared.NextDouble() < 0.5 ? -1 : 1) * (0.5 + Random.Shared.NextDouble() * 0.7);
                    var dir = new Vector(along.X * Math.Cos(turn) - along.Y * Math.Sin(turn), along.X * Math.Sin(turn) + along.Y * Math.Cos(turn));
                    var reach = span * (0.1 + Random.Shared.NextDouble() * 0.15);
                    var forkPath = Jagged(from, from + dir * reach, 0.3, top, bottom);
                    Polyline(f, forkPath);
                    if (forkPath.Count > 4 && Random.Shared.NextDouble() < 0.35)
                    {
                        var at = forkPath[forkPath.Count / 2];
                        var twig = new Vector(dir.X * 0.6 - dir.Y * 0.8 * turn, dir.Y * 0.6 + dir.X * 0.8 * turn);
                        Polyline(f, Jagged(at, at + twig * reach * 0.45, 0.32, top, bottom));
                    }
                }
            }
            trunk.Freeze();
            forks.Freeze();

            var bright = fade * (0.7 + 0.3 * bolt.Hue);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0x30 * bright), glow.R, glow.G, glow.B), arc ? 4.5 : 3.5), trunk);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0x26 * bright), glow.R, glow.G, glow.B), 2.2), forks);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0xB0 * bright), glow.R, glow.G, glow.B), arc ? 1.6 : 1.2), trunk);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0x90 * bright), glow.R, glow.G, glow.B), 0.8), forks);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0xF0 * bright), 0xFF, 0xFF, 0xFF), arc ? 0.8 : 0.6), trunk);
            // where an arc leaves the wire (and where it lands), a white-hot spot
            var hot = Solid(Color.FromArgb((byte)(0x90 * bright), 0xFF, 0xFF, 0xFF));
            dc.DrawEllipse(hot, null, start, arc ? 1.8 : 1.3, arc ? 1.8 : 1.3);
            if (arc) dc.DrawEllipse(hot, null, end, 1.8, 1.8);
        }
    }

    /// <summary>How far from the wire the arcs may go, above and below: the strip's half
    /// height and a few pixels more.</summary>
    private static double ElectricReach(double mid) => mid - 1 + 4;

    /// <summary>A lightning path from <paramref name="a"/> to <paramref name="b"/>: the straight
    /// line, its middle pushed sideways at random, then the middles of the halves, and so on -
    /// jagged at every scale, like the real thing. Kept between <paramref name="top"/> and
    /// <paramref name="bottom"/>.</summary>
    private static List<Point> Jagged(Point a, Point b, double roughness, double top, double bottom)
    {
        var points = new List<Point>(34) { a, new Point(b.X, Math.Clamp(b.Y, top, bottom)) };
        for (var pass = 0; pass < 5; pass++)
        {
            var next = new List<Point>(points.Count * 2);
            var longest = 0.0;
            for (var i = 0; i < points.Count - 1; i++)
            {
                var p = points[i];
                var q = points[i + 1];
                next.Add(p);
                var d = q - p;
                var length = d.Length;
                longest = Math.Max(longest, length);
                if (length < 3) continue;
                var across = new Vector(-d.Y / length, d.X / length);
                var m = p + d / 2 + across * ((Random.Shared.NextDouble() * 2 - 1) * length * roughness);
                // past the edge: bounced back in (cut off at it, the arcs ran flat along it)
                var y = m.Y < top ? top + (top - m.Y) * 0.6 : m.Y > bottom ? bottom - (m.Y - bottom) * 0.6 : m.Y;
                next.Add(new Point(m.X, Math.Clamp(y, top, bottom)));
            }
            next.Add(points[^1]);
            points = next;
            if (longest < 6) break;
        }
        return points;
    }

    private static void Polyline(StreamGeometryContext g, List<Point> points)
    {
        if (points.Count < 2) return;
        g.BeginFigure(points[0], false, false);
        g.PolyLineTo(points.GetRange(1, points.Count - 1), true, true);
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
        var room = thumbX - Left;
        if (room < 24) return;
        var energy = Energy;
        // arcs end well inside the band, so the jags around them still fit
        var limit = ElectricReach(ActualHeight / 2);
        double Spot() => Left + 4 + Random.Shared.NextDouble() * (room - 8);

        // arcs off the wire at random spots all along it - more and longer the louder it is
        // (and more of them on a longer played part)
        var rate = (8 + 52 * energy) * Math.Clamp(room / 300, 0.4, 1.6);
        var count = (int)(rate * dt) + (Random.Shared.NextDouble() < rate * dt % 1 ? 1 : 0);
        for (var n = 0; n < count && _bolts.Count < 22; n++)
        {
            // up or down, leaning one way or the other
            var up = Random.Shared.NextDouble() < 0.5 ? -1 : 1;
            var reach = 4 + Random.Shared.NextDouble() * (5 + 9 * energy);
            _bolts.Add(new Spark
            {
                X = Spot(),
                Vx = (Random.Shared.NextDouble() - 0.5) * reach * 2,
                Vy = up * Math.Min(reach, limit * 0.65),
                Life = 0.05 + Random.Shared.NextDouble() * 0.09,
                Hue = Random.Shared.NextDouble(),
            });
        }

        // a beat: bright arcs jumping from one spot on the wire to another
        if (beat)
        {
            var arcs = 1 + (int)(3 * energy);
            for (var n = 0; n < arcs && _bolts.Count < 28; n++)
            {
                var span = Math.Min(room - 8, 20 + Random.Shared.NextDouble() * (30 + 90 * energy));
                var from = Left + 4 + Random.Shared.NextDouble() * Math.Max(0, room - 8 - span);
                _bolts.Add(new Spark
                {
                    X = from,
                    Vx = span,
                    Vy = (Random.Shared.NextDouble() < 0.5 ? -1 : 1) * limit * (0.35 + 0.2 * Random.Shared.NextDouble() + 0.15 * energy),
                    Size = 1,
                    Life = 0.12 + Random.Shared.NextDouble() * 0.1,
                    Hue = 0.6 + 0.4 * Random.Shared.NextDouble(),
                });
            }
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
        if (beat && Look == SeekBarStyle.Petals)
        {
            // a new gust comes from either side
            if (_gust < 0.25 || _gustTurnIn <= 0)
            {
                _gustDirection = Random.Shared.NextDouble() < 0.5 ? -1 : 1;
                _gustTurnIn = 3 + Random.Shared.NextDouble() * 4;
            }
            _gust = Math.Min(1.4, _gust + 0.4 + _flash);
        }
        _gustTurnIn -= dt;
        _breezeTime += dt;
        var breeze = 9 * Math.Sin(_breezeTime * 0.21) + 4 * Math.Sin(_breezeTime * 0.53 + 1.3);
        var height = ActualHeight;
        for (var i = _petals.Count - 1; i >= 0; i--)
        {
            var p = _petals[i];
            p.Age += dt;
            // swaying down, with the gust's push
            var sway = Math.Sin(p.Age * (1.6 + p.Hue) + p.Hue * 20) * (6 + 4 * p.Hue);
            p.X += (p.Vx + sway + breeze + 28 * _gust * _gustDirection) * dt;
            p.Y += p.Vy * (0.75 + 0.35 * Math.Abs(Math.Sin(p.Age * 2.2 + p.Hue * 9))) * dt;
            if (p.Y > height + 4 || p.X > Right + 10 || p.X < Left - 10 || p.Age > 12) _petals.RemoveAt(i);
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
                Vx = -5 + Random.Shared.NextDouble() * 10,
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

    /// <summary>How many notes rise, against the first tuning.</summary>
    private const double NotesDensity = 0.7;

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
        // (all of it 30% fewer than first tuned: there were too many)
        var burst = beat ? NotesDensity * (1 + 3 * energy) : 0;
        var count = (int)burst + (Random.Shared.NextDouble() < burst % 1 ? 1 : 0)
                    + (Random.Shared.NextDouble() < dt * NotesDensity * (1 + 6 * energy) ? 1 : 0);
        for (var k = 0; k < count && _notes.Count < 17; k++)
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
