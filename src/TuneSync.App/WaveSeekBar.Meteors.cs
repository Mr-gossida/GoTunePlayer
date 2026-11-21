using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 별똥별: shooting stars falling onto the played part. Particles moved in UpdateNature and drawn
// with the kept brushes and streaks (Solid, DrawStreak), like the other nature looks.
public sealed partial class WaveSeekBar
{
    private readonly List<Spark> _meteors = [];
    private readonly List<Spark> _impacts = [];

    /// <summary>Which way the shower falls (+1 toward the thumb, -1 back): every star of a shower
    /// keeps to it, like a real meteor shower; it turns now and then.</summary>
    private double _showerDirection = 1;
    private double _showerTurnIn;

    /// <summary>How long a landing's flash and sparks last.</summary>
    private const double ImpactTime = 0.4;

    /// <summary>Shooting stars falling at a slant out of the air above onto the played part, each a
    /// bright head with a long fading tail; where one lands on the line it flashes and throws up a
    /// few sparks. Now and then one falls; louder music brings more, a beat sends one down, and
    /// a strong beat a whole shower. All of a shower fall the same way.
    /// (In a meteor: X, Y - its head; Vx, Vy - its speed; Size - its width; Hue - its color.
    /// In an impact: X - where it landed; Size - how bright; Hue - its color.)</summary>
    private void DrawMeteors(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        foreach (var m in _meteors)
        {
            var fadeIn = Math.Min(1, m.Age / 0.08);
            if (fadeIn <= 0.03) continue;
            var tone = MeteorTone(accent, m.Hue);
            var head = new Point(m.X, m.Y);
            // a tail as long as a tenth of a second's flight, a fainter and longer glow behind it
            var tail = new Point(m.X - m.Vx * 0.1, m.Y - m.Vy * 0.1);
            var trail = new Point(m.X - m.Vx * 0.2, m.Y - m.Vy * 0.2);
            DrawStreak(dc, trail, head, Color.FromArgb((byte)(70 * fadeIn), tone.R, tone.G, tone.B), m.Size * 2.6, roundHead: false);
            DrawStreak(dc, tail, head, Color.FromArgb((byte)(220 * fadeIn), tone.R, tone.G, tone.B), m.Size);
            var core = Mix(tone, Colors.White, 0.7);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(255 * fadeIn), core.R, core.G, core.B)), null, head, m.Size * 0.75, m.Size * 0.75);
        }

        foreach (var hit in _impacts)
        {
            var t = hit.Age / ImpactTime;
            var fade = (1 - t) * hit.Size;
            var tone = Mix(MeteorTone(accent, hit.Hue), Colors.White, 0.35);
            var at = new Point(hit.X, mid);
            // the flash on the line, wide and short
            var flash = 1 - Math.Min(1, t / 0.45);
            if (flash > 0.02)
            {
                dc.DrawEllipse(Solid(Color.FromArgb((byte)(70 * flash * hit.Size), tone.R, tone.G, tone.B)), null, at, 9 * EffectScale, 4 * EffectScale);
                dc.DrawEllipse(Solid(Color.FromArgb((byte)(200 * flash * hit.Size), 0xFF, 0xFF, 0xFF)), null, at, 2.2, 1.6);
            }
            // a few sparks thrown up and falling back
            var dot = Solid(Color.FromArgb((byte)(230 * fade), tone.R, tone.G, tone.B));
            for (var i = 0; i < 5; i++)
            {
                var angle = Math.PI * (0.12 + 0.76 * (i + hit.Hue * 0.6) / 5); // fanned out over the line
                var speed = (34 + 18 * ((i * 0.37 + hit.Hue) % 1)) * EffectScale;
                var x = hit.X - Math.Cos(angle) * speed * hit.Age;
                var y = mid - Math.Sin(angle) * speed * hit.Age + 160 * hit.Age * hit.Age;
                if (y > mid) continue;
                dc.DrawEllipse(dot, null, new Point(x, y), 0.8, 0.8);
            }
        }
    }

    /// <summary>A shooting star's color: the cover's color pulled a little round the wheel by
    /// <paramref name="hue"/> and made pale, as starlight is (in steps, so the brushes stay few).</summary>
    private static Color MeteorTone(Color accent, double hue) =>
        Mix(Shift(accent, Math.Round((hue - 0.5) * 4) * 15, 1.25), Colors.White, 0.4);

    private void UpdateMeteors(double dt, bool beat)
    {
        var mid = ActualHeight / 2;
        for (var i = _impacts.Count - 1; i >= 0; i--)
        {
            var hit = _impacts[i];
            hit.Age += dt;
            if (hit.Age >= ImpactTime) _impacts.RemoveAt(i);
            else _impacts[i] = hit;
        }

        var thumbX = PlayedX;
        for (var i = _meteors.Count - 1; i >= 0; i--)
        {
            var m = _meteors[i];
            m.Age += dt;
            m.X += m.Vx * dt;
            m.Y += m.Vy * dt;
            if (m.Y >= mid - 1)
            {
                _meteors.RemoveAt(i);
                // landed on the line: a flash and sparks (unless the song jumped back past it)
                if (m.X <= thumbX + 4 && _impacts.Count < 10)
                    _impacts.Add(new Spark { X = m.X, Size = Math.Min(1, 0.55 + 0.5 * m.Size / 2), Hue = m.Hue });
            }
            else if (m.Age > 2) _meteors.RemoveAt(i);
            else _meteors[i] = m;
        }

        _showerTurnIn -= dt;
        if (!Spawning(SeekBarStyle.Meteor) || thumbX - Left < 30) return;
        if (_showerTurnIn <= 0)
        {
            _showerDirection = Random.Shared.NextDouble() < 0.7 ? 1 : -1; // mostly falling toward the thumb
            _showerTurnIn = 6 + Random.Shared.NextDouble() * 8;
        }
        var energy = Energy;
        var count = (Random.Shared.NextDouble() < dt * (0.5 + 2.5 * energy) ? 1 : 0)
                    + (beat ? (_beatStrength >= StrongBeat ? 3 : 1) : 0);
        for (var n = 0; n < count && _meteors.Count < 8; n++)
        {
            // aimed at a spot on the played part, starting up in the air at a slant
            var land = Left + 8 + Random.Shared.NextDouble() * (thumbX - Left - 14);
            var slant = (24 + Random.Shared.NextDouble() * 16) * Math.PI / 180;
            var speed = (150 + Random.Shared.NextDouble() * 90 + 60 * energy) * EffectScale;
            var vx = Math.Cos(slant) * speed * _showerDirection;
            var vy = Math.Sin(slant) * speed;
            // the drop from where it appears (a little above the strip) to the line; a beat's
            // shower is staggered so its stars don't land at once
            var fall = mid + NatureOverflow + 4 + n * 9;
            var time = fall / vy;
            _meteors.Add(new Spark
            {
                X = land - vx * time,
                Y = mid - fall,
                Vx = vx,
                Vy = vy,
                Size = (1.3 + Random.Shared.NextDouble() * 0.7 + 0.5 * energy) * EffectScale,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }
}
