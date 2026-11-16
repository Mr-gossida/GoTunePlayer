using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>What the processor should be doing, as one unchangeable snapshot: the audio thread
/// picks up a new one between two blocks, so it never sees half of a change.</summary>
public sealed class ToneConfig
{
    /// <summary>The file's sound, untouched.</summary>
    public static readonly ToneConfig Neutral = new();

    private readonly double[] _gains = new double[ToneCurve.FilterCount];

    private ToneConfig() { }

    /// <summary>Level of each filter in running order (bass shelf, ten bands, treble shelf).</summary>
    public ReadOnlySpan<double> FilterGainsDb => _gains;

    /// <summary>Preamp minus the automatic headroom.</summary>
    public double GainDb { get; private init; }

    /// <summary>How much the automatic headroom took off (0 when it's off or nothing is boosted).</summary>
    public double HeadroomDb { get; private init; }

    public double Width { get; private init; } = 1;
    public double Balance { get; private init; }

    /// <summary>Whether peaks past full scale are held down.</summary>
    public bool Limiter { get; private init; } = true;

    public ReverbRoom ReverbRoom { get; private init; } = ReverbRooms.Find(null);

    /// <summary>How much of the room is added (0 = no reverb at all).</summary>
    public double ReverbMix { get; private init; }

    public double ReverbDecaySeconds { get; private init; } = 1;

    public static ToneConfig From(ToneSettings settings)
    {
        if (!settings.Enabled) return Neutral;
        var headroom = settings.AutoHeadroom ? ToneCurve.HeadroomDb(settings) : 0;
        var room = ReverbRooms.Find(settings.ReverbRoom);
        var config = new ToneConfig
        {
            GainDb = settings.PreampDb - headroom,
            HeadroomDb = headroom,
            Width = Math.Clamp(settings.Width, 0, 2),
            Balance = Math.Clamp(settings.Balance, -1, 1),
            Limiter = settings.Limiter,
            ReverbRoom = room,
            ReverbMix = settings.Reverb && double.IsFinite(settings.ReverbAmount) ? Math.Clamp(settings.ReverbAmount, 0, 1) : 0,
            ReverbDecaySeconds = room.DecaySeconds * (double.IsFinite(settings.ReverbDecay)
                ? Math.Clamp(settings.ReverbDecay, ReverbRooms.MinDecayScale, ReverbRooms.MaxDecayScale) : 1),
        };
        ToneCurve.FilterGains(settings, config._gains);
        return config;
    }
}

/// <summary>The player's sound chain, run on stereo frames as they go to the speakers:
/// preamp, bass shelf, ten equalizer bands, treble shelf, stereo width and balance, a reverb
/// (<see cref="Audio.Reverb"/>), a limiter that keeps boosts from clipping, and the volume (with a copy of the sound before the volume
/// kept for the live displays, <see cref="Scope"/>). Every level glides to a new setting over a
/// few hundredths of a second, so moving a slider while music plays never clicks or "zips".
/// Nothing here allocates once it is built.</summary>
public sealed class ToneProcessor
{
    /// <summary>Levels move in steps this many frames long; the filters are re-tuned per step.</summary>
    private const int Step = 32;

    /// <summary>A filter level crosses the whole range (24 dB) in this long.</summary>
    private const double FullSwingSeconds = 0.12;

    /// <summary>How fast the plain gains (preamp, width, balance, volume) settle.</summary>
    private const double GainSettleSeconds = 0.012;

    private readonly int _sampleRate;
    private readonly PeakLimiter _limiter;
    private readonly Reverb _reverb;
    private readonly double _filterStepDb;
    private readonly float _gainStep;

    private ToneConfig _config = ToneConfig.Neutral;
    private ToneConfig? _applied;
    private float _volumeTarget = 1;

    // Filters: where each level is now and where it is heading, its coefficients and its memory
    // (two values per channel).
    private readonly double[] _levelDb = new double[ToneCurve.FilterCount];
    private readonly double[] _levelTargetDb = new double[ToneCurve.FilterCount];
    private readonly BiquadCoefficients[] _filters = new BiquadCoefficients[ToneCurve.FilterCount];
    private readonly bool[] _filterOn = new bool[ToneCurve.FilterCount];
    private readonly double[] _memory = new double[ToneCurve.FilterCount * 4];

    // Plain gains: now / target.
    private float _gain = 1, _gainTarget = 1;
    private float _side = 1, _sideTarget = 1;
    private float _left = 1, _leftTarget = 1;
    private float _right = 1, _rightTarget = 1;
    private float _volume = 1;
    private bool _volumeSet;

    public ToneProcessor(int sampleRate)
    {
        _sampleRate = sampleRate;
        _limiter = new PeakLimiter(sampleRate);
        _reverb = new Reverb(sampleRate);
        Scope = new OutputScope(sampleRate);
        _filterStepDb = 2 * ToneCurve.MaxFilterGainDb * Step / (FullSwingSeconds * sampleRate);
        _gainStep = (float)(1 - Math.Exp(-Step / (GainSettleSeconds * sampleRate)));
        Array.Fill(_filters, BiquadCoefficients.Identity);
    }

    /// <summary>What comes out of the chain (before the volume), for the live displays.</summary>
    public OutputScope Scope { get; }

    /// <summary>How many frames later the sound comes out (the limiter's look-ahead).</summary>
    public int LatencyFrames => _limiter.LatencyFrames;

    /// <summary>The settings to glide to. Safe to set from any thread while <see cref="Process"/> runs.</summary>
    public ToneConfig Config
    {
        get => Volatile.Read(ref _config);
        set => Volatile.Write(ref _config, value);
    }

    /// <summary>0..1, applied after the limiter. Safe to set from any thread.</summary>
    public float Volume
    {
        get => Volatile.Read(ref _volumeTarget);
        set => Volatile.Write(ref _volumeTarget, Math.Clamp(value, 0, 1));
    }

    /// <summary>The most the limiter turned the sound down since this was last read, in dB (0 = not at all).</summary>
    public double TakeLimiterReductionDb()
    {
        var gain = _limiter.TakeLowestGain();
        return gain >= 1 ? 0 : -20 * Math.Log10(Math.Max(gain, 1e-6f));
    }

    /// <summary>Jumps straight to the current settings instead of gliding there (before the
    /// first sound of a song, so it doesn't start with a sweep).</summary>
    public void Settle()
    {
        TakeConfig();
        for (var i = 0; i < _levelDb.Length; i++)
        {
            _levelDb[i] = _levelTargetDb[i];
            Tune(i);
        }
        _gain = _gainTarget;
        _side = _sideTarget;
        _left = _leftTarget;
        _right = _rightTarget;
        _volume = Volume;
        _volumeSet = true;
        _reverb.Settle();
    }

    /// <summary>Stereo frames, left/right interleaved, processed in place.</summary>
    public void Process(Span<float> frames)
    {
        if (!_volumeSet) Settle();
        TakeConfig();
        for (var at = 0; at < frames.Length; at += Step * 2)
            ProcessStep(frames.Slice(at, Math.Min(Step * 2, frames.Length - at)));
    }

    private void TakeConfig()
    {
        var config = Config;
        if (ReferenceEquals(config, _applied)) return;
        _applied = config;
        config.FilterGainsDb.CopyTo(_levelTargetDb);
        _gainTarget = (float)Math.Pow(10, config.GainDb / 20);
        _sideTarget = (float)config.Width;
        _leftTarget = (float)Math.Min(1, 1 - config.Balance);
        _rightTarget = (float)Math.Min(1, 1 + config.Balance);
        _limiter.IsOn = config.Limiter;
        _reverb.SetTarget(config.ReverbRoom, config.ReverbMix, config.ReverbDecaySeconds);
    }

    private void ProcessStep(Span<float> frames)
    {
        // 1. Preamp (and headroom).
        var gainNext = Approach(_gain, _gainTarget);
        if (_gain != 1 || gainNext != 1)
        {
            var g = _gain;
            var slope = (gainNext - g) / (frames.Length / 2);
            for (var i = 0; i < frames.Length; i += 2)
            {
                frames[i] *= g;
                frames[i + 1] *= g;
                g += slope;
            }
        }
        _gain = gainNext;

        // 2. Filters, each over the whole step.
        for (var f = 0; f < _filters.Length; f++)
        {
            if (_levelDb[f] != _levelTargetDb[f])
            {
                var delta = _levelTargetDb[f] - _levelDb[f];
                if (Math.Abs(delta) > _filterStepDb)
                    _levelDb[f] += Math.Sign(delta) * _filterStepDb;
                else if (_levelTargetDb[f] == 0 && Math.Abs(delta) > 0.004)
                    _levelDb[f] *= 0.5; // fade the last bit out, so switching the filter off is inaudible
                else
                    _levelDb[f] = _levelTargetDb[f];
                Glide(f, frames);
                continue;
            }
            if (!_filterOn[f]) continue;

            var c = _filters[f];
            var m = f * 4;
            double l1 = _memory[m], l2 = _memory[m + 1], r1 = _memory[m + 2], r2 = _memory[m + 3];
            for (var i = 0; i < frames.Length; i += 2)
            {
                double x = frames[i];
                var y = c.B0 * x + l1;
                l1 = c.B1 * x - c.A1 * y + l2;
                l2 = c.B2 * x - c.A2 * y;
                frames[i] = (float)y;

                x = frames[i + 1];
                y = c.B0 * x + r1;
                r1 = c.B1 * x - c.A1 * y + r2;
                r2 = c.B2 * x - c.A2 * y;
                frames[i + 1] = (float)y;
            }
            // Numbers this small are silence, and slow to compute with: drop them.
            _memory[m] = Math.Abs(l1) < 1e-25 ? 0 : l1;
            _memory[m + 1] = Math.Abs(l2) < 1e-25 ? 0 : l2;
            _memory[m + 2] = Math.Abs(r1) < 1e-25 ? 0 : r1;
            _memory[m + 3] = Math.Abs(r2) < 1e-25 ? 0 : r2;
        }

        // 3. Stereo width (how much of the left/right difference is kept) and balance.
        float sideNext = Approach(_side, _sideTarget), leftNext = Approach(_left, _leftTarget), rightNext = Approach(_right, _rightTarget);
        if (_side != 1 || sideNext != 1 || _left != 1 || leftNext != 1 || _right != 1 || rightNext != 1)
        {
            var count = frames.Length / 2;
            float s = _side, l = _left, r = _right;
            float sSlope = (sideNext - s) / count, lSlope = (leftNext - l) / count, rSlope = (rightNext - r) / count;
            for (var i = 0; i < frames.Length; i += 2)
            {
                var mid = (frames[i] + frames[i + 1]) * 0.5f;
                var side = (frames[i] - frames[i + 1]) * 0.5f * s;
                frames[i] = (mid + side) * l;
                frames[i + 1] = (mid - side) * r;
                s += sSlope;
                l += lSlope;
                r += rSlope;
            }
        }
        _side = sideNext;
        _left = leftNext;
        _right = rightNext;

        // 4. Reverb (before the limiter, so the room added on top can't push the sound past full scale).
        if (_reverb.IsActive) _reverb.Process(frames);

        // 5. Limiter.
        _limiter.Process(frames);
        Scope.Write(frames);

        // 6. Volume.
        var volumeNext = Approach(_volume, Volume);
        if (_volume != 1 || volumeNext != 1)
        {
            var v = _volume;
            var slope = (volumeNext - v) / (frames.Length / 2);
            for (var i = 0; i < frames.Length; i += 2)
            {
                frames[i] *= v;
                frames[i + 1] *= v;
                v += slope;
            }
        }
        _volume = volumeNext;
    }

    /// <summary>Runs one filter over a step during which its level moves: the coefficients slide
    /// from the old tuning to the new one frame by frame (re-tuning in one go at each step would
    /// be heard as a faint buzz - "zipper noise" - while a slider moves).</summary>
    private void Glide(int f, Span<float> frames)
    {
        var from = _filters[f];
        Tune(f);
        var to = _filters[f];
        var count = frames.Length / 2;
        double b0 = from.B0, b1 = from.B1, b2 = from.B2, a1 = from.A1, a2 = from.A2;
        double db0 = (to.B0 - b0) / count, db1 = (to.B1 - b1) / count, db2 = (to.B2 - b2) / count,
               da1 = (to.A1 - a1) / count, da2 = (to.A2 - a2) / count;
        var m = f * 4;
        double l1 = _memory[m], l2 = _memory[m + 1], r1 = _memory[m + 2], r2 = _memory[m + 3];
        for (var i = 0; i < frames.Length; i += 2)
        {
            b0 += db0; b1 += db1; b2 += db2; a1 += da1; a2 += da2;

            double x = frames[i];
            var y = b0 * x + l1;
            l1 = b1 * x - a1 * y + l2;
            l2 = b2 * x - a2 * y;
            frames[i] = (float)y;

            x = frames[i + 1];
            y = b0 * x + r1;
            r1 = b1 * x - a1 * y + r2;
            r2 = b2 * x - a2 * y;
            frames[i + 1] = (float)y;
        }
        if (_filterOn[f])
        {
            _memory[m] = l1; _memory[m + 1] = l2; _memory[m + 2] = r1; _memory[m + 3] = r2;
        }
    }

    /// <summary>One step of a gain toward its target; lands on it exactly once close.</summary>
    private float Approach(float value, float target)
    {
        if (value == target) return target;
        var next = value + (target - value) * _gainStep;
        return Math.Abs(target - next) < 1e-4f ? target : next;
    }

    /// <summary>Recomputes one filter for its current level. At 0 dB it's switched off, with its
    /// memory cleared - it was passing the signal through unchanged at that point, so nothing jumps.</summary>
    private void Tune(int index)
    {
        _filters[index] = ToneCurve.Filter(index, _levelDb[index], _sampleRate);
        _filterOn[index] = _filters[index] != BiquadCoefficients.Identity;
        if (!_filterOn[index]) Array.Clear(_memory, index * 4, 4);
    }
}
