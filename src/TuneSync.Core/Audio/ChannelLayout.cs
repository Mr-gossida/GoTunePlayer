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

    /// <summary>The same for float frames (tests and tools).</summary>
    public void Expand(ReadOnlySpan<float> stereo, Span<float> output)
    {
        var wide = new double[stereo.Length];
        for (var i = 0; i < stereo.Length; i++) wide[i] = stereo[i];
        var result = new double[output.Length];
        Expand(wide, result);
        for (var i = 0; i < output.Length; i++) output[i] = (float)result[i];
    }

    /// <summary>Stereo frames (left/right interleaved) to this layout's frames, in its channel
    /// order (FL FR [FC LFE] BL BR [SL SR]). 64-bit.</summary>
    public void Expand(ReadOnlySpan<double> stereo, Span<double> output)
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
            double left = stereo[2 * i], right = stereo[2 * i + 1];
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
            o[2] = mid * CenterLevel;
            o[3] = _low2; // the receiver adds the subwoofer's own +10 dB
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

/// <summary>Turns the chain's 64-bit samples (-1..1) into what the device takes: 16-, 24- or
/// 32-bit integers, or 32-bit float. The scale is the decoder's own (2^15, 2^23, 2^31), so a
/// sample decoded from an integer comes back as exactly that integer - nothing is lost on the way
/// to a bit-perfect output. Processed sound gets a little triangular noise (dither) at its last
/// bit when it goes to 16 or 24 bits, so the quietest sounds fade into hiss instead of breaking
/// up; at 16 bits and a high sample rate that noise is also shaped - moved up above hearing
/// (second order, null at DC) - which leaves the audible band some 10 dB quieter. Bit-perfect
/// sound is never dithered.</summary>
public sealed class SampleWriter
{
    private readonly int _bits, _container, _channels;
    private readonly bool _float, _dither, _shape;
    private readonly double[] _error1, _error2;
    private uint _seed = 0x9E3779B9;

    /// <param name="container">Bytes-wide slot of each sample, in bits (32 for 24-bit sound in
    /// four bytes, the sound in the top three); 0 = the same as <paramref name="bits"/>.</param>
    /// <param name="noiseShaping">Shape the 16-bit dither's noise up out of the audible band
    /// (worth it at 88.2 kHz and above, where there is room up there to put it).</param>
    /// <param name="channels">Interleaved channels (the noise shaper remembers each one's error).</param>
    public SampleWriter(int bits, bool isFloat, int container = 0, bool dither = true, bool noiseShaping = false, int channels = 2)
    {
        _bits = bits;
        _float = isFloat;
        _container = container > 0 ? container : bits;
        _dither = dither;
        _shape = dither && noiseShaping && bits == 16 && !isFloat;
        _channels = Math.Max(1, channels);
        _error1 = new double[_channels];
        _error2 = new double[_channels];
    }

    /// <summary>Whether 16-bit noise shaping is in use.</summary>
    public bool NoiseShaping => _shape;

    private static int ToInt(double sample, double scale, int max) =>
        (int)Math.Clamp(Math.Round(sample * scale), -max - 1, max);

    public int BytesPerSample => _container / 8;

    /// <summary>The same for float samples (tests and tools).</summary>
    public void Write(ReadOnlySpan<float> samples, Span<byte> target)
    {
        var wide = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++) wide[i] = samples[i];
        Write(wide, target);
    }

    public void Write(ReadOnlySpan<double> samples, Span<byte> target)
    {
        if (_float)
        {
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(target);
            for (var i = 0; i < samples.Length; i++) floats[i] = (float)samples[i];
            return;
        }
        if (_bits == 24 && _container == 32)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var dither = _dither ? Tpdf() * (1.0 / 8388608) : 0;
                var v = ToInt(samples[i] + dither, 8388608.0, 8388607) << 8;
                target[4 * i] = (byte)v;
                target[4 * i + 1] = (byte)(v >> 8);
                target[4 * i + 2] = (byte)(v >> 16);
                target[4 * i + 3] = (byte)(v >> 24);
            }
            return;
        }
        switch (_bits)
        {
            case 16 when _shape:
                for (var i = 0; i < samples.Length; i++)
                {
                    // error feedback: what the last two roundings got wrong is taken off, so the
                    // total noise is (1 - z^-1)^2 times the rounding noise - nothing at the
                    // bottom, all of it pushed toward the top of the (high) sample rate
                    var c = i % _channels;
                    var wanted = samples[i] - (2 * _error1[c] - _error2[c]);
                    var v = ToInt(wanted + Tpdf() / 32768.0, 32768.0, 32767);
                    var error = Math.Clamp(v / 32768.0 - wanted, -4 / 32768.0, 4 / 32768.0);
                    _error2[c] = _error1[c];
                    _error1[c] = error;
                    target[2 * i] = (byte)v;
                    target[2 * i + 1] = (byte)(v >> 8);
                }
                break;
            case 16:
                for (var i = 0; i < samples.Length; i++)
                {
                    var dither = _dither ? Tpdf() / 32768.0 : 0;
                    var v = ToInt(samples[i] + dither, 32768.0, 32767);
                    target[2 * i] = (byte)v;
                    target[2 * i + 1] = (byte)(v >> 8);
                }
                break;
            case 24:
                for (var i = 0; i < samples.Length; i++)
                {
                    var dither = _dither ? Tpdf() * (1.0 / 8388608) : 0;
                    var v = ToInt(samples[i] + dither, 8388608.0, 8388607);
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

    /// <summary>Triangular noise of one step either way (the sum of two uniform ones).</summary>
    private double Tpdf() => Random() - (double)Random();

    /// <summary>0..1, cheap (xorshift).</summary>
    private float Random()
    {
        _seed ^= _seed << 13;
        _seed ^= _seed >> 17;
        _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / 16777216f;
    }
}
