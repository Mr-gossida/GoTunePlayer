using System.Windows;
using System.Windows.Media;

namespace TuneSync.App;

// 실크 리본: ribbons of silk tied to the thumb, streaming back over the played part. No
// particles: each frame works the ribbons out from the music's level and the flowing phase.
public sealed partial class WaveSeekBar
{
    /// <summary>One ribbon's shape: wavelength and speed of its sway, how far it swings, where its
    /// wave starts, and the wavelength and speed of its twist.</summary>
    private readonly record struct RibbonForm(double Wavelength, double Speed, double Swing, double Phase, double TwistLength, double TwistSpeed);

    private static readonly RibbonForm[] Ribbons =
    [
        new(170, 2.0, 0.65, 0.0, 95, 1.3),
        new(125, 2.6, 0.85, 2.1, 70, 1.8),
        new(150, 1.6, 1.0, 4.2, 120, 0.9),
    ];

    /// <summary>The played part as the tail of a few silk ribbons tied to the thumb: each sways
    /// in long waves running back from the thumb and twists as it goes - narrowing to an edge
    /// where it turns over - so they wind round the line and each other. Louder music: they
    /// swing wider and wave faster; a beat flicks them out. Paused, they settle onto the line.
    /// Drawn as one filled band and its two edges per ribbon, built anew each frame.</summary>
    private void DrawRibbons(DrawingContext dc, double mid, double thumbX, Color cool, Color accent, Color warm)
    {
        var energy = Energy;
        var reach = (mid + 4) * _activity * (0.55 + 0.6 * Math.Min(1, energy) + 0.3 * _flash);
        // no further than a few pixels past the strip at the top of a swing (1.3 with its overtone)
        reach = Math.Min(reach, (mid + 6) / 1.3);
        var width = (5 + 3 * Math.Min(1, energy)) * EffectScale;
        Color[] tones = [accent, warm, cool];

        // the back ribbon under the line, the other two over it
        for (var k = Ribbons.Length - 1; k >= 0; k--)
        {
            if (k == 1) DrawPlayedLine(dc, mid, thumbX, cool, accent, warm);
            if (reach < 0.3) continue;
            var form = Ribbons[k];
            var band = new StreamGeometry();
            var edges = new StreamGeometry();
            var top = new List<Point>(160);
            var bottom = new List<Point>(160);
            for (var x = thumbX; ; x -= 4)
            {
                x = Math.Max(x, Left);
                var behind = thumbX - x;
                // tied at the thumb, and fading into the line at the very start
                var envelope = SmoothStep(Math.Min(1, behind / 70)) * SmoothStep(Math.Min(1, (x - Left) / 40));
                var wave = Math.Tau * behind / form.Wavelength - _phase * form.Speed + form.Phase;
                var y = mid + reach * form.Swing * envelope * (Math.Sin(wave) + 0.3 * Math.Sin(2.3 * wave + form.Phase * 1.7));
                // twisting: the band narrows to its edge and turns over (the edges cross there)
                var twist = Math.Cos(Math.Tau * behind / form.TwistLength - _phase * form.TwistSpeed + form.Phase * 2);
                var half = width / 2 * (0.2 + 0.8 * envelope) * twist;
                top.Add(new Point(x, y - half));
                bottom.Add(new Point(x, y + half));
                if (x <= Left) break;
            }
            if (top.Count < 2) continue;
            using (var g = band.Open())
            {
                g.BeginFigure(top[0], true, true);
                g.PolyLineTo(top.GetRange(1, top.Count - 1), false, true);
                bottom.Reverse();
                g.PolyLineTo(bottom, false, true);
                bottom.Reverse();
            }
            using (var g = edges.Open())
            {
                Polyline(g, top);
                Polyline(g, bottom);
            }
            band.Freeze();
            edges.Freeze();

            var tone = Mix(tones[k], Colors.White, 0.15 + 0.25 * _flash);
            var sheen = Math.Min(1, 0.55 + 0.35 * energy + 0.3 * _flash) * _activity;
            dc.DrawGeometry(Solid(Color.FromArgb((byte)(110 * sheen), tone.R, tone.G, tone.B)), null, band);
            dc.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(220 * sheen), tone.R, tone.G, tone.B), 0.8), edges);
        }
    }
}
