using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 공룡 달리기: the thumb is a little pixel-art dinosaur (a design of our own) running along the bar
// as its ground. Obstacles - equalizer bars of pixel blocks - come in from the right end over the
// part still to play, as tall as the music will be loud when they reach it, and it jumps each
// one. Nobody plays it: the jumps are worked out so it always clears them. Drawn with kept
// brushes (Solid).
public sealed partial class WaveSeekBar
{
    /// <summary>The obstacles. (In one: X - its middle; Vx - its speed, px a second leftward;
    /// Size - its height in px; Hue - its width in px; Life - 1 once it has been jumped.)</summary>
    private readonly List<Spark> _obstacles = [];

    /// <summary>Dust kicked up on landing. (X, Y - where; Vx, Vy - its drift; Age, Life.)</summary>
    private readonly List<Spark> _dust = [];

    /// <summary>The dinosaur: how high it is off the ground and how fast it's going up (px, px a
    /// second), its running clock (for the legs), and time until the next obstacle.</summary>
    private double _jumpY, _jumpV, _runTime, _nextObstacle = 0.6;

    /// <summary>How fast things fall (px a second squared), how fast the obstacles come, and the
    /// dinosaur's height in px.</summary>
    private const double RunnerGravity = 900, ObstacleSpeed = 135, DinoHeight = 16;

    private bool RunnerAlive => _obstacles.Count > 0 || _dust.Count > 0 || _jumpY > 0;

    private void ClearRunner()
    {
        _obstacles.Clear();
        _dust.Clear();
        _jumpY = _jumpV = 0;
        _nextObstacle = 0.6;
    }

    private void UpdateRunner(double dt)
    {
        if (Look != SeekBarStyle.Runner)
        {
            if (RunnerAlive) ClearRunner();
            return;
        }
        // its world slows to a stop when the music is paused, and picks up again with it
        var t = dt * _activity;
        var dino = PlayedX;

        for (var i = _dust.Count - 1; i >= 0; i--)
        {
            var d = _dust[i];
            d.Age += dt;
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            if (d.Age >= d.Life) _dust.RemoveAt(i);
            else _dust[i] = d;
        }

        for (var i = _obstacles.Count - 1; i >= 0; i--)
        {
            var o = _obstacles[i];
            o.X -= o.Vx * t;
            if (o.X < Left - 10) _obstacles.RemoveAt(i);
            else _obstacles[i] = o;
        }

        // a new obstacle at the far end, as tall as the music will be loud when it gets here
        _nextObstacle -= t;
        if (_nextObstacle <= 0 && IsPlaying && Maximum > 0 && Right - dino > 90 && _obstacles.Count < 12)
        {
            var arrive = (Right - dino) / ObstacleSpeed;
            var level = Waveform is { } waveform ? waveform.LevelAt(CurrentPosition() + arrive) : FallbackLevel;
            var loud = Math.Clamp(Math.Pow(level, 1.3), 0, 1);
            var height = Math.Round((4 + 9 * loud) / 3) * 3; // in whole segments of the bar
            var wide = loud > 0.72 && Random.Shared.NextDouble() < 0.5; // a loud moment: sometimes two side by side
            _obstacles.Add(new Spark { X = Right, Vx = ObstacleSpeed, Size = height, Hue = wide ? 12 : 5 });
            var energy = Energy;
            _nextObstacle = 0.8 + Random.Shared.NextDouble() * 1.1 - 0.3 * Math.Min(1, energy);
        }

        // on the ground: jump the next obstacle so the top of the jump comes over its middle
        if (_jumpY <= 0 && _jumpV <= 0)
        {
            for (var i = 0; i < _obstacles.Count; i++)
            {
                var o = _obstacles[i];
                if (o.Life > 0 || o.X + o.Hue / 2 < dino - 4) continue;
                var apex = o.Size + 5;
                var up = Math.Sqrt(2 * RunnerGravity * apex);
                var half = up / RunnerGravity; // seconds to the top
                if (o.X - dino > o.Vx * half + o.Hue / 2) continue;
                _jumpV = up;
                o.Life = 1;
                _obstacles[i] = o;
                break;
            }
        }
        if (_jumpY > 0 || _jumpV > 0)
        {
            _jumpY += _jumpV * t;
            _jumpV -= RunnerGravity * t;
            if (_jumpY <= 0)
            {
                _jumpY = _jumpV = 0;
                var mid = ActualHeight / 2;
                for (var n = 0; n < 4; n++)
                    _dust.Add(new Spark
                    {
                        X = dino + (n - 1.5) * 3,
                        Y = mid - 1,
                        Vx = (n - 1.5) * 9 - 6,
                        Vy = -6 - Random.Shared.NextDouble() * 6,
                        Life = 0.35,
                    });
            }
        }
        else if (IsPlaying) _runTime += t;
    }

    /// <summary>One pixel of the pixel art, in px (fractional: the dinosaur has to fit the strip).</summary>
    private const double Pixel = 1.5;

    /// <summary>The obstacles, the dust and the dinosaur at <paramref name="thumbX"/> (in place of
    /// the thumb), all as pixel art.</summary>
    private void DrawRunner(DrawingContext dc, double mid, double thumbX, Color accent, Color warm)
    {
        var ground = mid - 1.5;
        // obstacles: columns of pixel blocks, like an equalizer's bars, warming toward the top
        foreach (var o in _obstacles)
        {
            var fade = Math.Clamp((o.X - Left + 10) / 30, 0, 1);
            if (fade <= 0.02) continue;
            var columns = o.Hue > 8 ? 2 : 1;
            var rows = (int)Math.Round(o.Size / Pixel);
            for (var c = 0; c < columns; c++)
            {
                var x = Math.Round(o.X - o.Hue / 2 + c * 4 * Pixel);
                for (var r = 0; r < rows; r++)
                {
                    // every third row a dark seam, so the column reads as stacked blocks
                    var seam = r % 3 == 2;
                    var tone = seam ? Mix(accent, Colors.Black, 0.35) : Mix(accent, warm, rows > 1 ? (double)r / (rows - 1) : 0);
                    dc.DrawRectangle(Solid(Color.FromArgb((byte)(240 * fade), tone.R, tone.G, tone.B)), null,
                        new Rect(x, ground - (r + 1) * Pixel, 3 * Pixel, Pixel));
                }
            }
        }

        foreach (var d in _dust)
        {
            var a = 1 - d.Age / d.Life;
            dc.DrawRectangle(Solid(Color.FromArgb((byte)(170 * a), 0xFF, 0xFF, 0xFF)), null,
                new Rect(Math.Round(d.X), Math.Round(d.Y), Pixel, Pixel));
        }

        DrawDino(dc, thumbX, ground - _jumpY, accent);
    }

    /// <summary>The dinosaur as pixel art, facing right: a chubby little one with spikes down its
    /// back, a pale belly and a short arm. B body, D dark (spikes, mouth, arm, legs), L belly,
    /// E eye. The body is the same in every frame; the legs below it run.</summary>
    private static readonly string[] DinoRows =
    [
        "..........BBB..",
        ".........BBBBB.",
        "....D...BBBEBBB",
        "...DD..BBBBBBBB",
        "..DDBBBBBBBBDD.",
        ".DBBBBBBBBBB...",
        "BBBBBBBBBBLBD..",
        ".BBBBBBBBLLLB..",
        "..BBBBBBLLLLB..",
        "...BBBBBBBBB...",
    ];

    /// <summary>The legs: two running frames, and both tucked up for a jump.</summary>
    private static readonly string[][] DinoLegs =
    [
        ["....DD...DD....", "....DD........."],
        ["....DD...DD....", ".........DD...."],
        ["....DD...DD....", "..............."],
    ];

    private void DrawDino(DrawingContext dc, double x, double ground, Color accent)
    {
        var legs = _jumpY > 0.5 ? DinoLegs[2] : DinoLegs[(int)(_runTime * 11) % 2];
        var rows = DinoRows.Length + legs.Length;
        var width = DinoRows[0].Length;
        // its middle on the thumb's place, its feet on the ground, on whole pixels so it stays crisp
        var left = Math.Round(x - width * Pixel / 2);
        var top = Math.Round(ground - rows * Pixel);
        Brush? Paint(char c) => c switch
        {
            'B' => Solid(Mix(accent, Colors.White, 0.25)),
            'D' => Solid(Mix(accent, Colors.Black, 0.4)),
            'L' => Solid(Mix(accent, Colors.White, 0.68)),
            'E' => Solid(Color.FromRgb(0x1E, 0x1E, 0x24)),
            _ => null,
        };
        for (var r = 0; r < rows; r++)
        {
            var line = r < DinoRows.Length ? DinoRows[r] : legs[r - DinoRows.Length];
            for (var c = 0; c < line.Length; c++)
            {
                if (Paint(line[c]) is not { } brush) continue;
                // a pixel's edges overlap a hair, so no seams show between them
                dc.DrawRectangle(brush, null, new Rect(left + c * Pixel, top + r * Pixel, Pixel + 0.15, Pixel + 0.15));
            }
        }
    }
}
