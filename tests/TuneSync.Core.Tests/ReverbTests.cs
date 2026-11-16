using System.Diagnostics;
using TuneSync.Core.Audio;
using TuneSync.Core.Models;
using Xunit.Abstractions;

namespace TuneSync.Core.Tests;

public class ReverbTests(ITestOutputHelper output)
{
    private const int Rate = 48000;

    private static ToneSettings Settings(Action<ToneSettings>? change = null)
    {
        var s = new ToneSettings { Enabled = true };
        change?.Invoke(s);
        return s;
    }

    private static ToneSettings WithReverb(string room, double amount = 1, double decay = 1) =>
        Settings(s => { s.Reverb = true; s.ReverbRoom = room; s.ReverbAmount = amount; s.ReverbDecay = decay; });

    private static float[] Noise(int frames, double amplitude, int seed = 1)
    {
        var random = new Random(seed);
        var data = new float[frames * 2];
        for (var i = 0; i < data.Length; i++) data[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        return data;
    }

    private static void Run(ToneProcessor processor, float[] data, int block = 480)
    {
        for (var at = 0; at < data.Length; at += block * 2)
            processor.Process(data.AsSpan(at, Math.Min(block * 2, data.Length - at)));
    }

    private static double Rms(ReadOnlySpan<float> data)
    {
        double sum = 0;
        foreach (var v in data) sum += v * (double)v;
        return Math.Sqrt(sum / data.Length);
    }

    /// <summary>Just the reverb's own sound for an impulse (the dry click taken away).</summary>
    private static float[] ImpulseResponse(string room, double decay, int frames)
    {
        var reverb = new Reverb(Rate);
        var r = ReverbRooms.Find(room);
        reverb.SetTarget(r, 1, r.DecaySeconds * decay);
        reverb.Settle();
        var data = new float[frames * 2];
        data[0] = data[1] = 1;
        for (var at = 0; at < data.Length; at += 64) reverb.Process(data.AsSpan(at, Math.Min(64, data.Length - at)));
        data[0] -= 1;
        data[1] -= 1;
        return data;
    }

    [Fact]
    public void ReverbOff_LeavesTheSoundBitForBit_AndAfterItIsSwitchedOffAgain()
    {
        // Flat equalizer, quiet enough that the limiter has nothing to do: only the delay remains.
        var settings = Settings(s => { s.ReverbAmount = 0.8; s.ReverbRoom = "cathedral"; });
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(settings) };
        var input = Noise(Rate, 0.4);
        var data = (float[])input.Clone();
        Run(processor, data);
        var delay = processor.LatencyFrames * 2;
        for (var i = delay; i < data.Length; i++) Assert.Equal(input[i - delay], data[i]);

        // on for a while, then off: once it has faded (a few hundredths of a second) the sound is
        // again exactly what went in
        settings.Reverb = true;
        processor.Config = ToneConfig.From(settings);
        Run(processor, Noise(Rate, 0.4, 2));
        settings.Reverb = false;
        processor.Config = ToneConfig.From(settings);
        Run(processor, Noise(Rate / 2, 0.4, 3));
        input = Noise(Rate, 0.4, 4);
        data = (float[])input.Clone();
        Run(processor, data);
        for (var i = delay; i < data.Length; i++) Assert.Equal(input[i - delay], data[i]);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("studio")]
    [InlineData("hall")]
    [InlineData("cathedral")]
    public void TheTail_DiesAwayInTheRoomsDecayTime(string id)
    {
        var room = ReverbRooms.Find(id);
        var response = ImpulseResponse(id, 1, (int)(Rate * (room.DecaySeconds * 1.5 + 0.2)));
        // Schroeder's backward integration: the energy still to come at each moment.
        var frames = response.Length / 2;
        var remaining = new double[frames];
        double sum = 0;
        for (var i = frames - 1; i >= 0; i--)
        {
            sum += response[2 * i] * (double)response[2 * i] + response[2 * i + 1] * (double)response[2 * i + 1];
            remaining[i] = sum;
        }
        double At(double db) => Array.FindIndex(remaining, e => 10 * Math.Log10(e / remaining[0]) < db) / (double)Rate;
        // the slope from -5 to -35 dB, extended to 60 dB
        var t60 = (At(-35) - At(-5)) * 2;
        output.WriteLine($"{id}: T60 {t60:0.00} s (room says {room.DecaySeconds} s), first sound after {At(-0.01) * 1000:0} ms");
        Assert.InRange(t60, room.DecaySeconds * 0.7, room.DecaySeconds * 1.3);
    }

    [Fact]
    public void TheDecayControl_MakesTheTailLongerAndShorter()
    {
        double Energy(double decay)
        {
            var r = ImpulseResponse("hall", decay, Rate * 3);
            return Rms(r.AsSpan(Rate * 2 * 3 / 2)); // what is left after 1.5 s
        }
        Assert.True(Energy(2) > Energy(1) * 4);
        Assert.True(Energy(1) > Energy(0.5) * 4);
    }

    [Fact]
    public void TheTail_HasLessHighsThanLows_AsItGoesOn()
    {
        // sample-to-sample differences (the highs) against the level: smaller late in the tail
        // than early on, i.e. the room gets darker as it dies away, like a real one
        var r = ImpulseResponse("hall", 1, Rate * 2);
        double Brightness(int fromFrame, int frames)
        {
            var part = r.AsSpan(fromFrame * 2, frames * 2);
            double level = 0, change = 0;
            for (var i = 2; i < part.Length; i += 2)
            {
                level += part[i] * (double)part[i];
                change += (part[i] - part[i - 2]) * (double)(part[i] - part[i - 2]);
            }
            return change / level;
        }
        double early = Brightness(Rate / 20, Rate / 10), late = Brightness(Rate, Rate / 2);
        output.WriteLine($"brightness early {early:0.000}, late {late:0.000}");
        Assert.True(late < early * 0.5, $"early {early}, late {late}");
    }

    [Theory]
    [InlineData("room")]
    [InlineData("studio")]
    [InlineData("hall")]
    [InlineData("cathedral")]
    public void AtFullMix_TheRoomIsAboutAsLoudAsTheDrySound_WhateverTheRoomAndDecay(string id)
    {
        var levels = new List<double>();
        foreach (var decay in new[] { 0.5, 1, 2 })
        {
            var reverb = new Reverb(Rate);
            var room = ReverbRooms.Find(id);
            reverb.SetTarget(room, 1, room.DecaySeconds * decay);
            reverb.Settle();
            // pink-ish music stand-in: noise with a falling top (a one-pole low-pass at ~2 kHz)
            var dry = Noise(Rate * 8, 0.3);
            float l = 0, r = 0;
            for (var i = 0; i < dry.Length; i += 2)
            {
                l += 0.25f * (dry[i] - l);
                r += 0.25f * (dry[i + 1] - r);
                dry[i] = l;
                dry[i + 1] = r;
            }
            var wet = (float[])dry.Clone();
            for (var at = 0; at < wet.Length; at += 64) reverb.Process(wet.AsSpan(at, 64));
            for (var i = 0; i < wet.Length; i++) wet[i] -= dry[i];
            var skip = Rate * 2 * 4; // once the room has filled
            var db = 20 * Math.Log10(Rms(wet.AsSpan(skip)) / Rms(dry.AsSpan(skip)));
            output.WriteLine($"{id} x{decay}: wet {db:+0.0;-0.0} dB against dry; default amount {20 * Math.Log10(ToneSettings.DefaultReverbAmount) + db:0.0} dB");
            levels.Add(db);
        }
        Assert.All(levels, db => Assert.InRange(db, -4, 2));
    }

    [Fact]
    public void LoudNoise_AtTheLongestDecay_StaysFiniteAndUnderFullScale()
    {
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(WithReverb("cathedral", 1, ReverbRooms.MaxDecayScale)) };
        var data = Noise(Rate * 10, 1);
        Run(processor, data);
        Assert.All(data, v => Assert.InRange(Math.Abs(v), 0, 1f)); // the limiter after it holds the sum
        var silence = new float[Rate * 2 * 2];
        Run(processor, silence);
        Assert.All(silence, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void InSilence_TheTailRingsOutToExactlyNothing()
    {
        // (no tiny "denormal" numbers left circulating: those cost a lot of CPU)
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(WithReverb("room")) };
        Run(processor, Noise(Rate / 2, 0.5));
        var silence = new float[Rate * 2 * 12];
        Run(processor, silence);
        Assert.All(silence.AsSpan(silence.Length - 4800).ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ChangingTheRoomAndAmountWhilePlaying_NeverJumps()
    {
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(Settings()) };
        var data = new float[Rate * 2 * 6];
        for (var i = 0; i < data.Length / 2; i++)
            data[2 * i] = data[2 * i + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 200 * i / Rate));
        var configs = new[]
        {
            WithReverb("hall", 0.5),
            WithReverb("cathedral", 1, 2),
            WithReverb("room", 0.2),
            Settings(),
            WithReverb("studio", 1, 0.5),
            WithReverb("studio", 0.1, 2),
            new ToneSettings { Enabled = false },
            WithReverb("hall", 1),
        };
        const int block = 480;
        var blocks = data.Length / 2 / block;
        var next = 0;
        for (var b = 0; b < blocks; b++)
        {
            if (b > 0 && b % 70 == 0) processor.Config = ToneConfig.From(configs[next++ % configs.Length]);
            processor.Process(data.AsSpan(b * block * 2, block * 2));
        }
        // The tone (with its room: still a 200 Hz tone, at most a few times louder) moves at most
        // about 0.026 * level per sample; a click would be a step many times that.
        var largest = 0f;
        var where = 0;
        for (var i = 2; i < data.Length; i++)
            if (Math.Abs(data[i] - data[i - 2]) > largest) { largest = Math.Abs(data[i] - data[i - 2]); where = i / 2; }
        Assert.True(largest < 0.06f, $"step {largest} at frame {where} (block {where / (double)block:F1})");
    }

    [Fact]
    public void Processing_AllocatesNothing_AndCostsLittle()
    {
        var processor = new ToneProcessor(44100) { Config = ToneConfig.From(Settings(s => EqPresets.Apply(s, "rock"))) };
        var data = Noise(44100 * 30, 0.3);
        Run(processor, data, 441); // warm up (JIT)

        double Seconds(ToneSettings settings)
        {
            processor.Config = ToneConfig.From(settings);
            var copy = Noise(44100 * 30, 0.3);
            var watch = new Stopwatch();
            var before = GC.GetAllocatedBytesForCurrentThread();
            watch.Start();
            Run(processor, copy, 441);
            watch.Stop();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            return watch.Elapsed.TotalSeconds;
        }
        var without = Seconds(Settings(s => EqPresets.Apply(s, "rock")));
        var with = Seconds(Settings(s => { EqPresets.Apply(s, "rock"); s.Reverb = true; s.ReverbRoom = "cathedral"; s.ReverbAmount = 0.3; }));
        output.WriteLine($"30 s of 44.1 kHz stereo: EQ {without * 1000:0} ms, EQ + reverb {with * 1000:0} ms " +
                         $"(reverb {(with - without) / 30 * 100:0.00}% of a core; a 10 ms block {with / 3000 * 1e6:0} µs)");
        Assert.True(with < 3, $"{with} s"); // far under real time even in a debug build
    }

    [Fact]
    public void ReverbSettings_AreOffByDefault_AndBroughtBackIntoRange()
    {
        var fresh = new ToneSettings();
        Assert.False(fresh.Reverb);
        Assert.Equal(0, ToneConfig.From(Settings()).ReverbMix);
        Assert.Equal(0, ToneConfig.From(new ToneSettings { Reverb = true }).ReverbMix); // equalizer off: no reverb either

        var odd = new ToneSettings { ReverbRoom = "spaceship", ReverbAmount = double.NaN, ReverbDecay = 9 };
        odd.Normalize();
        Assert.Equal(ReverbRooms.DefaultId, odd.ReverbRoom);
        Assert.Equal(ToneSettings.DefaultReverbAmount, odd.ReverbAmount);
        Assert.Equal(ReverbRooms.MaxDecayScale, odd.ReverbDecay);

        var config = ToneConfig.From(WithReverb("cathedral", 0.35, 0.5));
        Assert.Equal(0.35, config.ReverbMix);
        Assert.Equal(ReverbRooms.Find("cathedral").DecaySeconds * 0.5, config.ReverbDecaySeconds, 6);
    }
}
