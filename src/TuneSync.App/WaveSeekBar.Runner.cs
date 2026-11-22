using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;

namespace TuneSync.App;

// 오리: the thumb is a little pixel-art duck (a design of our own, like everything here) running
// along the bar as its ground. Farm things come at it from the right end over the part still to
// play, each set at a moment of the song and picked by how loud the music is then: a log or a
// pumpkin when it's quiet, a hay bale or a fence, a wooden barrel or a milk can when it's loud;
// now and then a fox running at it, and in loud passages an eagle swooping in at head height. It jumps the ones on the ground and runs ducked under the eagle.
// Nobody plays it: every move is worked out so it always gets by. Every pixel is a whole number
// of screen pixels on the screen's own grid, so the art stays crisp (a pixel of 1.5 px was smudged
// at its edges and didn't read as pixel art). Drawn with kept brushes (Solid).
public sealed partial class WaveSeekBar
{
    /// <summary>What's coming at the duck right now, placed from <see cref="_cues"/> for this moment
    /// of the song. (In one: X - its middle; Vx - its speed, px a second leftward; Hue - which thing
    /// it is (<see cref="Hazard"/>); Age - the moment it reaches the duck (its cue's time); Life - 1
    /// once the duck has jumped it.)</summary>
    private readonly List<Spark> _obstacles = [];

    private enum Hazard { Log, Hay, Barrel, Fox, Eagle, Pumpkin, Fence, MilkCan }

    /// <summary>One obstacle of the song: the moment (seconds into the song) it reaches the duck,
    /// what it is, and how fast it comes.</summary>
    private readonly record struct Cue(double Time, Hazard Kind, double Speed);

    /// <summary>The song's obstacles in order, worked out from its loudness as far as needed: the
    /// same song always gets the same ones at the same moments, so they stay with the music when
    /// the song is jumped about in (they used to be sent out as it played, and a jump left them
    /// where they were, out of step with the music). Made again for a new song.</summary>
    private readonly List<Cue> _cues = [];
    private WaveformData? _cuesFor;
    private double _cuesLength, _cuesFrom;

    /// <summary>The cues the duck has jumped (by their time), and the song moment last seen (to
    /// notice a jump in the song).</summary>
    private readonly HashSet<double> _jumpedCues = [];
    private double _lastMoment = double.NaN;

    /// <summary>Dust kicked up on landing. (X, Y - where; Vx, Vy - its drift; Age, Life.)</summary>
    private readonly List<Spark> _dust = [];

    /// <summary>The duck: how high it is off the ground and how fast it's going up (px, px a
    /// second), its running clock (for the feet), the scene's clock (for the fox and the eagle),
    /// and whether it's ducking.</summary>
    private double _jumpY, _jumpV, _runTime, _sceneTime;

    /// <summary>The jump under way is a big one, over something tall: it floats a little longer
    /// and the duck beats its little wings (a small hop over a low thing, it doesn't).</summary>
    private bool _bigJump;
    private double _flapTime;

    /// <summary>Things this many art pixels tall or more are jumped high (the barrel, the milk can
    /// and the fox); lower ones with a small hop.</summary>
    private const int TallRows = 8;

    /// <summary>How far above a low thing the small hop goes, and above a tall one the big jump
    /// (art pixels); and the big jump's lighter fall (px a second squared), so it floats.
    /// (A hop of 3 clipped the back of a long log on landing.)</summary>
    private const double HopSpare = 4, LeapSpare = 8, LeapGravity = 700;
    private bool _ducking;

    /// <summary>How fast things fall (px a second squared) and how fast the ground comes.</summary>
    private const double RunnerGravity = 900, ObstacleSpeed = 135;

    /// <summary>How much faster than the ground the fox runs and the eagle flies (px a second).</summary>
    private const double FoxRush = 50, EagleRush = 35;

    /// <summary>How high the eagle flies: the bottom of its picture, in art pixels off the ground
    /// (its lowest wing tip above the ducked duck, 8 pixels tall; its body, 2 rows up, into the
    /// standing one, 13).</summary>
    private const int EagleAltitude = 9;

    /// <summary>Something still moving on its own (the obstacles only move with the song).</summary>
    private bool RunnerAlive => _dust.Count > 0 || _jumpY > 0;

    private void ClearRunner()
    {
        _obstacles.Clear();
        _dust.Clear();
        _jumpedCues.Clear();
        _lastMoment = double.NaN;
        _jumpY = _jumpV = 0;
        _ducking = false;
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
        Hazard.Pumpkin => PumpkinArt,
        Hazard.Fence => FenceArt,
        Hazard.MilkCan => MilkCanArt,
        Hazard.Fox => FoxRun[frame % FoxRun.Length],
        _ => EagleFlap[frame % EagleFlap.Length],
    };

    /// <summary>An obstacle's half width and height in px.</summary>
    private (double Half, double Height) SizeOf(Spark o)
    {
        var art = SpriteOf(KindOf(o), 0);
        return (art[0].Length * ArtPixel / 2, art.Length * ArtPixel);
    }

    /// <summary>A number from 0 to 1 fixed by <paramref name="n"/>, <paramref name="salt"/> and the
    /// song's length: the song's own dice, the same every time.</summary>
    private double SongDice(int n, int salt)
    {
        var x = Math.Sin(n * 12.9898 + salt * 78.233 + _cuesLength * 0.731) * 43758.5453;
        return x - Math.Floor(x);
    }

    private double LoudnessAt(double seconds) =>
        Math.Clamp(Math.Pow(Waveform is { } waveform ? waveform.LevelAt(seconds) : FallbackLevel, 1.3), 0, 1);

    /// <summary>Works out the song's obstacles as far ahead as the bar shows from
    /// <paramref name="now"/> (the duck at <paramref name="duck"/>): one every
    /// 0.9-1.95 s (closer in loud passages), each picked by how loud the music is when it reaches
    /// the duck - a log or a pumpkin when quiet, a hay bale or a fence, a barrel or a milk can when
    /// loud; now and then a fox, and in loud passages an eagle. They reach the duck at least 0.9 s apart, so the fox and the eagle,
    /// faster than the rest, never run into the one ahead before the duck, and it's never still
    /// in the air when the eagle comes.</summary>
    private void PlanCues(double now, double duck)
    {
        // a new song: nothing on the track yet, the first one coming in at the far end (one set
        // a second or two into the song popped up in the middle of the bar)
        if (_cuesLength != Maximum)
        {
            _cues.Clear();
            _jumpedCues.Clear();
            _cuesFor = Waveform;
            _cuesLength = Maximum;
            _cuesFrom = Math.Max(0.6, now + (Right - duck) / ObstacleSpeed - 0.9);
        }
        // the song's loudness just came (it's worked out after the song starts): what's on the
        // track stays as it is, and only what comes after it is picked again (all of it picked
        // again made what was in sight change all at once)
        else if (!ReferenceEquals(_cuesFor, Waveform))
        {
            _cuesFor = Waveform;
            var inSight = now + (Right - duck) / ObstacleSpeed + 0.5;
            _cues.RemoveAll(c => c.Time > inSight);
        }
        var until = now + (Right - Left) / ObstacleSpeed + 1;
        var time = _cues.Count > 0 ? _cues[^1].Time : _cuesFrom;
        while (time < until && time < Maximum)
        {
            var n = _cues.Count;
            time += Math.Max(0.9, 0.95 + SongDice(n, 0) * 1.0 - 0.3 * LoudnessAt(time));
            var loud = LoudnessAt(time);
            var other = SongDice(n, 2) < 0.5;
            var kind = loud < 0.3 ? (other ? Hazard.Pumpkin : Hazard.Log)
                     : loud < 0.6 ? (other ? Hazard.Fence : Hazard.Hay)
                     : other ? Hazard.MilkCan : Hazard.Barrel;
            var roll = SongDice(n, 1);
            if (loud > 0.55 && roll < 0.3) kind = Hazard.Eagle;
            else if (loud > 0.25 && roll > 0.82) kind = Hazard.Fox;
            _cues.Add(new Cue(time, kind, ObstacleSpeed + kind switch { Hazard.Fox => FoxRush, Hazard.Eagle => EagleRush, _ => 0 }));
        }
    }

    /// <summary>Puts the obstacles where they are at this moment of the song: each its cue's time
    /// from the duck, at its speed - so they move with the song, stop when it pauses and jump when
    /// it's jumped in (dragging the thumb included).</summary>
    private void PlaceObstacles(double duck)
    {
        _obstacles.Clear();
        if (Maximum <= 0) return;
        var now = CurrentPosition();
        // jumped in the song: what's ahead now hasn't been jumped yet
        if (!double.IsNaN(_lastMoment) && Math.Abs(now - _lastMoment) > 0.5) _jumpedCues.RemoveWhere(t => t > now - 1);
        _lastMoment = now;
        PlanCues(now, duck);
        foreach (var cue in _cues)
        {
            if (cue.Time < now - 4) continue;
            // past the far end even at the slowest speed: so are all the later ones
            if ((cue.Time - now) * ObstacleSpeed > Right + 12 - duck) break;
            var x = duck + (cue.Time - now) * cue.Speed;
            if (x > Right + 12 || x < Left - 30) continue;
            _obstacles.Add(new Spark { X = x, Vx = cue.Speed, Hue = (int)cue.Kind, Age = cue.Time, Life = _jumpedCues.Contains(cue.Time) ? 1 : 0 });
        }
    }

    private void UpdateRunner(double dt)
    {
        if (Look != SeekBarStyle.Runner)
        {
            if (RunnerAlive || _obstacles.Count > 0) ClearRunner();
            return;
        }
        // the duck's own motion slows to a stop when the music is paused, and picks up again with it
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

        PlaceObstacles(duck);

        // ducking while the eagle goes over (from a little before it reaches the duck until it's by)
        _ducking = false;
        foreach (var o in _obstacles)
        {
            if (KindOf(o) != Hazard.Eagle) continue;
            var (half, _) = SizeOf(o);
            if (o.X - half < duck + 10 * pixel && o.X + half > duck - 8 * pixel) _ducking = true;
        }

        // on the ground (and not under an eagle): jump the next thing so the top of the jump comes
        // with its middle right under the feet, and high enough to stay over it for its whole width
        if (_jumpY <= 0 && _jumpV <= 0 && !_ducking && IsPlaying)
        {
            for (var i = 0; i < _obstacles.Count; i++)
            {
                var o = _obstacles[i];
                if (KindOf(o) == Hazard.Eagle) continue;
                var (half, height) = SizeOf(o);
                if (o.Life > 0 || o.X + half < duck - 4) continue;
                // a small hop over a low thing, a big flapping jump over a tall one
                var big = height >= TallRows * pixel - 0.01;
                var gravity = big ? LeapGravity : RunnerGravity;
                // the feet (6 art pixels, their middle half a pixel ahead of the duck's) must be off
                // the ground while the thing passes under them, with a pixel to spare each side:
                // that long above its top, half of it either side of the top of the jump
                var under = (2 * half + 8 * pixel) / 2 / o.Vx;
                var spare = Math.Max((big ? LeapSpare : HopSpare) * pixel, gravity / 2 * under * under);
                var apex = height + spare;
                var up = Math.Sqrt(2 * gravity * apex);
                var rise = up / gravity; // seconds to the top
                // (the top used to come where it suited a narrow thing: a long log was still under
                // the feet when they came down)
                if (o.X - (duck + 0.5 * pixel) > o.Vx * rise) continue;
                _jumpV = up;
                _bigJump = big;
                _flapTime = 0;
                _jumpedCues.Add(o.Age);
                break;
            }
        }
        if (_jumpY > 0 || _jumpV > 0)
        {
            _jumpY += _jumpV * t;
            _jumpV -= (_bigJump ? LeapGravity : RunnerGravity) * t;
            _flapTime += t;
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
        // placed for this very moment: also when the song is jumped in while paused (no frames run then)
        PlaceObstacles(thumbX);
        foreach (var o in _obstacles)
        {
            var kind = KindOf(o);
            // running legs and flapping wings
            var frame = kind == Hazard.Fox ? (int)(_sceneTime * 14) % FoxRun.Length : kind == Hazard.Eagle ? (int)(_sceneTime * 8) % EagleFlap.Length : 0;
            var art = SpriteOf(kind, frame);
            var fade = Math.Clamp((o.X - Left + 30) / 40, 0, 1);
            // the eagle up in the air; the fox off the ground by a pixel with its legs gathered, mid-bound
            var bottom = kind == Hazard.Eagle ? ground - EagleAltitude * pixel
                       : kind == Hazard.Fox && frame == 2 ? ground - pixel : ground;
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
            // in a big jump its little wings beat, up and down (about 7 times a second)
            body = _bigJump && _jumpY > 0.5 ? DuckFlap[(int)(_flapTime * 14) % 2] : DuckArt;
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
        // the pumpkin: skin, ridges, stem; the milk can's bright metal (with 'm' for its rims)
        'p' => Color.FromRgb(0xF0, 0x8A, 0x24),
        'q' => Color.FromRgb(0xB8, 0x5A, 0x14),
        'g' => Color.FromRgb(0x5E, 0x82, 0x2C),
        'n' => Color.FromRgb(0xD4, 0xD7, 0xDE),
        // hay: straw, its shade, the twine
        'h' => Color.FromRgb(0xE8, 0xC2, 0x5A),
        's' => Color.FromRgb(0xB9, 0x8E, 0x2E),
        't' => Color.FromRgb(0x8A, 0x5E, 0x24),
        // the fox: coat, white, dark (nose, ears, legs, eye)
        'o' => Color.FromRgb(0xE8, 0x72, 0x2A),
        'w' => Color.FromRgb(0xF4, 0xEE, 0xE6),
        'k' => Color.FromRgb(0x2A, 0x1A, 0x12),
        // the eagle: brown feathers (light enough to show on the dark screen saver), paler feather
        // edges, white head and tail, yellow beak (and the fox's 'k' for its eye)
        'e' => Color.FromRgb(0x8E, 0x5C, 0x2E),
        'f' => Color.FromRgb(0xC8, 0x92, 0x56),
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

    /// <summary>The duck beating its wings in a big jump: the wing raised over its back, then
    /// pressed down below its side.</summary>
    private static readonly string[][] DuckFlap =
    [
        [
            ".....KKKK....",
            "....KYYYYK...",
            "....KYYEYYKOO",
            "..W.KYYYYYOOO",
            "KWW.KYYYYYK..",
            "KWWWKYYYYK...",
            "KYWWYYYYYYK..",
            "KYYYYYYYYYYK.",
            ".KYYYYYYYYYK.",
            "..KYYYYYYYK..",
            "...KKKKKKK...",
        ],
        [
            ".....KKKK....",
            "....KYYYYK...",
            "....KYYEYYKOO",
            "....KYYYYYOOO",
            "K...KYYYYYK..",
            "KYK.KYYYYK...",
            "KYYKYYYYYYK..",
            "KYYYWWWWYYYK.",
            ".KYYWWWWWYYK.",
            "..KYWWWWYYK..",
            "...KWWWKKK...",
        ],
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

    /// <summary>A log lying on its side: its cut end at the front with the rings showing, bark
    /// along it with dark grain, shaded underneath.</summary>
    private static readonly string[] LogArt =
    [
        ".rrrbbbbbbb.",
        "rcccrbdbbbbb",
        "rcrcrbbbbdbb",
        "rcccrbbdbbbb",
        ".rrrdddddddd",
    ];

    /// <summary>A hay bale seen flat from the side: straw in lines, two bands of twine round it.</summary>
    private static readonly string[] HayArt =
    [
        "hhthhhthh",
        "sstshhtss",
        "hhthsstsh",
        "shthhhthh",
        "hhtssstsh",
        "hhthhhthh",
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

    /// <summary>The fox, running at the duck: tall pointed ears (orange to the tip: dark tips were
    /// lost against the dark screen saver), an eye with a light
    /// glint beside it, a white muzzle reaching out ahead of its head to a dark nose, a white chest,
    /// a long bushy tail with a white tip, and legs in a darker orange (dark legs were lost against
    /// the dark screen saver) galloping in four steps - stretched out, passing, gathered under it
    /// (where it's off the ground, mid-bound), passing.</summary>
    private static readonly string[] FoxBody =
    [
        "....o..o.........",
        "...oo.oo.........",
        "...oooooo....ooo.",
        "..ookwoooo..ooooo",
        "kwwwoooooooooooow",
        ".wwoooooooooooww.",
        "...wwooooooooo...",
    ];

    private static readonly string[][] FoxRun =
    [
        [.. FoxBody, "..qq.......qq....", ".q..q.....q..q..."],
        [.. FoxBody, "...qq......qq....", "...q.q.....q.q..."],
        [.. FoxBody, "....qq...qq......", ".....qq.qq......."],
        [.. FoxBody, "...qq......qq....", "....qq......qq..."],
    ];

    /// <summary>A pumpkin: round and wide, two deep ridges down it, a stem on top, a shine.</summary>
    private static readonly string[] PumpkinArt =
    [
        "....g....",
        "..pqgqp..",
        ".hpqpqpp.",
        "pppqpqppp",
        "pppqpqppp",
        ".ppqpqpp.",
    ];

    private static readonly string[] FenceArt =
    [
        "l...l...l",
        "lllllllll",
        "b...b...b",
        "lllllllll",
        "b...b...b",
        "b...b...b",
    ];

    private static readonly string[] MilkCanArt =
    [
        ".mnnm.",
        "..mm..",
        ".mnnm.",
        "mnnnnm",
        "mnnnnm",
        "mmmmmm",
        "mnnnnm",
        "mmmmmm",
    ];

    /// <summary>The eagle, flying at the duck: a white head with a dark eye and a hooked yellow beak,
    /// a brown body narrowing under the wing, a fanned white tail, and a big pale wing beating in four
    /// steps - raised high, spread level with feather tips, lowered, level. (It was a small dark-brown
    /// shape that didn't read as a bird, and then a level wing lying on a two-row body: a brick.)</summary>
    private static readonly string[][] EagleFlap =
    [
        [
            "..........ff....",
            "........ffe.....",
            "......ffee......",
            ".....feee.......",
            "yvkveeeeeeevvv..",
            "..vv.eeeee.vv...",
            "................",
            "................",
        ],
        [
            "................",
            "................",
            "................",
            ".....ffffffff.f.",
            "yvkveeeeeeevvv..",
            "..vv.eeeee.vv...",
            "................",
            "................",
        ],
        [
            "................",
            "................",
            "................",
            "................",
            "yvkveeeeeeevvv..",
            "..vv.eeeee.vv...",
            ".....feee.......",
            "......ffee......",
        ],
        [
            "................",
            "................",
            "................",
            ".....ffffffff.f.",
            "yvkveeeeeeevvv..",
            "..vv.eeeee.vv...",
            "................",
            "................",
        ],
    ];
}
