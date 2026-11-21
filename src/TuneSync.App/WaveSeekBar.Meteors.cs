using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 별똥별: shooting stars over the played part. Particles moved in UpdateNature and drawn with the
// kept brushes and streaks (Solid, SolidPen, DrawStreak), like the other nature looks.
public sealed partial class WaveSeekBar
{
    private readonly List<Spark> _meteors = [];

    /// <summary>How long a shooting star twinkles where it appears before it sets off (seconds).</summary>
    private const double MeteorTwinkle = 0.3;

    /// <summary>Thin shooting stars, always from the left: each first twinkles for a moment where
    /// it appears up in the air, then sets off down and to the right at a slant - slowly at first,
    /// faster through the middle and fastest at the end - and fades out as it passes, just above
    /// the line. Its tail grows with its speed. Now and then one falls; louder music brings more,
    /// a beat sends one off, and a strong beat three, one after another.
    /// (In a meteor: X, Y - where it appears; Vx, Vy - the way to where it ends; Age - seconds
    /// since it appeared (below 0 while waiting its turn); Life - how long it flies; Size - its
    /// width; Hue - its color.)</summary>
    private void DrawMeteors(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
        foreach (var m in _meteors)
        {
            if (m.Age < 0) continue;
            var tone = MeteorTone(accent, m.Hue);
            var start = new Point(m.X, m.Y);

            // the twinkle where it appears: a little four-pointed star that swells and fades
            if (m.Age < MeteorTwinkle + 0.15)
            {
                var t = m.Age / (MeteorTwinkle + 0.15);
                var glow = Math.Sin(Math.PI * Math.Min(1, t)); // up and down again
                var arm = (2.5 + 3 * glow) * EffectScale;
                var pen = SolidPen(Color.FromArgb((byte)(200 * glow), tone.R, tone.G, tone.B), 0.6);
                dc.DrawLine(pen, new Point(start.X - arm, start.Y), new Point(start.X + arm, start.Y));
                dc.DrawLine(pen, new Point(start.X, start.Y - arm), new Point(start.X, start.Y + arm));
                dc.DrawEllipse(Solid(Color.FromArgb((byte)(240 * glow), 0xFF, 0xFF, 0xFF)), null, start, 0.9 + 0.5 * glow, 0.9 + 0.5 * glow);
            }
            if (m.Age < MeteorTwinkle) continue;

            // flying: eased in - slow from the start, then ever faster to the end
            var u = Math.Min(1, (m.Age - MeteorTwinkle) / m.Life);
            var along = u * u;
            var speed = 2 * u; // how fast it goes now, against its average
            var head = new Point(m.X + m.Vx * along, m.Y + m.Vy * along);
            // fading in as it sets off and out over the last stretch
            var alpha = Math.Min(1, u / 0.1) * Math.Min(1, (1 - u) / 0.3);
            if (alpha <= 0.02) continue;
            // a tail as long as the way it's covering right now
            var reach = 0.08 + 0.2 * speed;
            var tail = new Point(head.X - m.Vx * reach * 0.5, head.Y - m.Vy * reach * 0.5);
            DrawStreak(dc, tail, head, Color.FromArgb((byte)(200 * alpha), tone.R, tone.G, tone.B), m.Size, roundHead: false);
            var core = Mix(tone, Colors.White, 0.75);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(255 * alpha), core.R, core.G, core.B)), null, head, m.Size * 0.6, m.Size * 0.6);
        }
    }

    /// <summary>A shooting star's color: the cover's color pulled a little round the wheel by
    /// <paramref name="hue"/> and made pale, as starlight is (in steps, so the brushes stay few).</summary>
    private static Color MeteorTone(Color accent, double hue) =>
        Mix(Shift(accent, Math.Round((hue - 0.5) * 4) * 15, 1.25), Colors.White, 0.4);

    private void UpdateMeteors(double dt, bool beat)
    {
        for (var i = _meteors.Count - 1; i >= 0; i--)
        {
            var m = _meteors[i];
            m.Age += dt;
            if (m.Age >= MeteorTwinkle + m.Life) _meteors.RemoveAt(i);
            else _meteors[i] = m;
        }

        var thumbX = PlayedX;
        if (!Spawning(SeekBarStyle.Meteor) || thumbX - Left < 90) return;
        var energy = Energy;
        var count = (Random.Shared.NextDouble() < dt * (0.5 + 2.2 * energy) ? 1 : 0)
                    + (beat ? (_beatStrength >= StrongBeat ? 3 : 1) : 0);
        var mid = ActualHeight / 2;
        for (var n = 0; n < count && _meteors.Count < 8; n++)
        {
            // it ends just above the line somewhere over the played part, having come from up and
            // to the left of there at a slant
            var end = Left + 70 + Random.Shared.NextDouble() * (thumbX - Left - 76);
            var fall = mid + NatureOverflow + 2 - Random.Shared.NextDouble() * 4;
            // a shallow slant, so it streaks a good way across (steeper, it barely moved)
            var slant = (9 + Random.Shared.NextDouble() * 7) * Math.PI / 180;
            var run = Math.Min(fall / Math.Tan(slant), end - Left - 4);
            _meteors.Add(new Spark
            {
                X = end - run,
                Y = mid - fall,
                Vx = run,
                Vy = fall - 3, // ending a few px above the line, where it fades out
                Life = 0.75 + Random.Shared.NextDouble() * 0.3 - 0.15 * Math.Min(1, energy),
                Age = -n * 0.18, // a beat's three go one after another
                Size = (0.8 + Random.Shared.NextDouble() * 0.35) * EffectScale,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }
}
