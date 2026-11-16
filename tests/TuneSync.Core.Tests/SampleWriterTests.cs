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
