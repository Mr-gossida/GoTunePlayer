namespace TuneSync.Core.Audio;

/// <summary>Keeps the sound under full scale after the equalizer has boosted it. It looks ahead
/// (the sound comes out that much later), so the level is already down, in a smooth ramp, when a
/// peak arrives - no clipping and no click. Both channels are turned down together, so the
/// stereo picture doesn't shift. Below the ceiling it changes nothing: what comes out is exactly
/// what went in.
/// It is tuned to be heard as a level that sits a little lower, not as a sound that is squeezed:
/// once down it holds a moment and then comes back up over seconds. A quick limiter (5 ms ahead,
/// back up in a quarter second) went up and down with every beat - with every cycle of a bass
/// note, even - and that wobble mixed itself into the music: measured on songs with the
/// presets, about -24 dB of distortion against about -37 to -40 dB now, at the same loudness
/// within a dB or two. That was the "smeared" sound of a boosted equalizer.</summary>
public sealed class PeakLimiter
{
    /// <summary>Just under full scale (-0.13 dB).</summary>
    public const float Ceiling = 0.985f;

    private readonly int _lookahead;
    private readonly double[] _delay;
    private int _delayAt;

    // The lowest wanted gain within the lookahead window: a queue whose values only rise from
    // front to back, so the front is always the window's minimum.
    private readonly double[] _queueGain;
    private readonly long[] _queueIndex;
    private int _queueHead, _queueCount;

    // The average of those minimums over the same length: turns a step down into a ramp that
    // has arrived by the time the peak leaves the delay.
    private readonly double[] _ramp;
    private int _rampAt;
    private double _rampSum;
    private int _rampBelowOne;

    private double _wanted = 1;
    private readonly double _release;
    private readonly int _hold;
    private int _holdLeft;
    private long _index;
    private float _lowestGain = 1;

    public PeakLimiter(int sampleRate, double lookaheadSeconds = 0.03, double releaseSeconds = 2, double holdSeconds = 0.12)
    {
        _hold = (int)(sampleRate * holdSeconds);
        _lookahead = Math.Max(8, (int)(sampleRate * lookaheadSeconds));
        _delay = new double[_lookahead * 2];
        _queueGain = new double[_lookahead + 2];
        _queueIndex = new long[_lookahead + 2];
        _ramp = new double[_lookahead];
        Array.Fill(_ramp, 1.0);
        _rampSum = _lookahead;
        _release = 1 - Math.Exp(-1.0 / (sampleRate * releaseSeconds));
    }

    /// <summary>Off, nothing is turned down (the sound still comes out the same few milliseconds
    /// later, so switching it while music plays doesn't skip); a reduction in progress fades out.</summary>
    public bool IsOn { get; set; } = true;

    /// <summary>How many frames later the sound comes out.</summary>
    public int LatencyFrames => _lookahead;

    /// <summary>The lowest gain applied since this was last read (1 = nothing turned down).</summary>
    public float TakeLowestGain()
    {
        var gain = _lowestGain;
        _lowestGain = 1;
        return gain;
    }

    /// <summary>The same for float frames (tests and tools; the player runs the 64-bit one).</summary>
    public void Process(Span<float> frames) => Wide.Run(frames, this, static (limiter, wide) => limiter.Process(wide));

    /// <summary>Stereo frames, left/right interleaved, processed in place (64-bit).</summary>
    public void Process(Span<double> frames)
    {
        var capacity = _queueGain.Length;
        for (var i = 0; i + 1 < frames.Length; i += 2)
        {
            var left = frames[i];
            var right = frames[i + 1];
            var peak = Math.Max(Math.Abs(left), Math.Abs(right));
            if (!(peak <= double.MaxValue)) { left = right = peak = 0; } // NaN or infinity never reaches the speakers

            // The gain this frame needs; after a peak it holds a moment (so the next beat or the
            // next cycle of a bass note finds it still down) and then recovers slowly.
            var needed = peak > Ceiling && IsOn ? Ceiling / peak : 1.0;
            if (needed <= _wanted)
            {
                _wanted = needed;
                _holdLeft = _hold;
            }
            else if (_holdLeft > 0)
            {
                _holdLeft--;
            }
            else
            {
                _wanted += (1 - _wanted) * _release;
                if (_wanted > 0.999999) _wanted = 1;
                if (needed < _wanted) _wanted = needed;
            }
            var wanted = _wanted;

            // Window minimum.
            while (_queueCount > 0 && _queueGain[(_queueHead + _queueCount - 1) % capacity] >= wanted) _queueCount--;
            var tail = (_queueHead + _queueCount) % capacity;
            _queueGain[tail] = wanted;
            _queueIndex[tail] = _index;
            _queueCount++;
            if (_queueIndex[_queueHead] < _index - _lookahead)
            {
                _queueHead = (_queueHead + 1) % capacity;
                _queueCount--;
            }
            var minimum = _queueGain[_queueHead];

            // Ramp.
            var leaving = _ramp[_rampAt];
            _ramp[_rampAt] = minimum;
            _rampAt = _rampAt + 1 == _lookahead ? 0 : _rampAt + 1;
            if (leaving < 1) _rampBelowOne--;
            if (minimum < 1) _rampBelowOne++;
            double gain;
            if (_rampBelowOne == 0)
            {
                gain = 1;
                _rampSum = _lookahead; // also drops whatever rounding the sum had gathered
            }
            else
            {
                _rampSum += minimum - leaving;
                gain = _rampSum / _lookahead;
                if (gain > 1) gain = 1;
                if (gain < _lowestGain) _lowestGain = (float)gain;
            }

            frames[i] = _delay[_delayAt] * gain;
            frames[i + 1] = _delay[_delayAt + 1] * gain;
            _delay[_delayAt] = left;
            _delay[_delayAt + 1] = right;
            _delayAt = _delayAt + 2 == _delay.Length ? 0 : _delayAt + 2;
            _index++;
        }
    }
}
