using System.Windows;
using System.Windows.Media;
using TuneSync.Core.Models;
using Wpf.Ui.Appearance;

namespace TuneSync.App;

// 오리: the thumb is a little pixel-art duck (a design of our own, like everything here) running
// along the bar as its ground. Farm things come at it from the right end over the part still to
// play, each set at a moment of the song and picked by how loud the music is then: a log, a
// pumpkin, a stone or a toadstool when it's quiet; a hay bale, a fence or a hedgehog; a wooden
// barrel, a milk can, a crate or a sunflower when it's loud; now and then a fox running at it, and
// in loud passages an eagle (an owl at night) swooping in at head height. Near the end the way is clear and the
// duck walks into its house at the right end. By day (the sun) with the light theme, by night (moon, stars) with the dark one. It jumps the ones on the ground and runs
// ducked under the eagle. Now and then a worm, a caterpillar or a grasshopper comes instead: the duck bends down,
// pecks it up on the beat and munches. Faint clouds drift by behind, slower than the ground.
// The fox, jumped, skids to a sit, looks up at the duck going over and then gazes after it.
// It moves with the music: each thing is set so the duck takes off on a beat of the song (higher
// on a strong one), it bobs on every beat as it runs and quacks on strong ones, its feet go
// faster when the music is loud, and a loud landing kicks up more dust.
// Nobody plays it: every move is worked out so it always gets by. Every pixel is a whole number
// of screen pixels on the screen's own grid, so the art stays crisp (a pixel of 1.5 px was smudged
// at its edges and didn't read as pixel art). Drawn with kept brushes (Solid).
public sealed partial class WaveSeekBar
{
    /// <summary>What's coming at the duck right now, placed from <see cref="_cues"/> for this moment
    /// of the song. (In one: X - its middle; Vx - its speed, px a second leftward; Hue - which thing
    /// it is (<see cref="Hazard"/>); Age - the moment it reaches the duck (its cue's time); Life - 1
    /// once the duck has jumped it; Size - its cue's <see cref="Cue.Lift"/>; Vy - for the fox, seconds
    /// since the duck went over it, 0 before.)</summary>
    private readonly List<Spark> _obstacles = [];

    private enum Hazard { Log, Hay, Barrel, Fox, Eagle, Pumpkin, Fence, MilkCan, Rock, Mushroom, Hedgehog, Crate, Sunflower, Worm, Caterpillar, Grasshopper }

    /// <summary>The worm, the caterpillar and the grasshopper aren't jumped: the duck pecks them
    /// up as they reach it.</summary>
    private static bool IsFood(Hazard kind) => kind is Hazard.Worm or Hazard.Caterpillar or Hazard.Grasshopper;

    /// <summary>What's picked at each loudness: quiet, middling, loud.</summary>
    private static readonly Hazard[] QuietThings = [Hazard.Log, Hazard.Pumpkin, Hazard.Rock, Hazard.Mushroom];
    private static readonly Hazard[] MiddleThings = [Hazard.Hay, Hazard.Fence, Hazard.Hedgehog];
    private static readonly Hazard[] LoudThings = [Hazard.Barrel, Hazard.MilkCan, Hazard.Crate, Hazard.Sunflower];

    /// <summary>Where the pecking duck's beak tip is, in art pixels ahead of its middle: food is
    /// gone when its middle gets there.</summary>
    private const double BeakReach = 8;

    /// <summary>The food the duck has eaten (by its cue's time), so its crumbs fly once.</summary>
    private readonly HashSet<double> _eatenCues = [];

    /// <summary>One obstacle of the song: the moment (seconds into the song) it reaches the duck,
    /// what it is, how fast it comes, and how much higher than usual the duck jumps it (0 to
    /// <see cref="MaxLift"/>: a strong beat sends it up higher).</summary>
    private readonly record struct Cue(double Time, Hazard Kind, double Speed, double Lift);

    /// <summary>The song's beats (moment and strength), found ahead of time in its loudness the way
    /// the live beat is found (<see cref="Step"/>), so obstacles can be set where the duck takes
    /// off on one. Found again for new loudness data.</summary>
    private List<(double Time, double Strength)> _songBeats = [];
    private WaveformData? _beatsFor;

    /// <summary>The song's ordinary beat strength and its strongest ones: a jump is lifted
    /// between the two, so it's the song's own big hits that send the duck higher (a
    /// fixed scale lifted nearly every jump in a loud modern song).</summary>
    private double _beatMid = 0.2, _beatTop = 0.4;

    /// <summary>How much more room a strong beat's jump leaves above the thing (as a share of the
    /// usual room).</summary>
    private const double MaxLift = 0.7;

    /// <summary>The duck's bob on a beat while it runs: time since the beat and how high (px); and
    /// how long its beak stays open for a quack on a strong beat (seconds left).</summary>
    private double _bobTime = 1, _bobHeight, _quackLeft;
    private const double BobSeconds = 0.18;

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

    /// <summary>The song moment the scene is drawn at: its own clock, moved on by exactly each
    /// frame's time and drawn gently toward the player's position. The position itself comes
    /// from a stopwatch read whenever the frame handler runs, and every 200 ms it's set again from
    /// the player: per 14 ms frame it moved anything from 1 to 31 ms of song, and at 135 px a
    /// second that's a 2-px back-and-forth that read as stutter even at 68 frames.</summary>
    private double _sceneClock;
    private bool _sceneClockSet;

    /// <summary>The moment to draw at: the smooth clock while playing, else where playback is.</summary>
    private double RunnerNow => _sceneClockSet && IsPlaying && !_dragging ? _sceneClock : CurrentPosition();

    /// <summary>Moves the scene's clock on by <paramref name="dt"/> and a little toward the
    /// player's position (an error is gone in about half a second, without a jump); jumps to it
    /// when the song was jumped in, paused, dragged or the frames stalled.</summary>
    private void AdvanceSceneClock(double dt)
    {
        var position = CurrentPosition();
        if (!_sceneClockSet || !IsPlaying || _dragging || Math.Abs(position - (_sceneClock + dt)) > 0.25)
        {
            _sceneClock = position;
            _sceneClockSet = true;
            return;
        }
        _sceneClock += dt;
        _sceneClock += (position - _sceneClock) * Math.Min(1, dt * 2);
    }

    /// <summary>Something still moving on its own (the obstacles only move with the song).</summary>
    private bool RunnerAlive => _dust.Count > 0 || _jumpY > 0;

    private void ClearRunner()
    {
        _obstacles.Clear();
        _dust.Clear();
        _jumpedCues.Clear();
        _eatenCues.Clear();
        _lastMoment = double.NaN;
        _sceneClockSet = false;
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
        Hazard.Rock => RockArt,
        Hazard.Mushroom => MushroomArt,
        Hazard.Crate => CrateArt,
        Hazard.Sunflower => SunflowerArt,
        Hazard.Hedgehog => HedgehogWalk[frame % HedgehogWalk.Length],
        Hazard.Worm => WormWiggle[frame % WormWiggle.Length],
        Hazard.Caterpillar => CaterpillarCrawl[frame % CaterpillarCrawl.Length],
        Hazard.Grasshopper => GrasshopperArt[frame % GrasshopperArt.Length],
        Hazard.Fox => FoxRun[frame % FoxRun.Length],
        // (by night an owl flies in its place: the same size and flight)
        _ => Night ? OwlFlap[frame % OwlFlap.Length] : EagleFlap[frame % EagleFlap.Length],
    };

    /// <summary>An obstacle's half width and height in px.</summary>
    private (double Half, double Height) SizeOf(Spark o) => SizeOf(KindOf(o));

    private (double Half, double Height) SizeOf(Hazard kind)
    {
        var art = SpriteOf(kind, 0);
        return (art[0].Length * ArtPixel / 2, art.Length * ArtPixel);
    }

    /// <summary>The jump over a <paramref name="kind"/> coming at <paramref name="speed"/>: how fast
    /// the duck leaves the ground, how fast it falls, and whether it's a big flapping one. Used both
    /// to set the obstacles (so the take-off lands on a beat) and for the jump itself, so the two
    /// always agree.</summary>
    private (double Up, double Gravity, bool Big) LeapOver(Hazard kind, double speed, double lift)
    {
        var pixel = ArtPixel;
        var (half, height) = SizeOf(kind);
        // a small hop over a low thing, a big flapping jump over a tall one
        var big = height >= TallRows * pixel - 0.01;
        var gravity = big ? LeapGravity : RunnerGravity;
        // the feet (6 art pixels, their middle half a pixel ahead of the duck's) must be off
        // the ground while the thing passes under them, with a pixel to spare each side:
        // that long above its top, half of it either side of the top of the jump
        var under = (2 * half + 8 * pixel) / 2 / speed;
        var spare = Math.Max((big ? LeapSpare : HopSpare) * pixel * (1 + lift), gravity / 2 * under * under);
        return (Math.Sqrt(2 * gravity * (height + spare)), gravity, big);
    }

    /// <summary>Seconds from take-off to the top of the jump over a cue's thing (none for the
    /// eagle, which is ducked under as it arrives; for food, from the moment it's eaten - so that
    /// comes on the beat - to its reaching the duck's middle).</summary>
    private double RiseFor(Hazard kind, double speed, double lift)
    {
        if (kind == Hazard.Eagle) return 0;
        if (IsFood(kind)) return BeakReach * ArtPixel / speed;
        var (up, gravity, _) = LeapOver(kind, speed, lift);
        return up / gravity;
    }

    /// <summary>The song's beats, worked out once per loudness data: a slice well above the
    /// running average (the same rule and numbers as the live beat in <see cref="Step"/>).</summary>
    private List<(double Time, double Strength)> SongBeats()
    {
        if (ReferenceEquals(_beatsFor, Waveform)) return _songBeats;
        _beatsFor = Waveform;
        _songBeats = [];
        if (Waveform is not { } waveform) return _songBeats;
        var levels = waveform.Levels;
        var step = waveform.BucketSeconds;
        double baseline = 0, since = 1;
        for (var i = 0; i < levels.Length; i++)
        {
            var level = levels[i];
            since += step;
            if (level - baseline > 0.12 && since > 0.15)
            {
                _songBeats.Add((i * step, level - baseline));
                since = 0;
            }
            baseline += (level - baseline) * Math.Min(1, step * 1.2);
        }
        if (_songBeats.Count > 0)
        {
            var sorted = _songBeats.Select(b => b.Strength).Order().ToArray();
            // (the jumps are set on the strongest beat near each spot, so most are above the
            // median: lifted from the 60th to the 97th percentile, only the big hits go highest)
            _beatMid = sorted[(int)((sorted.Length - 1) * 0.6)];
            _beatTop = Math.Max(_beatMid + 0.05, sorted[(int)((sorted.Length - 1) * 0.97)]);
        }
        return _songBeats;
    }

    /// <summary>What a cue reaching the duck about <paramref name="time"/> is (the
    /// <paramref name="n"/>th of the song): picked by how loud the music is then, with the song's
    /// own dice.</summary>
    private Hazard KindAt(double time, int n)
    {
        var loud = LoudnessAt(time);
        var things = loud < 0.3 ? QuietThings : loud < 0.6 ? MiddleThings : LoudThings;
        var pick = Math.Min(things.Length - 1, (int)(SongDice(n, 2) * things.Length));
        // never the same thing twice running: the next one in the list instead
        if (n > 0 && things[pick] == _cues[n - 1].Kind) pick = (pick + 1) % things.Length;
        var kind = things[pick];
        var roll = SongDice(n, 1);
        if (loud > 0.55 && roll < 0.3) kind = Hazard.Eagle;
        else if (loud > 0.25 && roll > 0.82) kind = Hazard.Fox;
        // now and then a snack instead: a worm when it's quiet, a caterpillar or a grasshopper
        // when it's not (a red ladybug looked like nothing a duck would eat)
        else if (SongDice(n, 3) < 0.16)
            kind = loud < 0.4 ? Hazard.Worm : SongDice(n, 4) < 0.5 ? Hazard.Caterpillar : Hazard.Grasshopper;
        return kind;
    }

    private static double SpeedOf(Hazard kind) =>
        ObstacleSpeed + kind switch { Hazard.Fox => FoxRush, Hazard.Eagle => EagleRush, _ => 0 };

    /// <summary>How much higher a jump taken off on a beat of <paramref name="strength"/> goes:
    /// nothing for an ordinary beat of the song, up to <see cref="MaxLift"/> for its strongest.</summary>
    private double LiftOf(double strength) => Math.Clamp((strength - _beatMid) / (_beatTop - _beatMid), 0, 1) * MaxLift;

    /// <summary>A number from 0 to 1 fixed by <paramref name="n"/>, <paramref name="salt"/> and the
    /// song's length: the song's own dice, the same every time.</summary>
    private double SongDice(int n, int salt)
    {
        // a proper integer mix (splitmix64): the old sine hash gave neighboring cues similar
        // numbers, so the same thing often came four or five times in a row
        unchecked
        {
            var h = (ulong)n * 0x9E3779B97F4A7C15UL ^ (ulong)salt * 0xBF58476D1CE4E5B9UL ^ (ulong)BitConverter.DoubleToInt64Bits(_cuesLength);
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27; h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
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
            _eatenCues.Clear();
            _cuesFor = Waveform;
            _cuesLength = Maximum;
            // (and not before the duck is out of its egg and off)
            _cuesFrom = Math.Max(HatchOut + 1, now + (Right - duck) / ObstacleSpeed - 0.9);
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
        var beats = SongBeats();
        // (nothing in the last stretch: the way to the house is clear; a cue comes at most 2.4 s
        // after the one before)
        while (time < until && time < Math.Min(Maximum, HouseFrom) - 2.4)
        {
            var n = _cues.Count;
            // where it would go by the dice alone; a beat near there is better
            var nominal = time + Math.Max(0.9, 0.95 + SongDice(n, 0) * 1.0 - 0.3 * LoudnessAt(time));
            var cue = new Cue(nominal, KindAt(nominal, n), 0, 0);
            var best = double.NegativeInfinity;
            // the duck takes off a jump's rise before the thing reaches it: set the thing so that
            // moment is on a beat (the eagle arrives on one, as the duck ducks); stronger beats and
            // ones nearer the dice's moment win, and they stay at least 0.9 s apart as before
            for (var i = FirstBeatFrom(beats, time + 0.4); i < beats.Count && beats[i].Time < nominal + 0.8; i++)
            {
                var (beat, strength) = beats[i];
                var kind = KindAt(beat + 0.25, n);
                var lift = LiftOf(strength);
                var at = beat + RiseFor(kind, SpeedOf(kind), lift);
                if (at < time + 0.9 || at > time + 2.4) continue;
                var score = strength - 0.12 * Math.Abs(at - nominal);
                if (score <= best) continue;
                best = score;
                cue = new Cue(at, kind, 0, lift);
            }
            time = cue.Time;
            _cues.Add(cue with { Speed = SpeedOf(cue.Kind) });
        }
    }

    /// <summary>The index of the first of <paramref name="beats"/> at or after
    /// <paramref name="time"/>.</summary>
    private static int FirstBeatFrom(List<(double Time, double Strength)> beats, double time)
    {
        int lo = 0, hi = beats.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (beats[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Puts the obstacles where they are at this moment of the song: each its cue's time
    /// from the duck, at its speed - so they move with the song, stop when it pauses and jump when
    /// it's jumped in (dragging the thumb included).</summary>
    private void PlaceObstacles(double duck)
    {
        _obstacles.Clear();
        if (Maximum <= 0) return;
        var now = RunnerNow;
        // jumped in the song: what's ahead now hasn't been jumped yet
        if (!double.IsNaN(_lastMoment) && Math.Abs(now - _lastMoment) > 0.5)
        {
            _jumpedCues.RemoveWhere(t => t > now - 1);
            _eatenCues.RemoveWhere(t => t > now - 1);
        }
        _lastMoment = now;
        PlanCues(now, duck);
        foreach (var cue in _cues)
        {
            if (cue.Time < now - 4) continue;
            // past the far end even at the slowest speed: so are all the later ones
            if ((cue.Time - now) * ObstacleSpeed > Right + 12 - duck) break;
            var x = duck + (cue.Time - now) * cue.Speed;
            // the fox, once the duck is over it, skids to a sit and is carried off with the ground
            var passed = cue.Kind == Hazard.Fox && now > cue.Time ? now - cue.Time : 0;
            if (passed > 0) x = duck - FoxSkid(passed, cue.Speed);
            // food is gone once it's reached the pecking beak
            if (IsFood(cue.Kind) && now >= EatenAt(cue)) continue;
            if (x > Right + 12 || x < Left - 30) continue;
            _obstacles.Add(new Spark { X = x, Vx = cue.Speed, Hue = (int)cue.Kind, Age = cue.Time, Life = _jumpedCues.Contains(cue.Time) ? 1 : 0, Size = cue.Lift, Vy = passed });
        }
    }

    /// <summary>How high the grasshopper is off the ground (px): it springs up on each beat (the
    /// duck's bob keeps the time since the last one) and lands again, and keeps to the ground
    /// as it gets close, so it's there for the beak.</summary>
    private double GrassHop(double x, double duck, double pixel)
    {
        const double airborne = 0.3;
        if (_bobTime >= airborne) return 0;
        var near = Math.Clamp((x - (duck + BeakReach * pixel)) / 40, 0, 1);
        return Math.Sin(Math.PI * _bobTime / airborne) * 5 * pixel * near;
    }

    /// <summary>The song moment a food cue's middle reaches the duck's beak and it's eaten.</summary>
    private double EatenAt(Cue cue) => cue.Time - RiseFor(cue.Kind, cue.Speed, 0);

    /// <summary>Seconds since the duck ate the food nearest this moment (negative just before:
    /// it's already bending down for it), or NaN with none near.</summary>
    private double SinceEaten(double now)
    {
        foreach (var cue in _cues)
        {
            if (!IsFood(cue.Kind)) continue;
            var since = now - EatenAt(cue);
            if (since < -PeckAhead) break;
            if (since < PeckHold + MunchSeconds) return since;
        }
        return double.NaN;
    }

    /// <summary>How long before the food reaches the beak the duck bends its head down for it, how
    /// long it keeps it down after, and how long it then munches (beak opening and shutting).</summary>
    private const double PeckAhead = 0.12, PeckHold = 0.12, MunchSeconds = 0.32;

    /// <summary>How long the fox takes to skid from its run to a sit.</summary>
    private const double FoxSkidSeconds = 0.3;

    /// <summary>How far behind the duck the fox is <paramref name="passed"/> seconds after it
    /// went under it: slowing evenly from its run (<paramref name="speed"/>) to the ground's speed,
    /// then sitting still on the ground as it goes by. Smooth, so it doesn't jerk at the switch.</summary>
    private static double FoxSkid(double passed, double speed)
    {
        var slow = speed - ObstacleSpeed;
        if (passed < FoxSkidSeconds) return speed * passed - slow * passed * passed / (2 * FoxSkidSeconds);
        return speed * FoxSkidSeconds - slow * FoxSkidSeconds / 2 + ObstacleSpeed * (passed - FoxSkidSeconds);
    }

    /// <summary>The fox after the duck went over it: still running for a blink of the skid, then
    /// sitting turned round - looking up at the duck in the air, and dropping its gaze to the right
    /// as the duck lands (<paramref name="land"/> seconds after it went over), blinking now and
    /// then. (It used to look up for a fixed 0.65 s: the duck was down at about 0.33 s, so the
    /// head turned a beat late.)</summary>
    private string[] FoxAfter(double passed, double land)
    {
        if (passed < 0.05) return FoxRun[0];
        // the head comes down just before the feet touch, following the duck's fall
        var gaze = Math.Max(0.15, land - 0.04);
        if (passed < gaze) return FoxSitLookUp;
        // a blink of about a tenth of a second every 2.6 seconds
        var blink = (passed - gaze) % 2.6 > 2.5;
        return blink ? FoxSitBlink : FoxSitGaze;
    }

    private void UpdateRunner(double dt, bool beat)
    {
        if (Look != SeekBarStyle.Runner)
        {
            if (RunnerAlive || _obstacles.Count > 0) ClearRunner();
            return;
        }
        AdvanceSceneClock(dt);
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

        // food reaching the beak is eaten: a few crumbs of it fly
        var moment = RunnerNow;
        foreach (var cue in _cues)
        {
            if (!IsFood(cue.Kind)) continue;
            var since = moment - EatenAt(cue);
            if (since < 0) break;
            if (since > 0.3 || !_eatenCues.Add(cue.Time)) continue;
            var color = cue.Kind == Hazard.Worm ? 'P' : 'G';
            for (var n = 0; n < 3; n++)
            {
                _dust.Add(new Spark
                {
                    X = duck + BeakReach * pixel,
                    Y = ActualHeight / 2 - 1.5 - 3 * pixel,
                    Vx = -10 + n * 12,
                    Vy = -14 - n % 2 * 8,
                    Life = 0.3,
                    Hue = color,
                });
            }
        }

        // on every beat it bobs up a pixel or two as it runs, and on a strong one it quacks too
        // (in the air it already has the jump; ducked, it keeps low)
        _bobTime += dt;
        _quackLeft = Math.Max(0, _quackLeft - dt);
        if (beat && IsPlaying && _jumpY <= 0 && !_ducking)
        {
            var strong = _beatStrength >= StrongBeat;
            _bobTime = 0;
            _bobHeight = (strong ? 2 : 1) * pixel;
            if (strong) _quackLeft = 0.2;
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
        // with its middle right under the feet, and high enough to stay over it for its whole width
        if (_jumpY <= 0 && _jumpV <= 0 && !_ducking && IsPlaying)
        {
            for (var i = 0; i < _obstacles.Count; i++)
            {
                var o = _obstacles[i];
                if (KindOf(o) == Hazard.Eagle || IsFood(KindOf(o))) continue;
                var (half, _) = SizeOf(o);
                if (o.Life > 0 || o.X + half < duck - 4) continue;
                // the same jump its cue was set for, so the take-off comes on the beat
                var (up, gravity, big) = LeapOver(KindOf(o), o.Vx, o.Size);
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
                // a louder landing kicks up more dust, and further
                var puffs = 3 + (int)Math.Round(4 * _level);
                var spread = 0.8 + 0.8 * _level;
                for (var n = 0; n < puffs; n++)
                {
                    var side = n - (puffs - 1) / 2.0;
                    _dust.Add(new Spark
                    {
                        X = duck + side * 3,
                        Y = mid - 2,
                        Vx = (side * 9 - 6) * spread,
                        Vy = (-6 - Random.Shared.NextDouble() * 6) * spread,
                        Life = 0.35,
                    });
                }
            }
        }
        // its feet go faster when the music is loud
        else if (IsPlaying) _runTime += t * (0.75 + 0.75 * _level);
    }

    /// <summary>The obstacles, the dust and the duck at <paramref name="thumbX"/> (in place of the
    /// thumb), all as pixel art on the screen's pixel grid.</summary>
    private void DrawRunner(DrawingContext dc, double mid, double thumbX, Color accent, Color warm)
    {
        var pixel = ArtPixel;
        var ground = OnGrid(mid - 1.5);
        var night = Night;
        DrawSky(dc, ground, pixel, night);
        DrawClouds(dc, ground, pixel, night);
        DrawHills(dc, ground, pixel, accent, night);
        var house = HouseShown;
        var doorRight = DoorRight(pixel);
        if (house > 0) DrawHouse(dc, ground, pixel, doorRight, house, night || thumbX - DuckArt[0].Length / 2.0 * pixel >= doorRight - 4 * pixel);
        // placed for this very moment: also when the song is jumped in while paused (no frames run then)
        PlaceObstacles(thumbX);
        foreach (var o in _obstacles)
        {
            var kind = KindOf(o);
            // running legs and flapping wings
            var frame = kind switch
            {
                Hazard.Fox => (int)(_sceneTime * 14) % FoxRun.Length,
                Hazard.Eagle => (int)(_sceneTime * 8) % EagleFlap.Length,
                Hazard.Hedgehog => (int)(_sceneTime * 8),
                Hazard.Worm or Hazard.Caterpillar => (int)(_sceneTime * 4),
                Hazard.Grasshopper => GrassHop(o.X, thumbX, pixel) > 0 ? 1 : 0,
                _ => 0,
            };
            var art = SpriteOf(kind, frame);
            if (kind == Hazard.Hedgehog) art = HedgehogPose((o.X - thumbX) / pixel) ?? art;
            if (kind == Hazard.Fox && o.Vy > 0)
            {
                // the jump over it is symmetric: the duck lands a rise after the top, which was over the fox
                art = FoxAfter(o.Vy, RiseFor(Hazard.Fox, o.Vx, o.Size));
                frame = 0; // on the ground
            }
            var fade = Math.Clamp((o.X - Left + 30) / 40, 0, 1);
            // the eagle up in the air; the fox off the ground by a pixel with its legs gathered, mid-bound
            var bottom = kind == Hazard.Eagle ? ground - EagleAltitude * pixel
                       : kind == Hazard.Fox && frame == 2 ? ground - pixel
                       : kind == Hazard.Grasshopper ? ground - OnGrid(GrassHop(o.X, thumbX, pixel)) : ground;
            var artLeft = OnGrid(o.X - art[0].Length * pixel / 2);
            if (!night) DrawOutline(dc, art, artLeft, bottom, pixel, fade);
            DrawSprite(dc, art, artLeft, bottom, pixel, fade);
        }

        foreach (var d in _dust)
        {
            var a = 1 - d.Age / d.Life;
            // dust is white; crumbs of food take its color
            var c = d.Hue > 0 && ArtColor((char)(int)d.Hue) is { } crumb ? crumb : Colors.White;
            dc.DrawRectangle(Solid(Color.FromArgb((byte)(190 * a), c.R, c.G, c.B)), null,
                new Rect(OnGrid(d.X), OnGrid(d.Y), pixel, pixel));
        }

        // the start of the song: the duck hatches from an egg, then sets off
        var since = RunnerNow;
        var hatchScene = Maximum >= StorySong;
        if (hatchScene && since < HatchBreak)
        {
            DrawEgg(dc, since, thumbX, ground, pixel);
            return;
        }
        var shellX = OnGrid(ThumbXAt(HatchBreak) - EggArt[0].Length * pixel / 2);
        var hatching = hatchScene && since < HatchOut;
        if (hatchScene)
        {
            // the shell bursts into bits that drop to the ground and fade there: its top as it
            // breaks open, its bottom as the duck runs off out of it (whole pieces flying off or
            // just fading looked wrong)
            DrawShards(dc, EggTopArt, shellX, ground - (EggArt.Length - EggBottomArt.Length + 1) * pixel, ground, pixel, since - HatchBreak, 70, 0);
            if (!hatching) DrawShards(dc, EggBottomArt, shellX, ground, ground, pixel, since - HatchOut, 45, 1);
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
            // in a big jump its little wings beat, up and down (about 7 times a second), and it
            // quacks: the beak opens and shuts on its own, slower beat (5 times a second), so the
            // two don't move in lockstep
            if (_bigJump && _jumpY > 0.5)
            {
                var wing = (int)(_flapTime * 14) % 2;
                body = (int)(_flapTime * 10) % 2 == 0 ? DuckFlapQuack[wing] : DuckFlap[wing];
            }
            else
            {
                // bending down for food, then munching it; otherwise quacking on a strong beat
                var eaten = _jumpY <= 0.5 ? SinceEaten(RunnerNow) : double.NaN;
                var munch = eaten - PeckHold;
                body = eaten < PeckHold ? DuckPeckArt
                     : munch < MunchSeconds ? ((int)(munch / 0.08) % 2 == 0 ? DuckQuack : DuckArt)
                     : _quackLeft > 0 ? DuckQuack : DuckArt;
            }
            feet = _jumpY > 0.5 ? DuckFeet[2] : DuckFeet[step];
        }
        // the beat's bob: up and back down in a moment, feet and all
        var bob = _bobTime < BobSeconds && _jumpY <= 0 ? _bobHeight * Math.Sin(Math.PI * _bobTime / BobSeconds) : 0;
        var rows = body.Length + feet.Length;
        // (placed by the standing duck's width: the pecking one reaches further forward)
        var left = OnGrid(thumbX - DuckArt[0].Length * pixel / 2);
        var top = OnGrid(ground - _jumpY - bob) - rows * pixel;
        if (hatching)
        {
            // just out: it pops up out of the bottom of the shell with a quack, and stands in it
            // a moment before running off
            var out_ = since - HatchBreak;
            body = out_ < 0.3 ? DuckQuack : DuckArt;
            feet = DuckFeet[0];
            top = OnGrid(ground + (1 - Math.Clamp(out_ / 0.2, 0, 1)) * 6 * pixel) - rows * pixel;
            // (rising from inside the shell: nothing of it below the ground, and nothing of it
            // out past the shell's sides - the duck is wider than the egg, and its tail and chest
            // stuck out of the unbroken bottom half)
            dc.PushClip(InsideShell(shellX, ground, pixel));
            DrawSprite(dc, body, left, top + body.Length * pixel, pixel, 1);
            DrawSprite(dc, feet, left, top + rows * pixel, pixel, 1);
            dc.Pop();
            DrawSprite(dc, EggBottomArt, shellX, ground, pixel, 1);
            return;
        }
        // going in at the house's door: what has gone past the door's far side is inside
        if (house > 0) dc.PushClip(new RectangleGeometry(new Rect(-1000, -1000, doorRight + 1000, ActualHeight + 2000)));
        DrawSprite(dc, body, left, top + body.Length * pixel, pixel, 1);
        DrawSprite(dc, feet, left, top + rows * pixel, pixel, 1);
        if (house > 0) dc.Pop();
    }

    // ---- the start of the song: the duck hatches ----

    /// <summary>Where the duck can be seen while it stands in the bottom half of the shell:
    /// anything above the shell, and inside it row by row (the shell itself is drawn over that).</summary>
    private Geometry InsideShell(double shellX, double ground, double pixel)
    {
        var art = EggBottomArt;
        var shellTop = ground - art.Length * pixel;
        var area = new GeometryGroup();
        area.Children.Add(new RectangleGeometry(new Rect(-1000, -1000, ActualWidth + 2000, shellTop + 1000)));
        for (var r = 0; r < art.Length; r++)
        {
            var row = art[r];
            var first = row.IndexOfAny(['K', 'w', 'n', 'U']);
            var last = row.LastIndexOfAny(['K', 'w', 'n', 'U']);
            if (first < 0) continue;
            // (the jagged top row: the shell's whole width)
            if (r == 0) (first, last) = (0, EggArt[0].Length - 1);
            area.Children.Add(new RectangleGeometry(new Rect(shellX + first * pixel, shellTop + r * pixel, (last - first + 1) * pixel, pixel)));
        }
        return area;
    }

    /// <summary>Songs at least this long (seconds) get the hatching and the house (a short one
    /// would be nothing but them).</summary>
    private const double StorySong = 20;

    /// <summary>When (seconds into the song) the egg starts to crack, breaks open, and the duck
    /// runs off out of it.</summary>
    private const double HatchCrack = 1.2, HatchBreak = 2.0, HatchOut = 2.6;

    /// <summary>Where the thumb is at <paramref name="seconds"/> into the song.</summary>
    private double ThumbXAt(double seconds) => Left + (Right - Left) * Math.Clamp(seconds / Maximum, 0, 1);

    /// <summary>The egg before it breaks: rocking now and then, a crack spreading across it,
    /// rocking harder and faster as it cracks.</summary>
    private void DrawEgg(DrawingContext dc, double since, double thumbX, double ground, double pixel)
    {
        var art = since < HatchCrack ? EggArt : since < HatchCrack + 0.3 ? EggCrackingArt : EggCrackedArt;
        int tilt;
        if (since < HatchCrack)
        {
            // a little rock (left, back, right, back) in the first half of each 0.6 s
            var phase = since % 0.6;
            tilt = phase > 0.3 ? 0 : (int)(phase / 0.075) switch { 0 => -1, 2 => 1, _ => 0 };
        }
        else tilt = ((int)(since / 0.06) % 4) switch { 0 => -1, 2 => 1, _ => 0 };
        var left = OnGrid(thumbX - art[0].Length * pixel / 2);
        // rocking on its base: the top half leans a pixel over, the bottom stays put
        var split = 6;
        DrawSprite(dc, art[..split], left + tilt * pixel, ground - (art.Length - split) * pixel, pixel, 1);
        DrawSprite(dc, art[split..], left, ground, pixel, 1);
    }

    /// <summary>A picture cut into bits of 2 x 2 art pixels (each bit: its corner, and its cells
    /// with their letters), worked out once per picture.</summary>
    private static readonly Dictionary<string[], (int Row, int Column, (int R, int C, char Letter)[] Cells)[]> _shards = new(ReferenceEqualityComparer.Instance);

    private static (int Row, int Column, (int R, int C, char Letter)[] Cells)[] ShardsOf(string[] art)
    {
        if (_shards.TryGetValue(art, out var known)) return known;
        var width = art.Max(row => row.Length);
        var shards = new List<(int, int, (int, int, char)[])>();
        for (var r = 0; r < art.Length; r += 2)
            for (var c = 0; c < width; c += 2)
            {
                var cells = new List<(int, int, char)>();
                for (var dr = 0; dr < 2 && r + dr < art.Length; dr++)
                    for (var dc = 0; dc < 2 && c + dc < art[r + dr].Length; dc++)
                        if (ArtColor(art[r + dr][c + dc]) is not null) cells.Add((dr, dc, art[r + dr][c + dc]));
                if (cells.Count > 0) shards.Add((r, c, [.. cells]));
            }
        return _shards[art] = [.. shards];
    }

    /// <summary>How fast the shell's bits fall (px a second squared), and when they're gone
    /// (seconds after the burst: fading over the last half second, lying on the ground).</summary>
    private const double ShardGravity = 520, ShardLife = 1.3;

    /// <summary><paramref name="art"/> (its bottom-left at <paramref name="left"/>,
    /// <paramref name="bottom"/>) burst <paramref name="t"/> seconds ago: its bits fly out from
    /// its middle and up by about <paramref name="up"/> px a second, fall, stop where they hit
    /// the ground and fade. Worked out from the time alone, so it's the same whenever drawn
    /// (and paused or jumped to).</summary>
    private void DrawShards(DrawingContext dc, string[] art, double left, double bottom, double ground, double pixel, double t, double up, int salt)
    {
        if (t < 0 || t > ShardLife) return;
        var alpha = Math.Clamp((ShardLife - t) / 0.5, 0, 1);
        var width = art.Max(row => row.Length);
        var top = bottom - art.Length * pixel;
        var k = 0;
        foreach (var (row, column, cells) in ShardsOf(art))
        {
            k++;
            // out from the middle (a bit more forward than back), up by a varying amount
            var vx = (column + 1 - width / 2.0) * 9 + (SongDice(k, 20 + salt) - 0.35) * 40;
            var vy = up * (0.5 + 0.7 * SongDice(k, 30 + salt));
            var y0 = top + row * pixel;
            // where it lies once down: on the ground, its own two rows tall
            var floor = ground - 2 * pixel;
            var landed = Math.Max(0, (vy + Math.Sqrt(Math.Max(0, vy * vy + 2 * ShardGravity * (floor - y0)))) / ShardGravity);
            var air = Math.Min(t, landed);
            var x = OnGrid(left + column * pixel + vx * air);
            var y = OnGrid(Math.Min(floor, y0 - vy * air + ShardGravity / 2 * air * air));
            foreach (var (r, c, letter) in cells)
            {
                var color = ArtColor(letter)!.Value;
                dc.DrawRectangle(Solid(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B)), null,
                    new Rect(x + c * pixel, y + r * pixel, pixel, pixel));
            }
        }
    }

    // ---- the end of the song: the duck goes home ----

    /// <summary>How long before the song ends the duck's house shows at the bar's right end, at
    /// the least (no obstacles come in that last stretch).</summary>
    private const double HouseSeconds = 8;

    /// <summary>How much the house shows: fading in over a second and a half as the last stretch
    /// begins (and gone again when the song is jumped back).</summary>
    private double HouseShown => Math.Clamp((RunnerNow - HouseFrom) / 1.5, 0, 1);

    /// <summary>Where the house's door ends on the right: the duck is all the way in when the
    /// thumb reaches the bar's end.</summary>
    private double DoorRight(double pixel) => OnGrid(Right - (DuckArt[0].Length / 2.0 + 1) * pixel);

    /// <summary>The song moment the house starts to show: as late as it can without landing on
    /// the duck - when the duck is just short of where it will stand - and at least
    /// <see cref="HouseSeconds"/> before the end. (A fixed time before the end put it right on
    /// top of the duck: on a four-minute song the thumb moves only some 25 px in 12 s; three duck
    /// lengths ahead was too early, half a minute before the end.) Never, for a short song or a
    /// bar too short to walk up to it.</summary>
    private double HouseFrom
    {
        get
        {
            if (Maximum < StorySong || Right <= Left) return double.PositiveInfinity;
            var pixel = ArtPixel;
            var duck = DuckArt[0].Length;
            var houseLeft = DoorRight(pixel) - 9 * pixel;
            var thumb = houseLeft - (duck / 2.0 + 4) * pixel;
            var at = (thumb - Left) / (Right - Left) * Maximum;
            if (at < Maximum / 2) return double.PositiveInfinity;
            return Math.Min(at, Maximum - HouseSeconds);
        }
    }

    /// <summary>The house at the bar's right end, its door on the left so the duck walks in
    /// (the duck is drawn in front of it and cut off past the door's far side, so it goes in
    /// through the doorway, and it's all the way in as the song ends); its window lights up as
    /// the duck gets home, or is lit all along at night.</summary>
    private void DrawHouse(DrawingContext dc, double ground, double pixel, double doorRight, double alpha, bool lit)
    {
        // the door's far side is the 9th column; it slides in from the right as it fades in,
        // like the scenery going by (just appearing beside the duck looked like a pop-up)
        var slide = 1 - alpha;
        var left = OnGrid(doorRight - 9 * pixel + slide * slide * 8 * pixel);
        DrawSprite(dc, lit ? HouseLitArt : HouseArt, left, ground, pixel, alpha);
    }

    private static readonly string[] HouseArt =
    [
        "...............ee.....",
        "...............ee.....",
        ".........RRRR..ee.....",
        ".......RRRRRRRRee.....",
        ".....RRRRRRRRRRRR.....",
        "...RRRRRRRRRRRRRRRR...",
        ".RRRRRRRRRRRRRRRRRRRR.",
        "QQQQQQQQQQQQQQQQQQQQQQ",
        ".lrrbbbrrrrrrrrrrrrrl.",
        ".lrbDDDbrrrrrrrrrrrrl.",
        ".lbDDDDDbrrrrrrrrrrrl.",
        ".lDDDDDDDrrbbbbbbbrrl.",
        ".lDDDDDDDrrbJJbJJbrrl.",
        ".lDDDDDDDrrbbbbbbbrrl.",
        ".lDDDDDDDrrbJJbJJbrrl.",
        ".lDDDDDDDrrbbbbbbbrrl.",
        ".lDDDDDDDrrcPcgcPcrrl.",
        ".lDDDDDyDrrrrrrrrrrrl.",
        ".lDDDDDDDrccccccccccl.",
        ".lDDDDDDDrrrrrrrrrrrl.",
        ".lDDDDDDDrrrrrrrrrrrl.",
        ".lDDDDDDDrccccccccccl.",
        ".lDDDDDDDrrrrrrrrrrrl.",
    ];

    /// <summary>The house with its window lit.</summary>
    private static readonly string[] HouseLitArt = [.. HouseArt.Select(row => row.Replace('J', 'L'))];

    // ---- the sky: day or night, with the app's theme ----

    /// <summary>Night with the dark theme, day with the light one (it used to be picked per song
    /// at random; following the theme it suits the rest of the screen). Redrawn when the theme
    /// changes (see <see cref="OnAppThemeChanged"/>).</summary>
    private static bool Night => ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;

    /// <summary>The app's theme changed: draw the sky again (while paused no frames run).</summary>
    private void OnAppThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (Look == SeekBarStyle.Runner) InvalidateVisual();
    }

    /// <summary>By day the sun, by night the moon and twinkling stars; still, high up behind
    /// everything, fading at the bar's ends like the clouds.</summary>
    private void DrawSky(DrawingContext dc, double ground, double pixel, bool night)
    {
        double Fade(double x) => Math.Clamp(Math.Min(x - Left, Right - x) / CloudFade, 0, 1);
        var width = Right - Left;
        var orbX = OnGrid(Left + 0.72 * width);
        var orb = night ? MoonArt : SunArt;
        var orbFade = Fade(orbX);
        if (orbFade > 0) DrawSprite(dc, orb, orbX, ground - 15 * pixel, pixel, (night ? 0.85 : 1.0) * orbFade); // (a see-through sun looked like a dull brown coin)
        if (!night) return;
        var count = Math.Max(6, (int)(width / 45));
        for (var i = 0; i < count; i++)
        {
            var x = Left + SongDice(i, 11) * width;
            var altitude = 11 + (int)(SongDice(i, 12) * 11);
            // not over the moon
            if (Math.Abs(x - (orbX + 3 * pixel)) < 8 * pixel && altitude > 12) continue;
            var fade = Fade(x);
            if (fade <= 0) continue;
            // each twinkles at its own pace
            var twinkle = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_sceneTime * (1.2 + SongDice(i, 13)) + i * 2.1));
            var color = Color.FromArgb((byte)(255 * 0.8 * twinkle * fade), 0xFF, 0xF4, 0xD0);
            var y = OnGrid(ground - altitude * pixel);
            x = OnGrid(x);
            var brush = Solid(color);
            dc.DrawRectangle(brush, null, new Rect(x, y, pixel, pixel));
            // a few bigger ones, a little cross
            if (i % 5 == 0)
            {
                dc.DrawRectangle(brush, null, new Rect(x - pixel, y, pixel, pixel));
                dc.DrawRectangle(brush, null, new Rect(x + pixel, y, pixel, pixel));
                dc.DrawRectangle(brush, null, new Rect(x, y - pixel, pixel, pixel));
                dc.DrawRectangle(brush, null, new Rect(x, y + pixel, pixel, pixel));
            }
        }
    }

    private static readonly string[] SunArt =
    [
        "..yyy..",
        ".yyhyy.",
        "yyhhyyy",
        "yyhyyyy",
        "yyyyyyy",
        ".yyyyy.",
        "..yyy..",
    ];

    private static readonly string[] MoonArt =
    [
        "..WWW.",
        ".WWW..",
        "WWW...",
        "WWW...",
        "WWW...",
        ".WWW..",
        "..WWW.",
    ];

    /// <summary>How fast the clouds drift by (px a second): a quarter of the ground's speed, so
    /// they read as far off behind it.</summary>
    private const double CloudSpeed = ObstacleSpeed / 4;

    /// <summary>How far in from either end of the bar a cloud (or the hills) fades in or out (px).</summary>
    private const double CloudFade = 70;

    /// <summary>How fast the far hills go by (px a second): half the clouds' speed, an eighth of
    /// the ground's - the furthest thing there is.</summary>
    private const double HillSpeed = ObstacleSpeed / 8;

    /// <summary>How strong the hills are drawn: faint, so the picture behind the bar (the cover's
    /// colors, the aurora) still shows through and the duck and its things stand out.</summary>
    private const double HillAlpha = 0.3;

    /// <summary>The height of the hills (art pixels) at world column <paramref name="j"/>: two
    /// slow swells of different lengths, so the line never quite repeats.</summary>
    private static int HillHeight(int j) =>
        (int)Math.Round(2 + 6 * (0.5 + 0.5 * Math.Sin(j * 0.022 + 0.7)) + 2 * (0.5 + 0.5 * Math.Sin(j * 0.061 + 2.1)));

    /// <summary>Now and then a lone tree stands on a hilltop (more of them crowded the line into
    /// a band).</summary>
    private static bool TreeAt(int j) => (j % 233 == 40 || j % 317 == 7) && HillHeight(j) >= 6;

    private static readonly string[] TreeArt =
    [
        "..XXX..",
        ".XXXXX.",
        "XXXXXXX",
        ".XXXXX.",
        "...X...",
        "...X...",
    ];

    /// <summary>A far line of low rolling hills along the ground, one flat color (the cover's,
    /// greyed) and faint, with a tree here and there, drifting by slower than the clouds and
    /// fading at the bar's ends like them. Like everything here they move with the song. Drawn
    /// as columns an art pixel wide, side by side columns of the same height as one rectangle.</summary>
    private void DrawHills(DrawingContext dc, double ground, double pixel, Color accent, bool night)
    {
        // at night mostly a deep blue (the cover's color only a touch)
        var tone = night
            ? Color.FromRgb((byte)((accent.R + 3 * 0x30) / 4), (byte)((accent.G + 3 * 0x3C) / 4), (byte)((accent.B + 3 * 0x66) / 4))
            : Color.FromRgb((byte)((accent.R + 0x90) / 2), (byte)((accent.G + 0x90) / 2), (byte)((accent.B + 0x90) / 2));
        var strength = night ? 0.3 : HillAlpha;
        Color Shade(double alpha) => Color.FromArgb((byte)(255 * strength * alpha), tone.R, tone.G, tone.B);
        // how faint at screen x: fading out over the last stretch at either end, in a few steps
        // so neighboring columns still share a rectangle
        double Fade(double x) => Math.Round(Math.Clamp(Math.Min(x - Left, Right - x) / CloudFade, 0, 1) * 8) / 8;

        var shift = RunnerNow * HillSpeed;
        var first = (int)Math.Floor((Left + shift) / pixel);
        var last = (int)Math.Ceiling((Right + shift) / pixel);
        int runStart = first, runHeight = -1;
        double runFade = -1;
        for (var j = first; j <= last + 1; j++)
        {
            var x = j * pixel - shift;
            var height = j <= last ? HillHeight(j) : -1;
            var fade = j <= last ? Fade(x + pixel / 2) : -1;
            if (height == runHeight && fade == runFade) continue;
            if (runHeight > 0 && runFade > 0)
            {
                var x0 = OnGrid(runStart * pixel - shift);
                var x1 = OnGrid(j * pixel - shift);
                dc.DrawRectangle(Solid(Shade(runFade)), null, new Rect(x0, ground - runHeight * pixel, x1 - x0, runHeight * pixel));
            }
            runStart = j;
            runHeight = height;
            runFade = fade;
        }

        for (var j = first; j <= last; j++)
        {
            if (!TreeAt(j)) continue;
            var left = j * pixel - shift - TreeArt[0].Length / 2 * pixel;
            var fade = Fade(left + TreeArt[0].Length * pixel / 2);
            if (fade <= 0) continue;
            var brush = Solid(Shade(fade));
            // standing on the hill (sunk into it, the overlap drew darker: both are see-through)
            var top = ground - (HillHeight(j) + TreeArt.Length) * pixel;
            for (var r = 0; r < TreeArt.Length; r++)
                for (var c = 0; c < TreeArt[r].Length; c++)
                    if (TreeArt[r][c] == 'X')
                        dc.DrawRectangle(brush, null, new Rect(OnGrid(left) + c * pixel, OnGrid(top) + r * pixel, pixel, pixel));
        }
    }

    /// <summary>Clouds in the sky behind everything, faint, at a few heights above the duck's
    /// head. Like the obstacles they move with the song (stopping when it pauses), going round
    /// the bar: one about every 230 px of it.</summary>
    private void DrawClouds(DrawingContext dc, double ground, double pixel, bool night)
    {
        var span = Right - Left + 80;
        var count = Math.Max(2, (int)(span / 230));
        var now = RunnerNow;
        for (var i = 0; i < count; i++)
        {
            // at night only every other one, and dimmer
            if (night && i % 2 == 1) continue;
            var art = (night ? CloudArt : CloudDayArt)[i % CloudArt.Length];
            // spread out evenly, each nudged a little so they don't look lined up
            var offset = (i + 0.35 * Math.Sin(i * 2.4)) * span / count;
            var x = offset - now * CloudSpeed;
            x = Left - 40 + (x % span + span) % span;
            // art pixels off the ground to its bottom: its top no higher than the eagle's wings go
            var altitude = 13 + (i * 2) % 3;
            // fading in as it comes past the right end and out before the left one (it went
            // round to the other end at full strength, so it vanished all at once); gone
            // entirely by the time it goes round
            var width = art[0].Length * pixel;
            var fade = Math.Clamp(Math.Min(x - Left, Right - (x + width)) / CloudFade, 0, 1);
            if (fade <= 0) continue;
            DrawSprite(dc, art, OnGrid(x), ground - altitude * pixel, pixel, (night ? 0.35 : 0.8) * fade);
        }
    }

    /// <summary>Each picture's outline: the clear cells next to its pixels (and just outside it),
    /// worked out once per picture.</summary>
    private static readonly Dictionary<string[], (int Row, int Column)[]> _outlines = new(ReferenceEqualityComparer.Instance);

    private static (int Row, int Column)[] OutlineOf(string[] art)
    {
        if (_outlines.TryGetValue(art, out var known)) return known;
        bool Filled(int r, int c) => r >= 0 && r < art.Length && c >= 0 && c < art[r].Length && ArtColor(art[r][c]) is not null;
        var width = art.Max(row => row.Length);
        var cells = new List<(int, int)>();
        for (var r = -1; r <= art.Length; r++)
            for (var c = -1; c <= width; c++)
                if (!Filled(r, c) && (Filled(r - 1, c) || Filled(r + 1, c) || Filled(r, c - 1) || Filled(r, c + 1)))
                    cells.Add((r, c));
        return _outlines[art] = [.. cells];
    }

    /// <summary>A soft dark outline round a picture, for the light theme: white and pale parts (the
    /// fox's muzzle and tail tip, the eagle's head, the milk can, the toadstool's stem) were lost
    /// on it. The duck, the egg and the house have outlines of their own. (On the dark theme a
    /// light outline looked like a grey rim, out of place on the grey background; the night hills
    /// are kept faint instead, so the dark wood shows against them.)</summary>
    private void DrawOutline(DrawingContext dc, string[] art, double left, double bottom, double pixel, double alpha)
    {
        var brush = Solid(Color.FromArgb((byte)(150 * alpha), 0x4A, 0x40, 0x3A));
        var top = bottom - art.Length * pixel;
        foreach (var (r, c) in OutlineOf(art))
            dc.DrawRectangle(brush, null, new Rect(left + c * pixel, top + r * pixel, pixel, pixel));
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
        // the owl: grey-brown feathers, their pale edges, its face
        'M' => Color.FromRgb(0x8A, 0x74, 0x5E),
        'N' => Color.FromRgb(0xBC, 0xA8, 0x8C),
        'H' => Color.FromRgb(0xF0, 0xE6, 0xD0),
        // the barrel's hoops where they turn away
        'i' => Color.FromRgb(0x62, 0x64, 0x6E),
        // stone: lit top, body, shade
        'x' => Color.FromRgb(0xC8, 0xC6, 0xBE),
        'a' => Color.FromRgb(0x9C, 0x98, 0x8E),
        'z' => Color.FromRgb(0x6E, 0x6A, 0x64),
        // red (the mushroom's cap) and its shade; leaves (and the caterpillar and the grasshopper)
        'R' => Color.FromRgb(0xE4, 0x42, 0x34),
        'Q' => Color.FromRgb(0xA8, 0x28, 0x22),
        'G' => Color.FromRgb(0x8C, 0xB8, 0x40),
        // the hedgehog's spines (and their light tips: 'f')
        'u' => Color.FromRgb(0x84, 0x60, 0x40),
        // the worm and its shade
        'P' => Color.FromRgb(0xF4, 0x9A, 0xA8),
        'S' => Color.FromRgb(0xCC, 0x68, 0x7C),
        // a cloud's white and its shaded underside
        'U' => Color.FromRgb(0xF2, 0xF6, 0xFC),
        'V' => Color.FromRgb(0xB8, 0xC8, 0xDC),
        // a cloud by day and its underside
        'A' => Color.FromRgb(0xD6, 0xE2, 0xF2),
        'B' => Color.FromRgb(0xA6, 0xBA, 0xD4),
        // the house: inside the door, the window dark and lit; the hedgehog's face
        'D' => Color.FromRgb(0x2C, 0x20, 0x18),
        'J' => Color.FromRgb(0x3A, 0x40, 0x58),
        'L' => Color.FromRgb(0xFF, 0xD8, 0x70),
        'C' => Color.FromRgb(0xF0, 0xDC, 0xC0),
        _ => null,
    };

    // ---- the art, all facing the way it moves: the duck right, everything coming at it left ----

    private static readonly string[] DuckArt =
    [
        ".....KKKK....",
        "....KYYYYK...",
        "...KYYYEYYKOO",
        "...KYYYYYYOOO",
        "K..KYYYYYYK..",
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
            "...KYYYEYYKOO",
            "..WKYYYYYYOOO",
            "KWWKYYYYYYK..",
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
            "...KYYYEYYKOO",
            "...KYYYYYYOOO",
            "K..KYYYYYYK..",
            "KYK.KYYYYK...",
            "KYYKYYYYYYK..",
            "KYYYWWWWYYYK.",
            ".KYYWWWWWYYK.",
            "..KYWWWWYYK..",
            "...KWWWKKK...",
        ],
    ];

    /// <summary><see cref="DuckFlap"/> with the beak open: the upper half stays, the lower half
    /// drops a row, and the dark inside of the mouth shows between them.</summary>
    private static readonly string[][] DuckFlapQuack = [.. DuckFlap.Select(OpenBeak)];

    /// <summary>The running duck quacking on a strong beat.</summary>
    private static readonly string[] DuckQuack = OpenBeak(DuckArt);

    /// <summary>The beak is the last three columns of rows 2-4 in every duck frame.</summary>
    private static string[] OpenBeak(string[] art)
    {
        var open = (string[])art.Clone();
        open[3] = open[3][..^3] + "OE.";
        open[4] = open[4][..^3] + "KOO";
        return open;
    }

    /// <summary>The duck pecking up food: tail up, its head bent down in front of it nearly to
    /// the ground, the beak at the bottom.</summary>
    private static readonly string[] DuckPeckArt =
    [
        "................",
        "................",
        ".K..............",
        "KYK.............",
        "KYYKKKKKKK.KKK..",
        ".KYYYYYYYYKYYYK.",
        ".KYYYYYYYYYYEYYK",
        ".KYYWWWWYYYYYYYK",
        "..KYYWWWYYYYYYK.",
        "...KYYYYYYKKKOO.",
        "....KKKKKK....O.",
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

    /// <summary>A wooden barrel standing up: its staves bulge out in the middle (the outline
    /// curves like the pumpkin's), lit from the upper left like the rest, a dark seam between two
    /// staves, and two iron hoops whose ends are darker where they turn away.</summary>
    private static readonly string[] BarrelArt =
    [
        "..cbbbd..",
        ".cblbbbd.",
        "immmmmmmi",
        "blbbdbbbd",
        "blbbdbbbd",
        "immmmmmmi",
        ".cblbbbd.",
        "..ddddd..",
    ];

    /// <summary>The fox, running at the duck: tall pointed ears (orange to the tip: dark tips were
    /// lost against the dark screen saver), an eye with a light
    /// glint beside it, a white muzzle reaching out ahead of its head to a dark nose, a white chest,
    /// a long bushy tail with a white tip, and legs in a darker orange (the front ones under its
    /// chest: set further forward, under its muzzle, they looked out of place) (dark legs were lost against
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

    /// <summary>The fox sat down, turned to face the way the duck went: looking up at it in the
    /// air (ears laid back, nose up)...</summary>
    private static readonly string[] FoxSitLookUp =
    [
        "....o..o....k.",
        "....oo.oo..ww.",
        "....ooooooww..",
        "....ooowkoo...",
        "....oooooow...",
        ".....ooooo....",
        ".....oowwo....",
        "oo..ooowwo....",
        "wooooooowq....",
        ".oooooo.qq....",
    ];

    /// <summary>...then just gazing off to the right, ...</summary>
    private static readonly string[] FoxSitGaze =
    [
        "......o..o....",
        "......oo.oo...",
        ".....oooooo...",
        "....oooowkoo..",
        "....oooooowwwk",
        "....oooooww...",
        ".....oowwo....",
        "oo..ooowwo....",
        "wooooooowq....",
        ".oooooo.qq....",
    ];

    /// <summary>...blinking now and then.</summary>
    private static readonly string[] FoxSitBlink = [.. FoxSitGaze.Select((row, i) => i == 3 ? row.Replace('k', 'o') : row)];

    private static readonly string[][] FoxRun =
    [
        [.. FoxBody, "....qq.....qq....", "...q..q...q..q..."],
        [.. FoxBody, ".....qq....qq....", ".....q.q...q.q..."],
        [.. FoxBody, "......qq..qq.....", ".......qqqq......"],
        [.. FoxBody, ".....qq....qq....", "......qq....qq..."],
    ];

    /// <summary>A pumpkin: round and wide, a stem on top, a shine, and two deep ridges that start
    /// at the stem, bow outward with the belly and come back in at the bottom (straight ones made
    /// it look like a flat box).</summary>
    private static readonly string[] PumpkinArt =
    [
        "....g....",
        "..pqgqp..",
        ".hqpppqp.",
        "pqpppppqp",
        "pqpppppqp",
        ".pqpppqp.",
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

    /// <summary>The eagle, flying at the duck, drawn round and chunky like the fox and the duck: a
    /// round white head with a dark eye and a sharp yellow beak whose tip hooks down, a plump brown body with a
    /// paler belly, a short white tail, yellow talons tucked back under it (hanging down,
    /// they looked like a cow's udder), and one broad wing with
    /// fingered tips beating in four steps - raised, level, lowered, level. Its belly stays a row
    /// above the picture's bottom, so it flies over the ducked duck and into the standing one.</summary>
    private static readonly string[][] EagleFlap =
    [
        [
            "............f.f.f..",
            "...........fffffff.",
            "..........feeeeef..",
            ".........feeeeef...",
            "....vvv.feeeeef....",
            "..yvvvvvfeeee......",
            "yyyvkvvveeeeeeeev..",
            "y.yvvvvveeeeeeeeevv",
            ".....vveeffffeeeev.",
            "........ffffffyy...",
            "...................",
        ],
        [
            "...................",
            "...................",
            "...................",
            ".............f.f.f.",
            "....vvv..ffffffff..",
            "..yvvvvvfeeeeeeef..",
            "yyyvkvvveeeeeeeev..",
            "y.yvvvvveeeeeeeeevv",
            ".....vveeffffeeeev.",
            "........ffffffyy...",
            "...................",
        ],
        [
            "...................",
            "...................",
            "...................",
            "...................",
            "....vvv............",
            "..yvvvvv.eeeee.....",
            "yyyvkvvveeeeeeeev..",
            "y.yvvvvveeeeeeeeevv",
            ".....vveeeeeeeeeev.",
            "........feeeeeyy...",
            ".........ffeef.f.f.",
        ],
        [
            "...................",
            "...................",
            "...................",
            ".............f.f.f.",
            "....vvv..ffffffff..",
            "..yvvvvvfeeeeeeef..",
            "yyyvkvvveeeeeeeev..",
            "y.yvvvvveeeeeeeeevv",
            ".....vveeffffeeeev.",
            "........ffffffyy...",
            "...................",
        ],
    ];

    /// <summary>At night, in the eagle's place, a horned owl: ear tufts, a pale face with big
    /// yellow eyes looking out at you and a little grey beak, grey-brown feathers, its wings
    /// beating like the eagle's (it's the eagle's frames, recolored, with an owl's head).</summary>
    private static readonly string[][] OwlFlap =
    [
        [
            "............N.N.N..",
            "...........NNNNNNN.",
            "..........NMMMMMN..",
            ".M...M...NMMMMMN...",
            ".MMMMMM.NMMMMMN....",
            "MHHMHHMMNMMMM......",
            "HykHykMMMMMMMMMMN..",
            "MHHzHHMMMMMMMMMMMNN",
            ".MMMMMMMMNNNNMMMMN.",
            "........NNNNNNNN...",
            "...................",
        ],
        [
            "...................",
            "...................",
            "...................",
            ".M...M.......N.N.N.",
            ".MMMMMM..NNNNNNNN..",
            "MHHMHHMMNMMMMMMMN..",
            "HykHykMMMMMMMMMMN..",
            "MHHzHHMMMMMMMMMMMNN",
            ".MMMMMMMMNNNNMMMMN.",
            "........NNNNNNNN...",
            "...................",
        ],
        [
            "...................",
            "...................",
            "...................",
            ".M...M.............",
            ".MMMMMM............",
            "MHHMHHMM.MMMMM.....",
            "HykHykMMMMMMMMMMN..",
            "MHHzHHMMMMMMMMMMMNN",
            ".MMMMMMMMMMMMMMMMN.",
            "........NMMMMMNN...",
            ".........NNMMN.N.N.",
        ],
        [
            "...................",
            "...................",
            "...................",
            ".M...M.......N.N.N.",
            ".MMMMMM..NNNNNNNN..",
            "MHHMHHMMNMMMMMMMN..",
            "HykHykMMMMMMMMMMN..",
            "MHHzHHMMMMMMMMMMMNN",
            ".MMMMMMMMNNNNMMMMN.",
            "........NNNNNNNN...",
            "...................",
        ],
    ];


    /// <summary>Clouds: puffs on top, a flat shaded underside.</summary>
    private static readonly string[][] CloudArt =
    [
        [
            "......UUU.....",
            "...UUUUUUU.UU.",
            "..UUUUUUUUUUUU",
            ".UUUUUUUUUUUUU",
            "..VVVVVVVVVVV.",
        ],
        [
            "...UUU...",
            ".UUUUUUU.",
            "UUUUUUUUU",
            ".VVVVVVV.",
        ],
        [
            ".......UUU........",
            "....UUUUUUU.UUU...",
            "..UUUUUUUUUUUUUUU.",
            ".UUUUUUUUUUUUUUUUU",
            "..VVVVVVVVVVVVVVV.",
        ],
    ];

    /// <summary>The clouds by day, a pale blue: by day the theme is light, and white clouds were lost
    /// on it. (After <see cref="CloudArt"/>: static fields are set in order.)</summary>
    private static readonly string[][] CloudDayArt = [.. CloudArt.Select(c => c.Select(row => row.Replace('U', 'A').Replace('V', 'B')).ToArray())];

    /// <summary>A stone: a low rounded lump, lit on top, shaded at its right and underneath.</summary>
    private static readonly string[] RockArt =
    [
        "...xxaa...",
        "..xaaaaaz.",
        ".xaaaaaazz",
        "aaazaaazzz",
    ];

    /// <summary>A toadstool: a red cap with white spots over a white stem, its rim in shade.</summary>
    private static readonly string[] MushroomArt =
    [
        "..RRR..",
        ".RwRRR.",
        "RRRRwRR",
        "QQQQQQQ",
        "..wwn..",
        "..wwn..",
    ];

    /// <summary>A hedgehog trundling at the duck: a cream pointed face with a dark eye and nose
    /// poking out of a dome of brown spines whose light tips make a jagged edge, little dark feet
    /// stepping in two frames, and small and round with a pink cheek, so it looks cute. (Its face
    /// was once tan like its spines, and the whole read as a brown lump; then, bigger and long,
    /// it looked like a mole.)</summary>
    private static readonly string[] HedgehogBody =
    [
        "....f.f.f..",
        "...fufufuf.",
        "..Cuuuuuuuf",
        ".CkCuuuuuuu",
        "kCPCCuuuuu.",
    ];

    private static readonly string[][] HedgehogWalk =
    [
        [.. HedgehogBody, "..k.k..k.k."],
        [.. HedgehogBody, "...k.k..k.k"],
    ];

    /// <summary>The hedgehog as the duck comes near (<paramref name="ahead"/>: how far in front of
    /// the duck it is, in art pixels): it tucks its face in, then rolls up into a ball with its
    /// spines up, shivering, until the duck has jumped it and gone on; then it uncurls again.
    /// Null when it's just walking.</summary>
    private string[]? HedgehogPose(double ahead) =>
        ahead is > 45 or < -36 ? null
        : ahead is > 38 or < -28 ? HedgehogTuck
        : (int)(_sceneTime * 12) % 4 == 0 ? HedgehogBall : HedgehogBristle;

    /// <summary>Tucking its face in...</summary>
    private static readonly string[] HedgehogTuck =
    [
        "...f.f.f...",
        "..fufufuf..",
        ".fuuuuuuuf.",
        "CkCuuuuuuuf",
        "CPuuuuuuuu.",
        ".uuuuuuuu..",
    ];

    /// <summary>...rolled up in a ball, its spines sticking out all round...</summary>
    private static readonly string[] HedgehogBall =
    [
        "...f.f.f...",
        ".f.fufuf.f.",
        "..fuuuuuf..",
        "ffuuuuuuuff",
        "..uuuuuuu..",
        "...uuuuu...",
    ];

    /// <summary>...and bristling, the spines raised further (it shivers between the two).</summary>
    private static readonly string[] HedgehogBristle =
    [
        "..f..f..f..",
        "f..fufuf..f",
        ".ffuuuuuff.",
        "..uuuuuuu..",
        "ffuuuuuuuff",
        "...uuuuu...",
    ];

    /// <summary>A wooden crate: a light frame round a darker panel crossed by a brace, shaded on
    /// the right and underneath.</summary>
    private static readonly string[] CrateArt =
    [
        "lllllllb",
        "lbbbbbld",
        "lbbbblbd",
        "lbbblbbd",
        "lbblbbbd",
        "lblbbbbd",
        "llbbbbbd",
        "lddddddd",
    ];

    /// <summary>A sunflower taller than the duck: yellow petals round a brown middle, on a green
    /// stem with a leaf either side.</summary>
    private static readonly string[] SunflowerArt =
    [
        "..yyy..",
        ".ybbby.",
        "yybdbyy",
        ".ybbby.",
        "..yyy..",
        "...g...",
        ".GGg...",
        "..Gg...",
        "...gGG.",
        "...gG..",
    ];

    /// <summary>Duck food: an earthworm wriggling (its humps travel along it in two frames)...</summary>
    private static readonly string[][] WormWiggle =
    [
        [
            ".PP..PP..",
            "PSSPPSSP.",
            "P..SS..SS",
        ],
        [
            "P..PP..P.",
            "SPPSSPPSP",
            ".SS..SS..",
        ],
    ];

    /// <summary>...a green caterpillar crawling at the duck: a darker head with an eye, its
    /// back in round segments, little feet stepping in two frames...</summary>
    private static readonly string[][] CaterpillarCrawl =
    [
        [
            "gg.GG.GG.G",
            "kgGGGGGGGG",
            ".g.g.g.g.g",
        ],
        [
            "gg..GG.GG.",
            "kgGGGGGGGG",
            "g.g.g.g.g.",
        ],
    ];

    /// <summary>...and a grasshopper, small and round-headed: a curled antenna, a big dark eye,
    /// a short green body and its hind leg folded up into a little peak over the back (without
    /// it, it read as a lizard); sitting, then in the air with the leg stretched out behind.</summary>
    private static readonly string[][] GrasshopperArt =
    [
        [
            "gg.......",
            "..g...g..",
            ".GGG.g.g.",
            "GkGGGGGGg",
            ".GGGGGG..",
            "..g.g....",
        ],
        [
            "gg.......",
            "..g......",
            ".GGG.....",
            "GkGGGGGgg",
            ".GGGGGG.g",
            "..g.g....",
        ],
    ];

    /// <summary>The egg the duck hatches from at the start of a song: cream, outlined like the duck, a shine at its upper left, shaded at its lower right.</summary>
    private static readonly string[] EggArt =
    [
        "....KKK....",
        "...KwwwK...",
        "..KwwwwwK..",
        ".KwUwwwwwK.",
        ".KwUwwwwwK.",
        "KwwwwwwwwwK",
        "KwwwwwwwwwK",
        "KwwwwwwwwnK",
        "KwwwwwwwwnK",
        "KwwwwwwwnnK",
        ".KwwwwwnnK.",
        "..KwwnnnK..",
        "...KKKKK...",
    ];

    /// <summary>...a crack starting across it...</summary>
    private static readonly string[] EggCrackingArt =
    [
        "....KKK....",
        "...KwwwK...",
        "..KwwwwwK..",
        ".KwUwwwwwK.",
        ".KwUwwwwwK.",
        "KwKwKwwwwwK",
        "KKwKwKwwwwK",
        "KwwwwwwwwnK",
        "KwwwwwwwwnK",
        "KwwwwwwwnnK",
        ".KwwwwwnnK.",
        "..KwwnnnK..",
        "...KKKKK...",
    ];

    /// <summary>...cracked all the way round...</summary>
    private static readonly string[] EggCrackedArt =
    [
        "....KKK....",
        "...KwwwK...",
        "..KwwwwwK..",
        ".KwUwwwwwK.",
        ".KwUwwwwwK.",
        "KwKwKwKwKwK",
        "KKwKwKwKwKK",
        "KwwwwwwwwnK",
        "KwwwwwwwwnK",
        "KwwwwwwwnnK",
        ".KwwwwwnnK.",
        "..KwwnnnK..",
        "...KKKKK...",
    ];

    /// <summary>...its top, flying off as the duck comes out...</summary>
    private static readonly string[] EggTopArt =
    [
        "....KKK....",
        "...KwwwK...",
        "..KwwwwwK..",
        ".KwUwwwwwK.",
        ".KwUwwwwwK.",
        "KwKwKwKwKwK",
        "KK.K.K.K.KK",
    ];

    /// <summary>...and its bottom, left behind on the ground as the duck runs off.</summary>
    private static readonly string[] EggBottomArt =
    [
        "..K.K.K.K..",
        "KKwKwKwKwKK",
        "KwwwwwwwwnK",
        "KwwwwwwwwnK",
        "KwwwwwwwnnK",
        ".KwwwwwnnK.",
        "..KwwnnnK..",
        "...KKKKK...",
    ];
}
