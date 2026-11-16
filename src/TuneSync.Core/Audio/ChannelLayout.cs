using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>Spreads the stereo sound over the speakers of a layout, the way a hi-fi receiver's
/// "multi-channel stereo" does: the fronts get left and right as they are; the center gets what
/// the two have in common (turned down, so a voice isn't louder than in stereo); the subwoofer
/// gets the bass of both (below about 100 Hz); the surrounds get left and right a little
/// quieter. Every speaker stays within full scale.</summary>
public sealed class ChannelLayout
{
    private const float CenterLevel = 0.5f;      // -6 dB of what left and right share
    private const float SurroundLevel = 0.7f;    // -3 dB
    private const double SubCutoffHz = 100;

    private readonly SpeakerLayout _layout;
    private readonly double _a;
    private double _low1, _low2;

    public ChannelLayout(SpeakerLayout layout, int sampleRate)
    {
        _layout = layout;
        // two one-pole low-passes in a row: a gentle 12 dB/octave crossover for the subwoofer
        _a = 1 - Math.Exp(-2 * Math.PI * SubCutoffHz / sampleRate);
    }

    public int Count => Channels(_layout);

    public static int Channels(SpeakerLayout layout) => layout switch
    {
        SpeakerLayout.Quad => 4,
        SpeakerLayout.Surround51 => 6,
        SpeakerLayout.Surround71 => 8,
        _ => 2,
    };

    /// <summary>Windows' speaker positions for each count (KSAUDIO_SPEAKER_*): stereo, quad
    /// (fronts and backs), 5.1 (FL FR FC LFE BL BR), 7.1 (... and the sides).</summary>
    public static int Mask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        4 => 0x33,
        6 => 0x3F,
        8 => 0x63F,
        _ => (1 << channels) - 1,
    };

    /// <summary>Stereo frames (left/right interleaved) to this layout's frames, in its channel
    /// order (FL FR [FC LFE] BL BR [SL SR]).</summary>
    public void Expand(ReadOnlySpan<float> stereo, Span<float> output)
    {
        var frames = stereo.Length / 2;
        var count = Count;
        if (count == 2)
        {
            stereo.CopyTo(output);
            return;
        }
        for (var i = 0; i < frames; i++)
        {
            float left = stereo[2 * i], right = stereo[2 * i + 1];
            var o = output.Slice(i * count, count);
            o[0] = left;
            o[1] = right;
            var surroundLeft = left * SurroundLevel;
            var surroundRight = right * SurroundLevel;
            if (count == 4)
            {
                o[2] = surroundLeft;
                o[3] = surroundRight;
                continue;
            }
            var mid = (left + right) * 0.5;
            _low1 += (mid - _low1) * _a;
            _low2 += (_low1 - _low2) * _a;
            o[2] = (float)(mid * CenterLevel);
            o[3] = (float)_low2; // the receiver adds the subwoofer's own +10 dB
            o[4] = surroundLeft;
            o[5] = surroundRight;
            if (count == 8)
            {
                o[6] = surroundLeft;
                o[7] = surroundRight;
            }
        }
        if (Math.Abs(_low1) < 1e-25) _low1 = 0;
        if (Math.Abs(_low2) < 1e-25) _low2 = 0;
    }
}

/// <summary>Turns float samples (-1..1) into what an exclusive-mode device takes: 16-, 24- or
/// 32-bit integers, or float as it is. Going down to 16 bits adds a little triangular noise
/// (dither), so the quietest sounds fade into hiss instead of breaking up.</summary>
public sealed class SampleWriter
{
    private readonly int _bits, _container;
    private readonly bool _float;
    private uint _seed = 0x9E3779B9;

    /// <param name="container">Bytes-wide slot of each sample, in bits (32 for 24-bit sound in
    /// four bytes, the sound in the top three); 0 = the same as <paramref name="bits"/>.</param>
    public SampleWriter(int bits, bool isFloat, int container = 0)
    {
        _bits = bits;
        _float = isFloat;
        _container = container > 0 ? container : bits;
    }

    public int BytesPerSample => _container / 8;

    public void Write(ReadOnlySpan<float> samples, Span<byte> target)
    {
        if (_float)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(target);
            return;
        }
        if (_bits == 24 && _container == 32)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var v = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607.0) << 8;
                target[4 * i] = (byte)v;
                target[4 * i + 1] = (byte)(v >> 8);
                target[4 * i + 2] = (byte)(v >> 16);
                target[4 * i + 3] = (byte)(v >> 24);
            }
            return;
        }
        switch (_bits)
        {
            case 16:
                for (var i = 0; i < samples.Length; i++)
                {
                    var dither = (Random() - Random()) * (1f / 32768f);
                    var v = (int)MathF.Round(Math.Clamp(samples[i] + dither, -1f, 1f) * 32767f);
                    target[2 * i] = (byte)v;
                    target[2 * i + 1] = (byte)(v >> 8);
                }
                break;
            case 24:
                for (var i = 0; i < samples.Length; i++)
                {
                    var v = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 8388607.0);
                    target[3 * i] = (byte)v;
                    target[3 * i + 1] = (byte)(v >> 8);
                    target[3 * i + 2] = (byte)(v >> 16);
                }
                break;
            default:
                for (var i = 0; i < samples.Length; i++)
                {
                    var v = (int)Math.Round(Math.Clamp((double)samples[i], -1.0, 1.0) * 2147483647.0);
                    target[4 * i] = (byte)v;
                    target[4 * i + 1] = (byte)(v >> 8);
                    target[4 * i + 2] = (byte)(v >> 16);
                    target[4 * i + 3] = (byte)(v >> 24);
                }
                break;
        }
    }

    /// <summary>0..1, cheap (xorshift).</summary>
    private float Random()
    {
        _seed ^= _seed << 13;
        _seed ^= _seed >> 17;
        _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / 16777216f;
    }
}
