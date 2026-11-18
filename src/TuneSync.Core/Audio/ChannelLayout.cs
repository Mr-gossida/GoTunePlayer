using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>Spreads the stereo sound over the speakers of a layout, the way a hi-fi receiver's
/// "multi-channel stereo" does: the fronts get left and right as they are; the center gets what
/// the two have in common (turned down, so a voice isn't louder than in stereo); the subwoofer
/// gets the bass of both (below the crossover, 100 Hz unless set otherwise); the surrounds get
/// left and right a little quieter. Every speaker stays within full scale.</summary>
public sealed class ChannelLayout
{
    private const float CenterLevel = 0.5f;      // -6 dB of what left and right share
    private const float SurroundLevel = 0.7f;    // -3 dB

    private readonly SpeakerLayout _layout;
    private readonly double _a;
    private double _low1, _low2;

    /// <param name="subCrossoverHz">Where the subwoofer's bass ends: the sound there is down
    /// 6 dB, as at a receiver's (Linkwitz-Riley) crossover point, and falls 12 dB per octave
    /// above it.</param>
    public ChannelLayout(SpeakerLayout layout, int sampleRate, double subCrossoverHz = OutputSettings.DefaultSubCrossoverHz)
    {
        _layout = layout;
        // two one-pole low-passes in a row (each -3 dB at the crossover: -6 dB together)
        var cutoff = Math.Clamp(subCrossoverHz, OutputSettings.MinSubCrossoverHz, OutputSettings.MaxSubCrossoverHz);
        _a = 1 - Math.Exp(-2 * Math.PI * cutoff / sampleRate);
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
/// 32-bit integers, or float as it is. The scale is the decoder's own (2^15, 2^23, 2^31), so a
/// sample decoded from an integer comes back as exactly that integer - nothing is lost on the way
/// to a bit-perfect output. Processed sound going down to 16 bits gets a little triangular noise
/// (dither), so the quietest sounds fade into hiss instead of breaking up; bit-perfect sound
/// never does.</summary>
public sealed class SampleWriter
{
    private readonly int _bits, _container;
    private readonly bool _float, _dither;
    private uint _seed = 0x9E3779B9;

    /// <param name="container">Bytes-wide slot of each sample, in bits (32 for 24-bit sound in
    /// four bytes, the sound in the top three); 0 = the same as <paramref name="bits"/>.</param>
    public SampleWriter(int bits, bool isFloat, int container = 0, bool dither = true)
    {
        _bits = bits;
        _float = isFloat;
        _container = container > 0 ? container : bits;
        _dither = dither;
    }

    private static int ToInt(double sample, double scale, int max) =>
        (int)Math.Clamp(Math.Round(sample * scale), -max - 1, max);

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
                var v = ToInt(samples[i], 8388608.0, 8388607) << 8;
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
                    var dither = _dither ? (Random() - Random()) * (1f / 32768f) : 0f;
                    var v = ToInt(samples[i] + dither, 32768.0, 32767);
                    target[2 * i] = (byte)v;
                    target[2 * i + 1] = (byte)(v >> 8);
                }
                break;
            case 24:
                for (var i = 0; i < samples.Length; i++)
                {
                    var v = ToInt(samples[i], 8388608.0, 8388607);
                    target[3 * i] = (byte)v;
                    target[3 * i + 1] = (byte)(v >> 8);
                    target[3 * i + 2] = (byte)(v >> 16);
                }
                break;
            default:
                for (var i = 0; i < samples.Length; i++)
                {
                    var v = (int)Math.Clamp(Math.Round(samples[i] * 2147483648.0), int.MinValue, int.MaxValue);
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
