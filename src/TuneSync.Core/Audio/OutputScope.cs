namespace TuneSync.Core.Audio;

/// <summary>The last second of what the player sends to the speakers (after the equalizer and
/// the limiter, before the volume), for the live spectrum and level meters. The audio thread
/// writes; any other thread may read the stretch being heard right now. Nothing is locked: a
/// read that races a write can only show a few samples a moment newer or older, which a
/// picture of the sound doesn't mind.</summary>
public sealed class OutputScope
{
    private readonly float[] _ring;   // stereo frames, left/right interleaved
    private readonly int _mask;       // frames in the ring, less one (a power of two)
    private long _written;            // frames written in all
    private int _delayFrames;

    public OutputScope(int sampleRate)
    {
        SampleRate = sampleRate;
        var capacity = 1;
        while (capacity < Math.Max(sampleRate, 8192)) capacity <<= 1;
        _ring = new float[capacity * 2];
        _mask = capacity - 1;
    }

    public int SampleRate { get; }

    /// <summary>How far behind the newest written frame the speakers are (the output device's
    /// buffer). Set by the audio thread as it writes.</summary>
    public int DelayFrames
    {
        get => Volatile.Read(ref _delayFrames);
        set => Volatile.Write(ref _delayFrames, Math.Clamp(value, 0, (_mask + 1) / 2));
    }

    /// <summary>Frames that have been heard so far (stops moving while nothing plays).</summary>
    public long HeardFrames => Math.Max(0, Volatile.Read(ref _written) - DelayFrames);

    internal void Write(ReadOnlySpan<float> frames)
    {
        var written = _written;
        var count = frames.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var at = (int)((written + i) & _mask) * 2;
            _ring[at] = frames[i * 2];
            _ring[at + 1] = frames[i * 2 + 1];
        }
        Volatile.Write(ref _written, written + count);
    }

    /// <summary>Copies the frames heard most recently into <paramref name="stereo"/> (left/right
    /// interleaved, oldest first; silence before the song's start). Returns <see cref="HeardFrames"/>.</summary>
    public long Read(Span<float> stereo)
    {
        var end = HeardFrames;
        var count = Math.Min(stereo.Length / 2, (_mask + 1) / 2);
        var start = end - count;
        for (var i = 0; i < count; i++)
        {
            var frame = start + i;
            if (frame < 0)
            {
                stereo[i * 2] = stereo[i * 2 + 1] = 0;
                continue;
            }
            var at = (int)(frame & _mask) * 2;
            stereo[i * 2] = _ring[at];
            stereo[i * 2 + 1] = _ring[at + 1];
        }
        return end;
    }
}
