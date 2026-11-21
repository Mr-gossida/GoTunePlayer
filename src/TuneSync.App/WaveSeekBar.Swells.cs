using TuneSync.Core.Models;

namespace TuneSync.App;

// 물결's swells: the music makes new waves rather than swelling the ones already there. Each is a
// smooth hill born by the thumb as big as the music is loud right then, rising as it leaves and
// rolling back along the played part, fading as it goes, and dying away as it nears the start of
// the bar. (It used to bounce off the start and roll back, which crossed the new ones and made the
// water move oddly.) Under them the water keeps a small lapping of its own.
public sealed partial class WaveSeekBar
{
    /// <summary>The swells on the water. (In a swell: X - its crest; Vx - its speed, - away from
    /// the thumb; Size - its height, a share of the strip's half height; Hue -
    /// its half width in px; Age - seconds since it was born.)</summary>
    private readonly List<Spark> _swells = [];

    /// <summary>Time since the last swell was born.</summary>
    private double _sinceSwell;

    /// <summary>How fast the swells roll (px a second), how often a new one is born, and how
    /// quickly they die down as they go (per second).</summary>
    private const double SwellSpeed = 115, SwellEvery = 0.5, SwellDamping = 0.22;

    private void UpdateSwells(double dt, bool beat)
    {
        var wall = PlayedX;
        for (var i = _swells.Count - 1; i >= 0; i--)
        {
            var s = _swells[i];
            s.Age += dt;
            s.X += s.Vx * dt;
            s.Size *= Math.Exp(-dt * SwellDamping);
            // gone flat, at the start of the bar, or past the thumb after the song jumped back
            if (s.Size < 0.03 || s.X < Left || s.X > wall || wall - Left < 30) _swells.RemoveAt(i);
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
    /// strip's half height.</summary>
    private double SwellHeight(double x, double wall)
    {
        var y = 0.0;
        foreach (var s in _swells)
        {
            var reach = 3 * s.Hue;
            // rising as it leaves the thumb (about 55 px), and sinking away over the last 60 px
            // before the start of the bar
            var size = s.Size * SmoothStep(Math.Min(1, s.Age / 0.5)) * SmoothStep(Math.Clamp((s.X - Left) / 60, 0, 1));
            if (Math.Abs(x - s.X) < reach)
            {
                var d = (x - s.X) / s.Hue;
                y += size * Math.Exp(-d * d);
            }
        }
        return y;
    }
}
