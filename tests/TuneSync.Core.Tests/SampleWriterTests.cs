using TuneSync.Core.Audio;

namespace TuneSync.Core.Tests;

/// <summary>Bit-perfect output: a sample the decoder turned into a float (x / 2^15, x / 2^23)
/// must come back out as the very same integer.</summary>
public class SampleWriterTests
{
    [Fact]
    public void Every16BitSample_ComesBackExactly_In16And24Bits()
    {
        var samples = new float[65536];
        for (var i = 0; i < samples.Length; i++) samples[i] = (i - 32768) / 32768f; // as MfDecoder reads 16-bit PCM

        var out16 = new byte[samples.Length * 2];
        new SampleWriter(16, isFloat: false, dither: false).Write(samples, out16);
        for (var i = 0; i < samples.Length; i++)
            Assert.Equal(i - 32768, BitConverter.ToInt16(out16, i * 2));

        // in 24 bits packed in four bytes (most HDMI and sound cards): the same value, eight bits up
        var out24 = new byte[samples.Length * 4];
        new SampleWriter(24, isFloat: false, container: 32, dither: false).Write(samples, out24);
        for (var i = 0; i < samples.Length; i++)
            Assert.Equal((i - 32768) << 16, BitConverter.ToInt32(out24, i * 4));
    }

    [Fact]
    public void Every24BitSample_ComesBackExactly()
    {
        const int count = 1 << 24;
        var samples = new float[count];
        for (var i = 0; i < count; i++) samples[i] = (i - (1 << 23)) / 8388608f; // as MfDecoder reads 24-bit PCM

        var packed = new byte[count * 3];
        new SampleWriter(24, isFloat: false, dither: false).Write(samples, packed);
        for (var i = 0; i < count; i++)
        {
            var value = (packed[i * 3] | packed[i * 3 + 1] << 8 | packed[i * 3 + 2] << 16) << 8 >> 8;
            if (value != i - (1 << 23)) Assert.Fail($"sample {i - (1 << 23)} came back as {value}");
        }

        var wide = new byte[count * 4];
        new SampleWriter(24, isFloat: false, container: 32, dither: false).Write(samples, wide);
        for (var i = 0; i < count; i++)
            if (BitConverter.ToInt32(wide, i * 4) >> 8 != i - (1 << 23)) Assert.Fail($"sample {i - (1 << 23)} (in 32) changed");
    }

    [Fact]
    public void FullScale_IsClampedNotWrapped()
    {
        var out16 = new byte[4];
        new SampleWriter(16, isFloat: false, dither: false).Write([1f, -1f], out16);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(out16, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(out16, 2));
    }

    [Fact]
    public void Stereo_IsPassedThroughUntouched()
    {
        var stereo = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var output = new float[4];
        new ChannelLayout(Models.SpeakerLayout.Stereo, 44100).Expand(stereo, output);
        Assert.Equal(stereo, output);
    }
}

/// <summary>Dither at the output: 16-bit noise shaping moves the rounding noise out of the
/// audible band, and 64-bit samples convert as exactly as float ones did.</summary>
public class DitherTests
{
    /// <summary>Rounding noise of a quiet tone written at 16 bits at 176.4 kHz, below 20 kHz
    /// (through a windowed-sinc low-pass).</summary>
    private static double InBandNoise(bool shaped)
    {
        const int count = 1 << 16;
        var samples = new double[count * 2];
        for (var i = 0; i < count; i++)
            samples[2 * i] = samples[2 * i + 1] = 0.001 * Math.Sin(2 * Math.PI * 1000 * i / 176400.0);
        var bytes = new byte[samples.Length * 2];
        new SampleWriter(16, isFloat: false, dither: true, noiseShaping: shaped, channels: 2).Write(samples, bytes);
        var error = new double[count];
        for (var i = 0; i < count; i++) error[i] = BitConverter.ToInt16(bytes, i * 4) / 32768.0 - samples[2 * i];
        const int half = 200;
        var fc = 20000.0 / 176400;
        var taps = new double[2 * half + 1];
        for (var k = -half; k <= half; k++)
        {
            var sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            taps[k + half] = sinc * (0.42 + 0.5 * Math.Cos(Math.PI * k / half) + 0.08 * Math.Cos(2 * Math.PI * k / half));
        }
        double energy = 0;
        for (var n = half; n < count - half; n++)
        {
            double y = 0;
            for (var k = 0; k < taps.Length; k++) y += taps[k] * error[n - half + k];
            energy += y * y;
        }
        return energy;
    }

    [Fact]
    public void NoiseShaping_LeavesTheAudibleBandQuieter()
    {
        var plain = InBandNoise(shaped: false);
        var shaped = InBandNoise(shaped: true);
        Assert.True(10 * Math.Log10(shaped / plain) < -10, $"{10 * Math.Log10(shaped / plain):0.0} dB");
    }

    [Fact]
    public void WideSamples_ComeBackExactly()
    {
        var samples = new double[65536];
        for (var i = 0; i < samples.Length; i++) samples[i] = (i - 32768) / 32768.0;
        var out16 = new byte[samples.Length * 2];
        new SampleWriter(16, isFloat: false, dither: false).Write(samples, out16);
        for (var i = 0; i < samples.Length; i++) Assert.Equal(i - 32768, BitConverter.ToInt16(out16, i * 2));
    }
}
