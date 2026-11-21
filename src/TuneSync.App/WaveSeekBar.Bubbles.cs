using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 비눗방울: soap bubbles blown out of the played part. Like the other nature looks, particles
// moved in UpdateNature and drawn with the kept brushes and pens (Solid, SolidPen).
public sealed partial class WaveSeekBar
{
    private readonly List<Spark> _bubbles = [];
    private readonly List<Spark> _pops = [];

    /// <summary>How long a pop's ring and droplets last.</summary>
    private const double PopTime = 0.24;

    /// <summary>Soap bubbles rising from all along the played part: each swells out of the line,
    /// floats up wobbling, its thin rim shimmering through the colors around the cover's, a
    /// bright glint at its top - and pops: a ring that flies apart into a few droplets. A beat
    /// pops some at once and blows out new ones; loud music, more and bigger bubbles.
    /// (In a bubble: Size - its radius; Hue - where its shimmer starts and how it wobbles.)</summary>
    private void DrawBubbles(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        foreach (var b in _bubbles)
        {
            var grow = 0.3 + 0.7 * SmoothStep(Math.Min(1, b.Age / 0.28)); // blown out of the line
            var alpha = Math.Min(1, b.Age / 0.12);
            if (alpha <= 0.03) continue;
            var r = b.Size * grow;
            // a soft wobble: wider while flatter, and back
            var wobble = 0.07 * Math.Sin(b.Age * (7 + 3 * b.Hue) + b.Hue * 40);
            var rx = r * (1 + wobble);
            var ry = r * (1 - wobble);
            var center = new Point(b.X, b.Y);
            var tone = BubbleTone(accent, b);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(26 * alpha), tone.R, tone.G, tone.B)),
                           SolidPen(Color.FromArgb((byte)(170 * alpha), tone.R, tone.G, tone.B), 0.9), center, rx, ry);
            // the window glint up at the left, and a faint one low on the right
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(220 * alpha), 0xFF, 0xFF, 0xFF)), null,
                           new Point(b.X - rx * 0.4, b.Y - ry * 0.42), r * 0.2, r * 0.15);
            if (r > 3)
                dc.DrawEllipse(Solid(Color.FromArgb((byte)(90 * alpha), 0xFF, 0xFF, 0xFF)), null,
                               new Point(b.X + rx * 0.45, b.Y + ry * 0.4), r * 0.1, r * 0.08);
        }

        foreach (var p in _pops)
        {
            var t = p.Age / PopTime;
            var fade = 1 - t;
            var tone = Mix(BubbleTone(accent, p), Colors.White, 0.4);
            // the rim snapping outward
            var ring = p.Size * (1 + 0.5 * t);
            dc.DrawEllipse(null, SolidPen(Color.FromArgb((byte)(160 * fade * fade), tone.R, tone.G, tone.B), 0.8), new Point(p.X, p.Y), ring, ring);
            // droplets flying off it
            var drops = 5;
            var reach = p.Size * (1 + 1.4 * SmoothStep(t));
            var dot = Solid(Color.FromArgb((byte)(230 * fade), tone.R, tone.G, tone.B));
            for (var i = 0; i < drops; i++)
            {
                var angle = (i + p.Hue) * Math.Tau / drops;
                var at = new Point(p.X + Math.Cos(angle) * reach, p.Y + Math.Sin(angle) * reach + 6 * t * t);
                dc.DrawEllipse(dot, null, at, 0.75, 0.75);
            }
        }
    }

    /// <summary>A bubble's rim color now: the colors around the cover's, sliding slowly round the
    /// wheel like the film of a real bubble (in steps, so the kept brushes stay few).</summary>
    private static Color BubbleTone(Color accent, Spark b)
    {
        var degrees = Math.Round((b.Hue * 300 + b.Age * 140) / 24) * 24;
        return Mix(Shift(accent, degrees, 1.3), Colors.White, 0.3);
    }

    private void UpdateBubbles(double dt, bool beat)
    {
        for (var i = _pops.Count - 1; i >= 0; i--)
        {
            var p = _pops[i];
            p.Age += dt;
            if (p.Age >= PopTime) _pops.RemoveAt(i);
            else _pops[i] = p;
        }

        var thumbX = PlayedX;
        var popOnBeat = beat && Look == SeekBarStyle.Bubbles ? 1 + (int)(2 * Energy) : 0;
        for (var i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            b.Age += dt;
            b.Y += b.Vy * dt;
            b.Vy *= Math.Exp(-dt * 0.35); // rising ever more gently
            b.X += (b.Vx + Math.Sin(b.Age * (2.2 + b.Hue) + b.Hue * 25) * 5) * dt;
            // a beat pops a few of the grown ones
            if (popOnBeat > 0 && b.Age > 0.45 && Random.Shared.NextDouble() < 0.5)
            {
                popOnBeat--;
                b.Age = b.Life;
            }
            if (b.X > thumbX + 12 || Look != SeekBarStyle.Bubbles) _bubbles.RemoveAt(i); // the song jumped back past it
            else if (b.Age >= b.Life)
            {
                _bubbles.RemoveAt(i);
                if (_pops.Count < 12) _pops.Add(b with { Age = 0, Size = b.Size });
            }
            else _bubbles[i] = b;
        }

        if (!Spawning(SeekBarStyle.Bubbles) || thumbX - Left < 16) return;
        var energy = Energy;
        var mid = ActualHeight / 2;
        var count = (beat ? 1 + (int)(2 * energy) : 0) + (Random.Shared.NextDouble() < dt * (1.8 + 5 * energy) ? 1 : 0);
        for (var n = 0; n < count && _bubbles.Count < 18; n++)
        {
            _bubbles.Add(new Spark
            {
                X = Left + 6 + Random.Shared.NextDouble() * (thumbX - Left - 10),
                Y = mid - 1,
                Vx = (Random.Shared.NextDouble() - 0.5) * 6,
                Vy = -(9 + Random.Shared.NextDouble() * 7 + 8 * energy),
                Life = 1.2 + Random.Shared.NextDouble() * 1.3,
                Size = (2.4 + Random.Shared.NextDouble() * 2.6 + 1.6 * energy) * EffectScale,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }
}
