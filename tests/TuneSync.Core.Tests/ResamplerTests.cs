using TuneSync.Core.Audio;
using TuneSync.Core.Models;
using Xunit.Abstractions;

namespace TuneSync.Core.Tests;

/// <summary>The polyphase converter: a pure tone comes out as the same pure tone (level and
/// frequency), nothing outside the original band survives, streaming in odd-sized blocks gives
/// the same samples as one go, and the minimum-phase filter rings only after a transient.</summary>
public class ResamplerTests(ITestOutputHelper log)
{
    private static double[] Tone(int rate, double hz, int frames, double level = 0.5, double rightHz = 0)
    {
        var data = new double[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            data[2 * i] = level * Math.Sin(2 * Math.PI * hz * i / rate);
            data[2 * i + 1] = level * Math.Sin(2 * Math.PI * (rightHz > 0 ? rightHz : hz) * i / rate);
        }
        return data;
    }

    /// <summary>Runs the whole input through, the way the player does: asks how much input a
    /// block of output needs, gives that.</summary>
    private static double[] Run(IResampler resampler, double[] input, int outBlock = 1024)
    {
        var output = new List<double>();
        var buffer = new double[outBlock * 2];
        var at = 0;
        var frames = input.Length / 2;
        while (at < frames)
        {
            var need = Math.Min(resampler.InputFor(outBlock), frames - at);
            var made = resampler.Process(input.AsSpan(at * 2, need * 2), buffer);
            for (var i = 0; i < made * 2; i++) output.Add(buffer[i]);
            at += need;
            if (need == 0 && made == 0) break;
        }
        return [.. output];
    }

    /// <summary>The residual after taking out the best-fitting sine at <paramref name="hz"/>
    /// (over the middle of the left channel), relative to the sine, in dB.</summary>
    private static double ResidualDb(double[] stereo, int rate, double hz, double level)
    {
        var frames = stereo.Length / 2;
        int from = frames / 4, to = frames * 3 / 4;
        double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0;
        for (var i = from; i < to; i++)
        {
            double s = Math.Sin(2 * Math.PI * hz * i / rate), c = Math.Cos(2 * Math.PI * hz * i / rate), y = stereo[2 * i];
            ss += s * s; cc += c * c; sc += s * c; ys += y * s; yc += y * c;
        }
        var det = ss * cc - sc * sc;
        var a = (ys * cc - yc * sc) / det;
        var b = (yc * ss - ys * sc) / det;
        double residual = 0;
        for (var i = from; i < to; i++)
        {
            var e = stereo[2 * i] - a * Math.Sin(2 * Math.PI * hz * i / rate) - b * Math.Cos(2 * Math.PI * hz * i / rate);
            residual += e * e;
        }
        var fitted = Math.Sqrt(a * a + b * b);
        Assert.InRange(20 * Math.Log10(fitted / level), -0.001, 0.001); // the tone keeps its level
        return 10 * Math.Log10(residual / (to - from) / (level * level / 2));
    }

    [Theory]
    [InlineData(44100, 88200, ResampleFilter.Sharp)]
    [InlineData(44100, 352800, ResampleFilter.Sharp)]
    [InlineData(44100, 384000, ResampleFilter.Sharp)]
    [InlineData(48000, 768000, ResampleFilter.Sharp)]
    [InlineData(44100, 705600, ResampleFilter.MinimumPhase)]
    [InlineData(44100, 384000, ResampleFilter.MinimumPhase)]
    [InlineData(44100, 352800, ResampleFilter.Soft)]
    [InlineData(96000, 44100, ResampleFilter.Sharp)]
    public void PureTone_ComesOutPure(int inRate, int outRate, ResampleFilter filter)
    {
        foreach (var hz in new[] { 997.0, 15011.0 })
        {
            if (filter == ResampleFilter.Soft && hz > 15000) continue; // gently rolled off up there by design
            var input = Tone(inRate, hz, inRate / 2);
            var output = Run(new PolyphaseResampler(inRate, outRate, filter), input);
            Assert.InRange(output.Length / 2, outRate / 2 * 0.99, outRate / 2 * 1.01);
            var db = ResidualDb(output, outRate, hz, 0.5);
            log.WriteLine($"{inRate}->{outRate} {filter} {hz} Hz: residual {db:0.0} dB");
            Assert.True(db < -120, $"{inRate}->{outRate} {filter} {hz} Hz: residual {db:0.0} dB");
        }
    }

    [Theory]
    [InlineData(44100, 352800)]
    [InlineData(44100, 384000)]
    public void Images_AreRemoved(int inRate, int outRate)
    {
        // 20 kHz at 44.1 kHz: a naive upsampler leaves its mirror at 24.1 kHz (and further up)
        var output = Run(new PolyphaseResampler(inRate, outRate), Tone(inRate, 20000, inRate / 2));
        var db = ResidualDb(output, outRate, 20000, 0.5);
        log.WriteLine($"{inRate}->{outRate} 20 kHz: residual {db:0.0} dB");
        Assert.True(db < -120, $"residual {db:0.0} dB");
    }

    [Fact]
    public void Downsampling_RemovesWhatCantFit()
    {
        // 30 kHz in 96 kHz sound has no place in 44.1 kHz: it must vanish, not fold back to 14.1 kHz
        var output = Run(new PolyphaseResampler(96000, 44100), Tone(96000, 30000, 48000));
        var energy = output.Skip(output.Length / 4).Take(output.Length / 2).Average(v => v * v);
        var db = 10 * Math.Log10(energy / (0.5 * 0.5 / 2));
        log.WriteLine($"30 kHz left after 96->44.1: {db:0.0} dB");
        Assert.True(db < -130, $"{db:0.0} dB");
    }

    [Fact]
    public void OddBlocks_GiveTheSameSamples()
    {
        var input = Tone(44100, 1234, 20000, 0.7, 4321);
        var whole = Run(new PolyphaseResampler(44100, 384000), input, 1 << 20);
        var pieces = Run(new PolyphaseResampler(44100, 384000), input, 333);
        var count = Math.Min(whole.Length, pieces.Length);
        Assert.True(count > 0);
        for (var i = 0; i < count; i++) Assert.Equal(whole[i], pieces[i]);
    }

    [Fact]
    public void AskedInput_MakesExactlyTheAskedOutput()
    {
        var resampler = new PolyphaseResampler(44100, 384000);
        var input = Tone(44100, 440, 50000);
        var at = 0;
        var buffer = new double[2048 * 2];
        foreach (var want in new[] { 512, 1, 2048, 777, 1500, 3 })
        {
            var need = resampler.InputFor(want);
            var made = resampler.Process(input.AsSpan(at * 2, need * 2), buffer.AsSpan(0, want * 2));
            Assert.Equal(want, made);
            at += need;
        }
    }

    [Fact]
    public void MinimumPhase_RingsOnlyAfter()
    {
        // a click: linear phase rings before and after it, minimum phase only after (counted from
        // three input samples ahead of the peak, past the main lobe's own rise; 8 outputs per input)
        static (double before, double peakAt) Ring(ResampleFilter filter)
        {
            var input = new double[4000 * 2];
            input[2000 * 2] = input[2000 * 2 + 1] = 1;
            var output = Run(new PolyphaseResampler(44100, 352800, filter), input);
            var peak = 0;
            for (var i = 0; i < output.Length / 2; i++) if (Math.Abs(output[2 * i]) > Math.Abs(output[2 * peak])) peak = i;
            double before = 0, total = 0;
            for (var i = 0; i < output.Length / 2; i++)
            {
                total += output[2 * i] * output[2 * i];
                if (i < peak - 24) before += output[2 * i] * output[2 * i];
            }
            return (10 * Math.Log10(before / total + 1e-30), peak);
        }
        var linear = Ring(ResampleFilter.Sharp);
        var minimum = Ring(ResampleFilter.MinimumPhase);
        log.WriteLine($"energy before the peak: linear {linear.before:0.0} dB, minimum {minimum.before:0.0} dB");
        Assert.True(linear.before > -30);
        Assert.True(minimum.before < -50, $"{minimum.before:0.0} dB");
    }
}
