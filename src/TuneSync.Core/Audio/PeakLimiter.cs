namespace TuneSync.Core.Audio;

/// <summary>Keeps the sound under full scale after the equalizer has boosted it. It looks a few
/// milliseconds ahead (the sound comes out that much later), so the level is already down, in a
/// smooth ramp, when a peak arrives - no clipping and no click. Both channels are turned down
/// together, so the stereo picture doesn't shift. Below the ceiling it changes nothing: what
/// comes out is exactly what went in.</summary>
public sealed class PeakLimiter
{
    /// <summary>Just under full scale (-0.13 dB).</summary>
    public const float Ceiling = 0.985f;

    private readonly int _lookahead;
    private readonly float[] _delay;
    private int _delayAt;

    // The lowest wanted gain within the lookahead window: a queue whose values only rise from
    // front to back, so the front is always the window's minimum.
    private readonly float[] _queueGain;
    private readonly long[] _queueIndex;
    private int _queueHead, _queueCount;

    // The average of those minimums over the same length: turns a step down into a ramp that
    // has arrived by the time the peak leaves the delay.
    private readonly float[] _ramp;
    private int _rampAt;
    private double _rampSum;
    private int _rampBelowOne;

    private double _wanted = 1;
    private readonly double _release;
    private long _index;
    private float _lowestGain = 1;

    public PeakLimiter(int sampleRate, double lookaheadSeconds = 0.005, double releaseSeconds = 0.25)
    {
        _lookahead = Math.Max(8, (int)(sampleRate * lookaheadSeconds));
        _delay = new float[_lookahead * 2];
        _queueGain = new float[_lookahead + 2];
        _queueIndex = new long[_lookahead + 2];
        _ramp = new float[_lookahead];
        Array.Fill(_ramp, 1f);
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

    /// <summary>Stereo frames, left/right interleaved, processed in place.</summary>
    public void Process(Span<float> frames)
    {
        var capacity = _queueGain.Length;
        for (var i = 0; i + 1 < frames.Length; i += 2)
        {
            var left = frames[i];
            var right = frames[i + 1];
            var peak = Math.Max(Math.Abs(left), Math.Abs(right));
            if (!(peak <= float.MaxValue)) { left = right = peak = 0; } // NaN or infinity never reaches the speakers

            // The gain this frame needs; after a peak it recovers slowly rather than jumping back.
            var needed = peak > Ceiling && IsOn ? Ceiling / (double)peak : 1.0;
            _wanted += (1 - _wanted) * _release;
            if (_wanted > 0.999999) _wanted = 1;
            if (needed < _wanted) _wanted = needed;
            var wanted = (float)_wanted;
            if (wanted > _wanted) wanted = MathF.BitDecrement(wanted); // rounding must never raise it

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
            float gain;
            if (_rampBelowOne == 0)
            {
                gain = 1;
                _rampSum = _lookahead; // also drops whatever rounding the sum had gathered
            }
            else
            {
                _rampSum += minimum - leaving;
                gain = (float)(_rampSum / _lookahead);
                if (gain > 1) gain = 1;
                if (gain < _lowestGain) _lowestGain = gain;
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
