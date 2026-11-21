using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 별똥별: shooting stars over the played part. Particles moved in UpdateNature and drawn with the
// kept brushes and streaks (Solid, SolidPen, DrawStreak), like the other nature looks.
public sealed partial class WaveSeekBar
{
    private readonly List<Spark> _meteors = [];

    /// <summary>How long a shooting star's sparkle lasts where it ends (seconds), and how far the
    /// sparkle turns meanwhile (degrees).</summary>
    private const double MeteorSparkle = 0.45, SparkleTurn = 45;

    /// <summary>Thin shooting stars, always from the left: each appears softly up in the air and
    /// sets off down and to the right at a slant - slowly at first, faster through the middle and
    /// fastest at the end - and as it vanishes just above the line, it sparkles there: a little
    /// four-pointed star that swells and fades, turning a little as it does. Its tail grows with
    /// its speed. Now and then one falls; louder music brings more, a beat sometimes sends one
    /// off, and a strong beat two, one after another.
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

            if (m.Age < m.Life)
            {
                // flying: eased in - slow from the start, then ever faster to the end
                var u = m.Age / m.Life;
                var along = u * u;
                var speed = 2 * u; // how fast it goes now, against its average
                var head = new Point(m.X + m.Vx * along, m.Y + m.Vy * along);
                // appearing softly over the first third, and gone as it reaches the end (where
                // the sparkle takes over)
                var alpha = SmoothStep(Math.Min(1, u / 0.35)) * Math.Min(1, (1 - u) / 0.12);
                if (alpha > 0.02)
                {
                    // a tail as long as the way it's covering right now
                    var reach = 0.08 + 0.2 * speed;
                    var tail = new Point(head.X - m.Vx * reach * 0.5, head.Y - m.Vy * reach * 0.5);
                    DrawStreak(dc, tail, head, Color.FromArgb((byte)(200 * alpha), tone.R, tone.G, tone.B), m.Size, roundHead: false);
                    var core = Mix(tone, Colors.White, 0.75);
                    dc.DrawEllipse(Solid(Color.FromArgb((byte)(255 * alpha), core.R, core.G, core.B)), null, head, m.Size * 0.6, m.Size * 0.6);
                }
            }

            // the sparkle where it vanishes, starting a moment before it gets there
            var s = (m.Age - (m.Life - 0.08)) / MeteorSparkle;
            if (s <= 0 || s >= 1) continue;
            var end = new Point(m.X + m.Vx, m.Y + m.Vy);
            var glow = Math.Sin(Math.PI * s); // up and down again
            var arm = (2 + 3.5 * glow) * EffectScale;
            var turn = new RotateTransform(SparkleTurn * s, end.X, end.Y);
            turn.Freeze();
            dc.PushTransform(turn);
            var pen = SolidPen(Color.FromArgb((byte)(210 * glow), tone.R, tone.G, tone.B), 0.6);
            dc.DrawLine(pen, new Point(end.X - arm, end.Y), new Point(end.X + arm, end.Y));
            dc.DrawLine(pen, new Point(end.X, end.Y - arm), new Point(end.X, end.Y + arm));
            dc.Pop();
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(240 * glow), 0xFF, 0xFF, 0xFF)), null, end, 0.8 + 0.6 * glow, 0.8 + 0.6 * glow);
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
            if (m.Age >= m.Life - 0.08 + MeteorSparkle) _meteors.RemoveAt(i);
            else _meteors[i] = m;
        }

        var thumbX = PlayedX;
        if (!Spawning(SeekBarStyle.Meteor) || Right - Left < 90) return;
        // over the played part - or early in a song, while that is still short, over the first
        // half of the bar (only over the played part, none fell for the first sixth of a song
        // on the screen saver's bar)
        var reach = Math.Max(thumbX, Left + (Right - Left) / 2);
        var energy = Energy;
        // (fewer than first tuned: about 40% fewer on their own, a beat sends one only half the
        // time, and a strong beat two)
        var count = (Random.Shared.NextDouble() < dt * (0.3 + 1.3 * energy) ? 1 : 0)
                    + (beat ? (_beatStrength >= StrongBeat ? 2 : Random.Shared.NextDouble() < 0.5 ? 1 : 0) : 0);
        var mid = ActualHeight / 2;
        for (var n = 0; n < count && _meteors.Count < 8; n++)
        {
            // it ends just above the line somewhere over the played part, having come from up and
            // to the left of there at a slant
            var end = Left + 70 + Random.Shared.NextDouble() * (reach - Left - 76);
            var fall = mid + NatureOverflow + 2 - Random.Shared.NextDouble() * 4;
            // a shallow slant, so it streaks a good way across (steeper, it barely moved)
            var slant = (9 + Random.Shared.NextDouble() * 7) * Math.PI / 180;
            var run = Math.Min(fall / Math.Tan(slant), end - Left - 4);
            _meteors.Add(new Spark
            {
                X = end - run,
                Y = mid - fall,
                Vx = run,
                Vy = fall - 3, // ending a few px above the line, where it sparkles
                Life = 0.75 + Random.Shared.NextDouble() * 0.3 - 0.15 * Math.Min(1, energy),
                Age = -n * 0.18, // a strong beat's two go one after another
                Size = (0.8 + Random.Shared.NextDouble() * 0.35) * EffectScale,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }
}
