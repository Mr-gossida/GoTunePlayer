using TuneSync.Core.Models;

namespace TuneSync.App;

// 물결's swells: the music makes new waves rather than swelling the ones already there. Each is a
// smooth hill born by the thumb as big as the music is loud right then, rising as it leaves and
// rolling back along the played part; at the start of the bar it runs into the corner as into a
// wall, heaps up and rolls back toward the thumb, where it dies away. It fades as it goes, a
// little more at the wall. Under them the water keeps a small lapping of its own.
public sealed partial class WaveSeekBar
{
    /// <summary>The swells on the water. (In a swell: X - its crest; Vx - its speed, - away from
    /// the thumb, + on its way back; Size - its height, a share of the strip's half height; Hue -
    /// its half width in px; Age - seconds since it was born.)</summary>
    private readonly List<Spark> _swells = [];

    /// <summary>Time since the last swell was born.</summary>
    private double _sinceSwell;

    /// <summary>How fast the swells roll (px a second), how often a new one is born, what's left
    /// of one after it meets a wall, and how quickly they die down as they go (per second).</summary>
    private const double SwellSpeed = 115, SwellEvery = 0.5, SwellBounce = 0.7, SwellDamping = 0.22;

    private void UpdateSwells(double dt, bool beat)
    {
        var wall = PlayedX;
        for (var i = _swells.Count - 1; i >= 0; i--)
        {
            var s = _swells[i];
            s.Age += dt;
            s.X += s.Vx * dt;
            s.Size *= Math.Exp(-dt * SwellDamping);
            // meeting the corner at the start of the bar, it turns round
            if (s.Vx < 0 && s.X < Left)
            {
                s.X = 2 * Left - s.X;
                s.Vx = -s.Vx;
                s.Size *= SwellBounce;
            }
            // gone flat, back at the thumb, or past it after the song jumped back
            if (s.Size < 0.03 || s.X > wall || wall - Left < 30) _swells.RemoveAt(i);
            else _swells[i] = s;
        }

        if (Look != SeekBarStyle.Wave || !IsPlaying || _activity < 0.3 || Maximum <= 0 || wall - Left < 40) return;
        _sinceSwell += dt;
        // one every half second as loud as the music is then, and one on a beat (not too close
        // to the last), a little taller - born just behind the thumb
        if (_sinceSwell < SwellEvery && !(beat && _sinceSwell > 0.18)) return;
        _sinceSwell = 0;
        var height = Math.Clamp(Loud(0.02, 0.62, _level), 0, 0.85) * (beat ? 1 + Math.Min(0.35, _beatStrength) : 1);
        if (height < 0.06 || _swells.Count >= 40) return;
        _swells.Add(new Spark
        {
            X = wall - 4,
            Vx = -SwellSpeed,
            Size = height,
            // narrow enough that each crest stands apart (wider, they ran together into one flat heap)
            Hue = 13 + Random.Shared.NextDouble() * 5,
        });
    }

    /// <summary>How high the swells lift the water at <paramref name="x"/>, as a share of the
    /// strip's half height. A swell nearing the corner also counts its mirror image beyond it, so
    /// it heaps up there and turns back smoothly instead of being cut off; one coming back dies
    /// away over the last stretch before the thumb, so the water by the thumb stays calm.</summary>
    private double SwellHeight(double x, double wall)
    {
        var y = 0.0;
        foreach (var s in _swells)
        {
            var reach = 3 * s.Hue;
            // rising as it leaves the thumb (about 55 px), and on the way back sinking before it
            var size = s.Size * SmoothStep(Math.Min(1, s.Age / 0.5))
                       * (s.Vx > 0 ? SmoothStep(Math.Clamp((wall - s.X) / 90, 0, 1)) : 1);
            if (Math.Abs(x - s.X) < reach)
            {
                var d = (x - s.X) / s.Hue;
                y += size * Math.Exp(-d * d);
            }
            if (s.Vx < 0)
            {
                var d = (x - (2 * Left - s.X)) / s.Hue;
                if (Math.Abs(d) < 3) y += size * SwellBounce * Math.Exp(-d * d);
            }
        }
        return y;
    }
}
