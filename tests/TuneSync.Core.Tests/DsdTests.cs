using TuneSync.Core.Audio;
using TuneSync.Core.Audio.Dsd;
using Xunit.Abstractions;

namespace TuneSync.Core.Tests;

/// <summary>DSD: a tone modulated to DSD and decimated back to PCM is the same tone, clean
/// across the audio band; the modulator's noise shaping is what it is meant to be.</summary>
public class DsdTests(ITestOutputHelper log)
{
    internal const int Dsd64 = 2_822_400;

    /// <summary>DSD bytes (one channel) for a sine of PCM level <paramref name="level"/>.</summary>
    internal static byte[] Modulate(double hz, double level, int seconds10ths, int dsdRate = Dsd64, int oversampling = 64)
    {
        var modulator = new DsdModulator(oversampling);
        var bits = dsdRate / 10 * seconds10ths;
        var input = new double[bits];
        for (var i = 0; i < bits; i++) input[i] = level * DsdModulator.PcmFullScale * Math.Sin(2 * Math.PI * hz * i / dsdRate);
        var bytes = new byte[bits / 8];
        modulator.Modulate(input, bytes);
        Assert.Equal(0, modulator.Resets);
        return bytes;
    }

    /// <summary>What's left after taking out the best-fitting sine, below 20 kHz, in dB under the
    /// sine, and the sine's level in dBFS.</summary>
    private static (double ResidualDb, double LevelDb) Measure(double[] pcm, int rate, double hz)
    {
        // band-limit to 20 kHz first (the DSD noise above the band isn't the question here)
        var stereo = new double[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++) stereo[2 * i] = stereo[2 * i + 1] = pcm[i];
        var resampler = new PolyphaseResampler(rate, 44100);
        var output = new double[stereo.Length];
        var made = resampler.Process(stereo, output);
        var n = made;
        int from = n / 4, to = n * 3 / 4;
        double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0;
        for (var i = from; i < to; i++)
        {
            double s = Math.Sin(2 * Math.PI * hz * i / 44100), c = Math.Cos(2 * Math.PI * hz * i / 44100), y = output[2 * i];
            ss += s * s; cc += c * c; sc += s * c; ys += y * s; yc += y * c;
        }
        var det = ss * cc - sc * sc;
        double a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det;
        double residual = 0;
        for (var i = from; i < to; i++)
        {
            var e = output[2 * i] - a * Math.Sin(2 * Math.PI * hz * i / 44100) - b * Math.Cos(2 * Math.PI * hz * i / 44100);
            residual += e * e;
        }
        var amplitude = Math.Sqrt(a * a + b * b);
        return (10 * Math.Log10(residual / (to - from) / (amplitude * amplitude / 2)), 20 * Math.Log10(amplitude));
    }

    [Theory]
    [InlineData(997, 0.5)]
    [InlineData(997, 0.01)]
    [InlineData(10007, 0.5)]
    public void Dsd64_RoundTrip_IsClean(double hz, double level)
    {
        var bytes = Modulate(hz, level, 5);
        var decimator = new DsdDecimator(Dsd64, 176400);
        var pcm = new double[bytes.Length];
        var count = decimator.Process(bytes, pcm);
        var (residual, levelDb) = Measure(pcm[..count], 176400, hz);
        log.WriteLine($"{hz} Hz at {20 * Math.Log10(level):0} dBFS: came back at {levelDb:0.00} dBFS, residual {residual:0.0} dB (= {residual + 20 * Math.Log10(level):0.0} dBFS)");
        Assert.InRange(levelDb - 20 * Math.Log10(level), -0.05, 0.05);
        // what's left below 20 kHz, against full scale: the modulator's in-band noise and the
        // filters' leftovers
        Assert.True(residual + 20 * Math.Log10(level) < -110, $"{residual:0.0} dB");
    }

    [Fact]
    public void Dsd128_RoundTrip_IsClean()
    {
        const int rate = Dsd64 * 2;
        var bytes = Modulate(997, 0.5, 3, rate, oversampling: 128);
        var decimator = new DsdDecimator(rate, 176400);
        var pcm = new double[bytes.Length];
        var count = decimator.Process(bytes, pcm);
        var (residual, levelDb) = Measure(pcm[..count], 176400, 997);
        log.WriteLine($"DSD128: level {levelDb:0.00} dBFS, residual {residual:0.0} dB");
        Assert.InRange(levelDb, 20 * Math.Log10(0.5) - 0.05, 20 * Math.Log10(0.5) + 0.05);
        Assert.True(residual < -120, $"{residual:0.0} dB");
    }

    [Fact]
    public void NoiseTransfer_StaysWithinItsBound()
    {
        var (b, a) = DsdModulator.NoiseTransfer(64);
        double peak = 0, inBand = 0;
        for (var i = 0; i <= 4000; i++)
        {
            var w = Math.PI * i / 4000;
            System.Numerics.Complex n = 0, d = 0;
            for (var k = 0; k < b.Length; k++)
            {
                var z = System.Numerics.Complex.FromPolarCoordinates(1, -w * k);
                n += b[k] * z;
                d += a[k] * z;
            }
            var gain = (n / d).Magnitude;
            peak = Math.Max(peak, gain);
            if (w <= Math.PI / 64) inBand = Math.Max(inBand, gain);
        }
        log.WriteLine($"NTF peak {peak:0.000}, worst in band {20 * Math.Log10(inBand):0.0} dB");
        Assert.InRange(peak, 1.45, 1.55);
        Assert.True(20 * Math.Log10(inBand) < -60);
    }
}
