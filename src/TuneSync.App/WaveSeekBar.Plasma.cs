using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 플라즈마 볼: the plasma of a plasma ball, spread over the played part - soft wavering
// filaments reaching up and down from the played line.
public sealed partial class WaveSeekBar
{
    private readonly List<Spark> _filaments = [];

    /// <summary>Filaments of plasma all along the played part, spaced evenly: each a smooth,
    /// wavering strand of light from the played line up or down into the air, where its tip
    /// glows like the spot where a plasma ball's filament meets the glass. The tips wander
    /// slowly from side to side and the strands waver faster when the music is loud; louder
    /// music brings more and brighter strands, and a beat makes them all flare. The light is the
    /// cover's color leaning toward a plasma lamp's magenta, pink-white at the core. Paused, it
    /// dims and goes quiet.
    /// (In a filament: X - where along the played part it starts, 0 to 1; Vx - its tip's
    /// sideways lean; Y - the waver's phase; Vy - up (-) or down (+) times the waver's speed;
    /// Size - how far it wavers; Hue - how bright.)</summary>
    private void DrawPlasma(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        var plasma = Mix(accent, Color.FromRgb(0xE0, 0x70, 0xFF), 0.6);
        var pink = Mix(plasma, Colors.White, 0.55);
        var live = Math.Clamp(_activity, 0, 1);

        // the played line glows faintly, as the charged electrode
        if (live > 0.05)
            dc.DrawLine(SolidPen(Color.FromArgb((byte)(0x2C * live * (1 + 0.6 * _flash)), plasma.R, plasma.G, plasma.B), 5), new Point(Left, mid), new Point(thumbX, mid));
        DrawPlayedLine(dc, mid, thumbX, cool, accent, plasma);

        // the strands stay well inside a band a little taller than the strip (no flat edges)
        var reachLimit = ElectricReach(mid) * 0.85;
        var played = thumbX - Left;
        foreach (var f in _filaments)
        {
            var fade = Math.Min(1, Math.Min(f.Age / 0.3, (f.Life - f.Age) / 0.4));
            var bright = Math.Clamp(fade * live * (0.55 + 0.45 * f.Hue) * (1 + 0.6 * _flash), 0, 1);
            if (bright < 0.04) continue;
            var baseX = Left + played * f.X;
            if (baseX > thumbX - 2) continue;
            var side = f.Vy < 0 ? -1 : 1;
            // the tip wanders slowly to and fro around its lean
            var tipX = baseX + f.Vx + 6 * Math.Sin(f.Age * 0.8 + f.Hue * 20);
            var reach = reachLimit * (0.7 + 0.3 * f.Hue);
            var start = new Point(baseX, mid);
            var tip = new Point(Math.Clamp(tipX, Left, thumbX), mid + side * reach);

            var along = tip - start;
            var length = along.Length;
            if (length < 2) continue;
            var across = new Vector(-along.Y / length, along.X / length);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                const int steps = 10;
                g.BeginFigure(start, false, false);
                var points = new List<Point>(steps);
                for (var k = 1; k <= steps; k++)
                {
                    var t = k / (double)steps;
                    // still at both ends, wavering between (smooth, not jagged as lightning is)
                    var wave = Math.Sin(t * Math.PI) * (Math.Sin(f.Y + t * 7) * 0.7 + Math.Sin(f.Y * 1.7 - t * 13) * 0.3);
                    points.Add(start + along * t + across * (wave * f.Size));
                }
                g.PolyLineTo(points, true, true);
            }
            geometry.Freeze();

            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0x34 * bright), plasma.R, plasma.G, plasma.B), 3), geometry);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0xB8 * bright), pink.R, pink.G, pink.B), 1), geometry);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(0xC8 * bright), 0xFF, 0xFF, 0xFF), 0.45), geometry);
            // the glowing tip, and a small bright root on the line
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(0x48 * bright), plasma.R, plasma.G, plasma.B)), null, tip, 2.6, 2.6);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(0xC0 * bright), 0xFF, 0xFF, 0xFF)), null, tip, 1.1, 1.1);
            dc.DrawEllipse(Solid(Color.FromArgb((byte)(0x80 * bright), pink.R, pink.G, pink.B)), null, start, 1.4, 1.4);
        }
    }

    private void UpdatePlasma(double dt, bool beat)
    {
        for (var i = _filaments.Count - 1; i >= 0; i--)
        {
            var f = _filaments[i];
            f.Age += dt;
            f.Y += Math.Abs(f.Vy) * dt * (0.6 + 0.8 * _level);
            if (f.Age >= f.Life) _filaments.RemoveAt(i);
            else _filaments[i] = f;
        }
        if (!Spawning(SeekBarStyle.Plasma)) return;
        var played = PlayedX - Left;
        if (played < 16) return;
        var energy = Energy;
        // about one strand per 30 px of the played part, more when loud
        var room = Math.Clamp((int)(played / 30), 1, 18);
        var wanted = (int)Math.Round(room * (0.45 + 0.55 * energy));
        var add = Math.Max(0, wanted - _filaments.Count) + (beat ? 1 + (int)(2 * energy) : 0);
        var mid = ActualHeight / 2;
        for (var n = 0; n < add && _filaments.Count < 22; n++)
        {
            _filaments.Add(new Spark
            {
                X = EvenSpot(),
                Vx = (Random.Shared.NextDouble() - 0.5) * 2 * ElectricReach(mid) * 0.6,
                Y = Random.Shared.NextDouble() * 20,
                Vy = (Random.Shared.NextDouble() < 0.5 ? -1 : 1) * (4 + Random.Shared.NextDouble() * 5),
                Size = 1 + Random.Shared.NextDouble() * 1.6,
                Life = 0.9 + Random.Shared.NextDouble() * 2.2,
                Hue = Random.Shared.NextDouble(),
            });
        }
    }

    /// <summary>Where along the played part (0 to 1) a new strand starts: of a few random
    /// spots, the one farthest from the strands already there, so they spread out evenly.</summary>
    private double EvenSpot()
    {
        var best = Random.Shared.NextDouble();
        var bestGap = -1.0;
        for (var c = 0; c < 4; c++)
        {
            var spot = 0.02 + Random.Shared.NextDouble() * 0.96;
            var gap = 1.0;
            foreach (var f in _filaments) gap = Math.Min(gap, Math.Abs(f.X - spot));
            if (gap > bestGap) (best, bestGap) = (spot, gap);
        }
        return best;
    }
}
