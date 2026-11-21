using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 오리: the thumb is a little pixel-art duck (a design of our own, like everything here) running
// along the bar as its ground. Things come at it from the right end over the part still to play,
// picked by how loud the music will be when they reach it: a log when it's quiet, a hay bale, a
// wooden barrel when it's loud; now and then a fox running at it, and in loud passages an eagle
// swooping in at head height. It jumps the ones on the ground and runs ducked under the eagle.
// Nobody plays it: every move is worked out so it always gets by. Every pixel is a whole number
// of screen pixels on the screen's own grid, so the art stays crisp (a pixel of 1.5 px was smudged
// at its edges and didn't read as pixel art). Drawn with kept brushes (Solid).
public sealed partial class WaveSeekBar
{
    /// <summary>What's coming at the duck. (In one: X - its middle; Vx - its speed, px a second
    /// leftward; Hue - which thing it is (<see cref="Hazard"/>); Life - 1 once the duck has
    /// jumped it.)</summary>
    private readonly List<Spark> _obstacles = [];

    private enum Hazard { Log, Hay, Barrel, Fox, Eagle }

    /// <summary>Dust kicked up on landing. (X, Y - where; Vx, Vy - its drift; Age, Life.)</summary>
    private readonly List<Spark> _dust = [];

    /// <summary>The duck: how high it is off the ground and how fast it's going up (px, px a
    /// second), its running clock (for the feet), the scene's clock (for the fox and the eagle),
    /// time until the next obstacle, and whether it's ducking.</summary>
    private double _jumpY, _jumpV, _runTime, _sceneTime, _nextObstacle = 0.6;
    private bool _ducking;

    /// <summary>How fast things fall (px a second squared) and how fast the ground comes.</summary>
    private const double RunnerGravity = 900, ObstacleSpeed = 135;

    /// <summary>How much faster than the ground the fox runs and the eagle flies (px a second).</summary>
    private const double FoxRush = 50, EagleRush = 35;

    /// <summary>How high the eagle flies: the bottom of its picture, in art pixels off the ground
    /// (above the ducked duck, 8 pixels tall; into the standing one, 13).</summary>
    private const int EagleAltitude = 9;

    private bool RunnerAlive => _obstacles.Count > 0 || _dust.Count > 0 || _jumpY > 0;

    private void ClearRunner()
    {
        _obstacles.Clear();
        _dust.Clear();
        _jumpY = _jumpV = 0;
        _ducking = false;
        _nextObstacle = 0.6;
    }

    /// <summary>One art pixel in px: a whole number of screen pixels (2 at 100%), so its edges fall
    /// on the screen's own pixels.</summary>
    private double ArtPixel
    {
        get
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return Math.Max(1, Math.Round(1.7 * scale)) / scale;
        }
    }

    /// <summary><paramref name="value"/> (px) moved onto the screen's pixel grid.</summary>
    private double OnGrid(double value)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return Math.Round(value * scale) / scale;
    }

    private static Hazard KindOf(Spark o) => (Hazard)(int)o.Hue;

    private static string[] SpriteOf(Hazard kind, int frame) => kind switch
    {
        Hazard.Log => LogArt,
        Hazard.Hay => HayArt,
        Hazard.Barrel => BarrelArt,
        Hazard.Fox => frame == 0 ? FoxArt : FoxArt2,
        _ => frame == 0 ? EagleArt : EagleArt2,
    };

    /// <summary>An obstacle's half width and height in px.</summary>
    private (double Half, double Height) SizeOf(Spark o)
    {
        var art = SpriteOf(KindOf(o), 0);
        return (art[0].Length * ArtPixel / 2, art.Length * ArtPixel);
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
        _sceneTime += t;
        var duck = PlayedX;
        var pixel = ArtPixel;

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
            if (o.X < Left - 30) _obstacles.RemoveAt(i);
            else _obstacles[i] = o;
        }

        // something new at the far end, picked by how loud the music will be when it gets here
        _nextObstacle -= t;
        if (_nextObstacle <= 0 && IsPlaying && Maximum > 0 && Right - duck > 90 && _obstacles.Count < 12)
        {
            var arrive = (Right - duck) / ObstacleSpeed;
            var level = Waveform is { } waveform ? waveform.LevelAt(CurrentPosition() + arrive) : FallbackLevel;
            var loud = Math.Clamp(Math.Pow(level, 1.3), 0, 1);
            var kind = loud < 0.3 ? Hazard.Log : loud < 0.6 ? Hazard.Hay : Hazard.Barrel;
            var roll = Random.Shared.NextDouble();
            if (loud > 0.55 && roll < 0.3) kind = Hazard.Eagle;
            else if (loud > 0.25 && roll > 0.82) kind = Hazard.Fox;
            var speed = ObstacleSpeed + kind switch { Hazard.Fox => FoxRush, Hazard.Eagle => EagleRush, _ => 0 };
            // the fox and the eagle are faster: not one that would reach the duck too soon after
            // the one ahead (they'd run into each other, or the duck would still be in the air
            // when the eagle came)
            if (speed > ObstacleSpeed && _obstacles.Count > 0 && _obstacles[^1] is var ahead && ahead.X > duck)
            {
                var after = (Right + 12 - duck) / speed - (ahead.X - duck) / ahead.Vx;
                if (after < 0.9)
                {
                    kind = loud < 0.3 ? Hazard.Log : loud < 0.6 ? Hazard.Hay : Hazard.Barrel;
                    speed = ObstacleSpeed;
                }
            }
            _obstacles.Add(new Spark { X = Right + 12, Vx = speed, Hue = (int)kind });
            _nextObstacle = 0.85 + Random.Shared.NextDouble() * 1.1 - 0.3 * Math.Min(1, Energy);
        }

        // ducking while the eagle goes over (from a little before it reaches the duck until it's by)
        _ducking = false;
        foreach (var o in _obstacles)
        {
            if (KindOf(o) != Hazard.Eagle) continue;
            var (half, _) = SizeOf(o);
            if (o.X - half < duck + 10 * pixel && o.X + half > duck - 8 * pixel) _ducking = true;
        }

        // on the ground (and not under an eagle): jump the next thing so the top of the jump comes
        // over its middle
        if (_jumpY <= 0 && _jumpV <= 0 && !_ducking)
        {
            for (var i = 0; i < _obstacles.Count; i++)
            {
                var o = _obstacles[i];
                if (KindOf(o) == Hazard.Eagle) continue;
                var (half, height) = SizeOf(o);
                if (o.Life > 0 || o.X + half < duck - 4) continue;
                var apex = height + 5 * pixel; // 5 art pixels to spare
                var up = Math.Sqrt(2 * RunnerGravity * apex);
                var rise = up / RunnerGravity; // seconds to the top
                // the top of the jump a little past its middle: the feet sit a little behind the
                // duck's middle, and landing came within 2 px of the back of a wide one
                if (o.X - duck > o.Vx * rise + half - 3 * pixel) continue;
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
                        X = duck + (n - 1.5) * 3,
                        Y = mid - 2,
                        Vx = (n - 1.5) * 9 - 6,
                        Vy = -6 - Random.Shared.NextDouble() * 6,
                        Life = 0.35,
                    });
            }
        }
        else if (IsPlaying) _runTime += t;
    }

    /// <summary>The obstacles, the dust and the duck at <paramref name="thumbX"/> (in place of the
    /// thumb), all as pixel art on the screen's pixel grid.</summary>
    private void DrawRunner(DrawingContext dc, double mid, double thumbX, Color accent, Color warm)
    {
        var pixel = ArtPixel;
        var ground = OnGrid(mid - 1.5);
        foreach (var o in _obstacles)
        {
            var kind = KindOf(o);
            // running legs and flapping wings
            var frame = kind == Hazard.Fox ? (int)(_sceneTime * 12) % 2 : kind == Hazard.Eagle ? (int)(_sceneTime * 5) % 2 : 0;
            var art = SpriteOf(kind, frame);
            var fade = Math.Clamp((o.X - Left + 30) / 40, 0, 1);
            var bottom = kind == Hazard.Eagle ? ground - EagleAltitude * pixel : ground;
            DrawSprite(dc, art, OnGrid(o.X - art[0].Length * pixel / 2), bottom, pixel, fade);
        }

        foreach (var d in _dust)
        {
            var a = 1 - d.Age / d.Life;
            dc.DrawRectangle(Solid(Color.FromArgb((byte)(190 * a), 0xFF, 0xFF, 0xFF)), null,
                new Rect(OnGrid(d.X), OnGrid(d.Y), pixel, pixel));
        }

        // the duck: running, tucked up in a jump, or ducked under the eagle
        var step = (int)(_runTime * 10) % 2;
        string[] body, feet;
        if (_ducking && _jumpY <= 0)
        {
            body = DuckLowArt;
            feet = DuckLowFeet[step];
        }
        else
        {
            body = DuckArt;
            feet = _jumpY > 0.5 ? DuckFeet[2] : DuckFeet[step];
        }
        var rows = body.Length + feet.Length;
        var left = OnGrid(thumbX - body[0].Length * pixel / 2);
        var top = OnGrid(ground - _jumpY) - rows * pixel;
        DrawSprite(dc, body, left, top + body.Length * pixel, pixel, 1);
        DrawSprite(dc, feet, left, top + rows * pixel, pixel, 1);
    }

    /// <summary>Pixel art from rows of letters (see <see cref="ArtColor"/>; '.' is clear), its
    /// bottom-left corner at (<paramref name="left"/>, <paramref name="bottom"/>).</summary>
    private void DrawSprite(DrawingContext dc, string[] art, double left, double bottom, double pixel, double alpha)
    {
        var top = bottom - art.Length * pixel;
        for (var r = 0; r < art.Length; r++)
        {
            var line = art[r];
            for (var c = 0; c < line.Length; c++)
            {
                if (ArtColor(line[c]) is not { } color) continue;
                dc.DrawRectangle(Solid(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B)), null,
                    new Rect(left + c * pixel, top + r * pixel, pixel, pixel));
            }
        }
    }

    /// <summary>The colors the art is drawn in, one letter each.</summary>
    private static Color? ArtColor(char c) => c switch
    {
        // the duck: outline, body, wing, eye, beak and feet
        'K' => Color.FromRgb(0x6B, 0x45, 0x10),
        'Y' => Color.FromRgb(0xFF, 0xD6, 0x3A),
        'W' => Color.FromRgb(0xFF, 0xF0, 0xA6),
        'E' => Color.FromRgb(0x1E, 0x1E, 0x24),
        'O' => Color.FromRgb(0xFF, 0x8A, 0x26),
        // wood: bark, dark grain, cut end, its rings; the barrel's iron hoops and a highlight
        'b' => Color.FromRgb(0x7A, 0x4A, 0x22),
        'd' => Color.FromRgb(0x4E, 0x2E, 0x14),
        'r' => Color.FromRgb(0xD6, 0xA8, 0x68),
        'c' => Color.FromRgb(0xA8, 0x76, 0x38),
        'm' => Color.FromRgb(0x8C, 0x8E, 0x98),
        'l' => Color.FromRgb(0xB0, 0x72, 0x38),
        // hay: straw, its shade, the twine
        'h' => Color.FromRgb(0xE8, 0xC2, 0x5A),
        's' => Color.FromRgb(0xB9, 0x8E, 0x2E),
        't' => Color.FromRgb(0x8A, 0x5E, 0x24),
        // the fox: coat, white, dark (nose, ears, legs, eye)
        'o' => Color.FromRgb(0xE8, 0x72, 0x2A),
        'w' => Color.FromRgb(0xF4, 0xEE, 0xE6),
        'k' => Color.FromRgb(0x2A, 0x1A, 0x12),
        // the eagle: brown feathers, white head, yellow beak (and the fox's 'k' for eye and wing tips)
        'e' => Color.FromRgb(0x6E, 0x44, 0x24),
        'v' => Color.FromRgb(0xF2, 0xEE, 0xE6),
        'y' => Color.FromRgb(0xF2, 0xB6, 0x32),
        _ => null,
    };

    // ---- the art, all facing the way it moves: the duck right, everything coming at it left ----

    private static readonly string[] DuckArt =
    [
        ".....KKKK....",
        "....KYYYYK...",
        "....KYYEYYKOO",
        "....KYYYYYOOO",
        "K...KYYYYYK..",
        "KYK.KYYYYK...",
        "KYYKYYYYYYK..",
        "KYYYWWWWYYYK.",
        ".KYYYWWWYYYK.",
        "..KYYYYYYYK..",
        "...KKKKKKK...",
    ];

    /// <summary>The duck's feet: two running frames, and tucked up for a jump.</summary>
    private static readonly string[][] DuckFeet =
    [
        ["....O...O....", "....OO..OO..."],
        [".....O.O.....", ".....OOOO...."],
        ["....O...O....", "............."],
    ];

    /// <summary>The duck ducked under the eagle: flat, head stretched forward.</summary>
    private static readonly string[] DuckLowArt =
    [
        ".......KKKK....",
        "K.....KYYYEYKOO",
        "KYK..KYYYYYYOOO",
        "KYYKKYYYYYYK...",
        "KYYYWWWWYYYYK..",
        ".KYYYYYYYYYYK..",
        "..KKKKKKKKKK...",
    ];

    private static readonly string[][] DuckLowFeet =
    [
        ["...OO....OO...."],
        ["....OO..OO....."],
    ];

    private static readonly string[] LogArt =
    [
        ".dbbbbbbbd",
        "rcrbbdbbbb",
        "rrrbbbbdbb",
        ".dbbbbbbbd",
    ];

    private static readonly string[] HayArt =
    [
        ".hhhhhh.",
        "hhshhshh",
        "tttttttt",
        "hshhhhsh",
        "hhhshhhh",
        "ssssssss",
    ];

    private static readonly string[] BarrelArt =
    [
        ".mmmm.",
        "dblbbd",
        "mmmmmm",
        "dblbbd",
        "dblbbd",
        "mmmmmm",
        "dblbbd",
        ".mmmm.",
    ];

    private static readonly string[] FoxArt =
    [
        "..k.k.......",
        ".koooo....oo",
        "kwkooooooo.w",
        ".wwoooooooo.",
        "..wwooooooo.",
        "..k.k...k.k.",
        ".k...k.k...k",
    ];

    private static readonly string[] FoxArt2 =
    [
        "..k.k.......",
        ".koooo....oo",
        "kwkooooooo.w",
        ".wwoooooooo.",
        "..wwooooooo.",
        "...kk....kk.",
        "...kk....kk.",
    ];

    private static readonly string[] EagleArt =
    [
        "......eee...",
        ".....eeee...",
        "yvkeeeeeeekk",
        "..v.eeeeek..",
        "............",
    ];

    private static readonly string[] EagleArt2 =
    [
        "............",
        "............",
        "yvkeeeeeeekk",
        "..v.eeeeek..",
        ".....eeee...",
    ];
}
