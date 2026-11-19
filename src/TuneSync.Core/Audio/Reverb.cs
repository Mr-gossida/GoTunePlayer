namespace TuneSync.Core.Audio;

/// <summary>A kind of space the reverb imitates. <paramref name="Size"/> scales the echo paths
/// (1 = a concert hall), <paramref name="DecaySeconds"/> is how long the tail takes to fall
/// 60 dB, <paramref name="HighDecay"/> how much sooner the highs die out than the lows (soft
/// walls and air swallow them first: lower is darker), and the pre-delay is the gap before the
/// first reflections, which keeps the dry sound clear in front of the room.</summary>
public sealed record ReverbRoom(string Id, string Name, double Size, double DecaySeconds, double PreDelayMs, double HighDecay);

/// <summary>The rooms the reverb offers.</summary>
public static class ReverbRooms
{
    public const string DefaultId = "hall";

    /// <summary>The 길이 control scales a room's decay by this much either way.</summary>
    public const double MinDecayScale = 0.5, MaxDecayScale = 2;

    public static readonly IReadOnlyList<ReverbRoom> All =
    [
        new("room",      "작은 방",  0.45, 0.5, 5,  0.5),
        new("studio",    "스튜디오", 0.7,  0.9, 10, 0.7),
        new("hall",      "홀",       1.0,  1.7, 20, 0.55),
        new("cathedral", "대성당",   1.5,  3.2, 32, 0.45),
    ];

    public static ReverbRoom Find(string? id) => All.FirstOrDefault(r => r.Id == id) ?? All.First(r => r.Id == DefaultId);
}

/// <summary>A room around the music: an eight-line feedback delay network (the structure of
/// most good studio reverbs - eight echo paths of unrelated lengths that feed each other through
/// a mixing matrix, so the echoes multiply into a smooth tail instead of the ringing of a few
/// combs), with highs fading faster than lows in each path, a pre-delay and four all-pass
/// diffusers in front (they smear the attack into a dense wash before it enters the room).
/// The send leaves out the deep bass and the hiss at the top: a room full of boomy low end is
/// what makes reverb sound muddy, and that is the opposite of what it is for here.
/// The dry sound passes untouched; the room is added on top. Changing the room fades the wet
/// sound out and back in (the echo paths change length, which would otherwise crackle); the
/// amount and the decay glide. Nothing allocates once it is built.</summary>
public sealed class Reverb
{
    private const int LineCount = 8;

    // Echo path lengths at size 1, in ms: spread between ~37 and ~77 ms with no simple ratios
    // between them, so their echoes don't line up into a pitch.
    private static readonly double[] LineMs = [37.1, 41.3, 46.7, 51.9, 57.7, 63.1, 69.7, 76.3];
    private static readonly double[] DiffuserMs = [4.77, 3.59, 12.73, 9.31];
    private static readonly float[] DiffuserGain = [0.7f, 0.7f, 0.6f, 0.6f];

    // How the mono send enters the paths and how the left and right outputs take them: rows of
    // a Hadamard matrix, so left and right hear different mixtures of the same room (that is
    // what makes it wide) and neither repeats the input pattern.
    private static readonly float[] InSigns = [1, -1, -1, 1, -1, 1, 1, -1];
    private static readonly float[] LeftSigns = [1, -1, 1, -1, 1, -1, 1, -1];
    private static readonly float[] RightSigns = [1, 1, -1, -1, 1, 1, -1, -1];

    private const double MaxSize = 1.5, MaxPreDelayMs = 40;
    private const double SendLowCutHz = 200, SendHighCutHz = 7000;

    /// <summary>The wet level fades in and out over about this long.</summary>
    private const double WetSettleSeconds = 0.04;

    /// <summary>A decay change settles over about this long.</summary>
    private const double DecaySettleSeconds = 0.15;

    /// <summary>Scales the tail so that at a mix of 1 it has about the loudness of the dry
    /// sound, whatever the room (measured with noise, see the tests).</summary>
    private const double Loudness = 1.8;

    private readonly int _sampleRate;

    private readonly float[] _lines;
    private readonly int[] _lineStart = new int[LineCount], _lineLength = new int[LineCount], _linePos = new int[LineCount];
    private readonly float[] _damp = new float[LineCount];
    private readonly float[] _feed = new float[LineCount], _pole = new float[LineCount];

    private readonly float[] _diffusers;
    private readonly int[] _diffStart = new int[4], _diffLength = new int[4], _diffPos = new int[4];

    private readonly float[] _preDelay;
    private int _preLength = 1, _prePos;

    // The send's low cut (second order, in double: a 200 Hz filter at 192 kHz is too fine for
    // float) and high cut (first order).
    private readonly double _hb0, _hb1, _hb2, _ha1, _ha2;
    private double _h1, _h2;
    private readonly float _lowPass;
    private float _lp;

    private ReverbRoom? _room;
    private ReverbRoom _roomTarget = ReverbRooms.Find(null);
    private float _mixTarget;
    private double _decayTarget = 1;
    private double _decay = 1;
    private double _meanLength = 1;
    private float _wet, _trim;
    private bool _clear = true;
    private int _quietFrames, _quietLimit;

    /// <summary>-240 dB: far below anything that can be heard, or even stored in 24 bits.</summary>
    private const float Inaudible = 1e-12f;
    private readonly double _wetStep, _decayStep;

    public Reverb(int sampleRate)
    {
        _sampleRate = sampleRate;
        var lines = 0;
        foreach (var ms in LineMs) lines += Samples(ms * MaxSize) + 2;
        _lines = new float[lines];
        var diffusers = 0;
        foreach (var ms in DiffuserMs) diffusers += Samples(ms) + 1;
        _diffusers = new float[diffusers];
        _preDelay = new float[Samples(MaxPreDelayMs) + 1];

        for (int i = 0, at = 0; i < 4; i++)
        {
            _diffStart[i] = at;
            _diffLength[i] = Math.Max(1, Samples(DiffuserMs[i]));
            at += _diffLength[i] + 1;
        }

        // RBJ cookbook high-pass, Butterworth Q.
        var w = 2 * Math.PI * SendLowCutHz / sampleRate;
        var alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        _hb0 = (1 + cos) / 2 / a0;
        _hb1 = -(1 + cos) / a0;
        _hb2 = _hb0;
        _ha1 = -2 * cos / a0;
        _ha2 = (1 - alpha) / a0;
        _lowPass = (float)(1 - Math.Exp(-2 * Math.PI * Math.Min(SendHighCutHz, sampleRate * 0.45) / sampleRate));

        // per 32-frame step (the processor's)
        _wetStep = 1 - Math.Exp(-32.0 / (WetSettleSeconds * sampleRate));
        _decayStep = 1 - Math.Exp(-32.0 / (DecaySettleSeconds * sampleRate));
    }

    /// <summary>Whether anything is being added (or still fading out).</summary>
    public bool IsActive => _wet != 0 || _mixTarget != 0;

    /// <summary>Where to head: the room, the wet level (0 = off) and the decay time. Called on
    /// the audio thread, between blocks.</summary>
    public void SetTarget(ReverbRoom room, double mix, double decaySeconds)
    {
        _roomTarget = room;
        _mixTarget = (float)Math.Max(0, mix);
        _decayTarget = Math.Max(0.05, decaySeconds);
    }

    /// <summary>Jumps to the target instead of gliding (before the first sound of a song).</summary>
    public void Settle()
    {
        if (!ReferenceEquals(_room, _roomTarget)) Configure(_roomTarget);
        _decay = _decayTarget;
        Tune();
        _wet = _mixTarget;
    }

    /// <summary>The same for float frames (tests and tools; the player runs the 64-bit one).</summary>
    public void Process(Span<float> frames) => Wide.Run(frames, this, static (reverb, wide) => reverb.Process(wide));

    /// <summary>Adds the room to stereo frames, left/right interleaved, in place. Meant for short
    /// steps (the processor's 32 frames): levels move once per call. The frames are 64-bit; the
    /// room's own paths are float (its tail sits far below anything their precision could touch).</summary>
    public void Process(Span<double> frames)
    {
        var roomChanging = !ReferenceEquals(_room, _roomTarget);
        if (_wet == 0 && (roomChanging || _mixTarget == 0))
        {
            // Silent: a new room is put in place now, and nothing more is done until it's wanted.
            if (roomChanging) Configure(_roomTarget);
            else if (!_clear) Clear();
            if (_mixTarget == 0) return;
            roomChanging = false;
        }

        // Levels for this step.
        var want = roomChanging ? 0f : _mixTarget;
        var wetNext = _wet + (float)((want - _wet) * _wetStep);
        if (Math.Abs(want - wetNext) < 1e-4f) wetNext = want;
        if (_decay != _decayTarget)
        {
            _decay += (_decayTarget - _decay) * _decayStep;
            if (Math.Abs(_decayTarget - _decay) < 1e-3) _decay = _decayTarget;
            Tune();
        }
        var gain = _wet * _trim;
        var gainNext = wetNext * _trim;
        var count = frames.Length / 2;
        var slope = count > 0 ? (gainNext - gain) / count : 0;
        _wet = wetNext;

        // An empty room with nothing coming in (paused, between songs, a silent passage) stays
        // empty: no work.
        if (_clear)
        {
            var silent = true;
            foreach (var v in frames)
                if (Math.Abs(v) >= Inaudible) { silent = false; break; }
            if (silent) return;
        }
        _clear = false;
        var loudest = 0f;

        Span<float> taps = stackalloc float[LineCount];
        for (var i = 0; i + 1 < frames.Length; i += 2)
        {
            // Send: mono, without the deep bass and the top.
            var x = (frames[i] + frames[i + 1]) * 0.5;
            var h = _hb0 * x + _h1;
            _h1 = _hb1 * x - _ha1 * h + _h2;
            _h2 = _hb2 * x - _ha2 * h;
            _lp = Flush(_lp + _lowPass * ((float)h - _lp));

            // Pre-delay.
            var d = _preDelay[_prePos];
            _preDelay[_prePos] = _lp;
            if (++_prePos >= _preLength) _prePos = 0;

            // Diffusers (Schroeder all-passes).
            for (var k = 0; k < 4; k++)
            {
                var at = _diffStart[k] + _diffPos[k];
                var g = DiffuserGain[k];
                var y = _diffusers[at] - g * d;
                _diffusers[at] = Flush(d + g * y);
                d = y;
                if (++_diffPos[k] >= _diffLength[k]) _diffPos[k] = 0;
            }

            // The network: read each path, darken and shorten it by its decay, mix (Householder:
            // each path gets itself minus a quarter of all of them, which loses no energy) and
            // write back with the new sound.
            var sum = 0f;
            for (var l = 0; l < LineCount; l++)
            {
                var v = _feed[l] * _lines[_lineStart[l] + _linePos[l]] + _pole[l] * _damp[l];
                v = Flush(v);
                _damp[l] = v;
                taps[l] = v;
                sum += v;
                loudest = Math.Max(loudest, Math.Abs(v));
            }
            sum *= 2f / LineCount;
            float left = 0, right = 0;
            for (var l = 0; l < LineCount; l++)
            {
                var v = taps[l];
                left += LeftSigns[l] * v;
                right += RightSigns[l] * v;
                _lines[_lineStart[l] + _linePos[l]] = Flush(v - sum + InSigns[l] * d);
                if (++_linePos[l] >= _lineLength[l]) _linePos[l] = 0;
            }

            loudest = Math.Max(loudest, Math.Abs((float)x));
            frames[i] += (double)gain * left;
            frames[i + 1] += (double)gain * right;
            gain += slope;
        }

        // Rung out: once nothing audible has come in or gone round any path for longer than the
        // longest way through, the room is emptied. (Rounding keeps the last crumbs of a tail
        // circling at around 1e-25 forever otherwise - harmless, but never quite silence.)
        if (loudest < Inaudible)
        {
            _quietFrames += count;
            if (_quietFrames > _quietLimit) Clear();
        }
        else
        {
            _quietFrames = 0;
        }
        if (Math.Abs(_h1) < 1e-30) _h1 = 0;
        if (Math.Abs(_h2) < 1e-30) _h2 = 0;
        // Faded out: what is left of the tail is dropped, so switching back on starts afresh
        // instead of fading in an echo of earlier music.
        if (_wet == 0) Clear();
    }

    /// <summary>Numbers too small to hear are dropped: they would decay into "denormal" floats,
    /// which the processor computes many times slower (a reverb tail ringing out in silence
    /// would otherwise eat CPU). Adding and taking away 1e-18 rounds anything below ~1e-25 to 0
    /// and leaves larger values all but untouched (well below -300 dB).</summary>
    private static float Flush(float v) => v + 1e-18f - 1e-18f;

    private int Samples(double ms) => (int)Math.Round(ms * _sampleRate / 1000);

    /// <summary>Sets the paths' lengths for a room and empties them.</summary>
    private void Configure(ReverbRoom room)
    {
        _room = room;
        var at = 0;
        var total = 0.0;
        for (var l = 0; l < LineCount; l++)
        {
            var length = Math.Max(3, Samples(LineMs[l] * Math.Clamp(room.Size, 0.1, MaxSize)));
            // odd and different from the one before (the table rises, so all differ)
            length |= 1;
            if (l > 0 && length <= _lineLength[l - 1]) length = _lineLength[l - 1] + 2;
            _lineStart[l] = at;
            _lineLength[l] = length;
            at += Samples(LineMs[l] * MaxSize) + 2;
            total += length;
        }
        _meanLength = total / LineCount;
        _preLength = Math.Clamp(Samples(room.PreDelayMs), 1, _preDelay.Length);
        _quietLimit = _preLength + _diffusers.Length + _lineLength[LineCount - 1];
        _decay = _decayTarget; // the room is silent now: no need to glide
        Tune();
        Clear();
    }

    private void Clear()
    {
        _quietFrames = 0;
        Array.Clear(_lines);
        Array.Clear(_diffusers);
        Array.Clear(_preDelay);
        Array.Clear(_damp);
        Array.Clear(_linePos);
        Array.Clear(_diffPos);
        _prePos = 0;
        _h1 = _h2 = 0;
        _lp = 0;
        _clear = true;
    }

    /// <summary>Each path's loss for the current decay time: a gain that takes the lows down
    /// 60 dB in the decay time, and a one-pole low-pass that takes the highs down 60 dB in a
    /// shorter time. Also the trim that keeps the tail's loudness about the same for every room
    /// and decay (a longer tail piles up more energy: about decay / path length).</summary>
    private void Tune()
    {
        if (_room is null) return;
        var high = _decay * Math.Clamp(_room.HighDecay, 0.05, 1);
        for (var l = 0; l < LineCount; l++)
        {
            var n = (double)_lineLength[l];
            var g = Math.Pow(10, -3 * n / (_sampleRate * _decay));
            var gh = Math.Pow(10, -3 * n / (_sampleRate * high));
            var r = gh / g;
            var a = (1 - r) / (1 + r);
            _pole[l] = (float)a;
            _feed[l] = (float)(g * (1 - a));
        }
        // energy of a loop of gain g: 1 / (1 - g^2), g^2 = 10^(-6 n / (rate * decay)) ~ 1 - 13.8 n / (rate * decay)
        // (measured: a shorter tail is still heard a little quieter than that alone says - the
        // early sound carries more of it - hence the small extra for short ones)
        _trim = (float)(Loudness / Math.Sqrt(LineCount) * Math.Sqrt(13.8 * _meanLength / (_sampleRate * _decay)) * Math.Pow(_decay, -0.15));
    }
}
