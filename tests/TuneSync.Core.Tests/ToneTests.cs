using TuneSync.Core.Audio;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class ToneTests
{
    private const int Rate = 48000;

    private static float[] Sine(double frequency, double amplitude, int frames, int rate = Rate)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            data[2 * i] = data[2 * i + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
        return data;
    }

    private static double Rms(ReadOnlySpan<float> data, int fromFrame)
    {
        double sum = 0;
        var n = 0;
        for (var i = fromFrame * 2; i < data.Length; i++, n++) sum += data[i] * (double)data[i];
        return Math.Sqrt(sum / n);
    }

    private static ToneSettings Settings(Action<ToneSettings>? change = null)
    {
        var s = new ToneSettings { Enabled = true, AutoHeadroom = false };
        change?.Invoke(s);
        return s;
    }

    /// <summary>Runs a tone through the processor and measures how much louder it came out.</summary>
    private static double MeasuredGainDb(ToneSettings settings, double frequency, int rate = Rate)
    {
        var processor = new ToneProcessor(rate) { Config = ToneConfig.From(settings) };
        var input = Sine(frequency, 0.05, rate * 2, rate);
        var before = Rms(input, rate);
        processor.Process(input);
        return 20 * Math.Log10(Rms(input, rate) / before);
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(-9.0)]
    [InlineData(12.0)]
    public void Peaking_HasItsGainAtTheCenter_AndNoneFarAway(double gain)
    {
        var filter = BiquadCoefficients.Peaking(Rate, 1000, 1.41, gain);
        Assert.Equal(gain, filter.MagnitudeDb(Rate, 1000), 3);
        Assert.InRange(Math.Abs(filter.MagnitudeDb(Rate, 40)), 0, 0.15);
        Assert.InRange(Math.Abs(filter.MagnitudeDb(Rate, 18000)), 0, 0.3);
    }

    /// <summary>The analog bell the digital one is modeled on.</summary>
    private static double AnalogBellDb(double center, double q, double gainDb, double frequency)
    {
        var a = Math.Pow(10, gainDb / 40);
        var s = new System.Numerics.Complex(0, frequency / center);
        return 20 * Math.Log10(((s * s + s * a / q + 1) / (s * s + s / (a * q) + 1)).Magnitude);
    }

    [Theory]
    [InlineData(44100, 16000, 9, 1.3)]
    [InlineData(44100, 16000, -12, 1.4)]
    [InlineData(48000, 16000, 12, 1.4)]
    [InlineData(44100, 8000, 6, 1.1)]
    [InlineData(44100, 1000, -6, 1.0)]
    [InlineData(96000, 31.25, 12, 1.4)]
    public void Peaking_KeepsTheAnalogShape_UpToTheTopOfTheRange(int rate, double center, double gain, double q)
    {
        var filter = BiquadCoefficients.Peaking(rate, center, q, gain);
        for (var frequency = 20.0; frequency <= 20000; frequency *= 1.05)
            Assert.Equal(AnalogBellDb(center, q, gain, frequency), filter.MagnitudeDb(rate, frequency), 0.9); // the cookbook bell: 5 dB off
        // The cookbook bell was already back at 0 dB at half the sample rate; this one isn't.
        if (center == 16000) Assert.True(Math.Abs(filter.MagnitudeDb(rate, 20000)) > Math.Abs(gain) * 0.4);
    }

    [Fact]
    public void Peaking_IsStableAndFinite_ForEveryBandRateAndLevel()
    {
        foreach (var rate in new[] { 8000, 22050, 32000, 44100, 48000, 88200, 96000, 192000 })
            foreach (var center in ToneCurve.BandFrequencies)
                for (var gain = -ToneCurve.MaxFilterGainDb; gain <= ToneCurve.MaxFilterGainDb; gain += 0.75)
                {
                    var f = BiquadCoefficients.Peaking(rate, center, ToneCurve.BandQ(gain), gain);
                    Assert.True(double.IsFinite(f.B0) && double.IsFinite(f.B1) && double.IsFinite(f.B2) && double.IsFinite(f.A1) && double.IsFinite(f.A2));
                    Assert.True(Math.Abs(f.A2) < 1 && Math.Abs(f.A1) < 1 + f.A2, $"unstable at {rate} Hz, {center} Hz, {gain} dB");
                    if (f != BiquadCoefficients.Identity) Assert.Equal(gain, f.MagnitudeDb(rate, center), 2);
                    // A cut undoes the boost of the same size.
                    var back = BiquadCoefficients.Peaking(rate, center, ToneCurve.BandQ(gain), -gain);
                    foreach (var frequency in new[] { 50.0, 900, 3000, 0.4 * rate })
                        Assert.Equal(0, f.MagnitudeDb(rate, frequency) + back.MagnitudeDb(rate, frequency), 6);
                }
    }

    [Fact]
    public void Bands_AreBroadForSmallMoves_AndTighterForLargeOnes()
    {
        Assert.True(ToneCurve.BandQ(1) < ToneCurve.BandQ(6));
        Assert.True(ToneCurve.BandQ(6) < ToneCurve.BandQ(12));
        Assert.Equal(ToneCurve.BandQ(-9), ToneCurve.BandQ(9));
        Assert.Equal(ToneCurve.BandQ(12), ToneCurve.BandQ(18));
    }

    public static IEnumerable<object[]> SliderSets()
    {
        foreach (var preset in EqPresets.BuiltIn) yield return [preset.BandsDb];
        yield return [new double[] { 12, 12, 12, 12, 12, 12, 12, 12, 12, 12 }];
        yield return [new double[] { 0, 0, 0, 0, 0, 12, 0, 0, 0, 0 }];
        yield return [new double[] { -12, 0, 6, 6, -3, 0, 9, -9, 3, 12 }];
        yield return [new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, -12 }];
    }

    [Theory]
    [MemberData(nameof(SliderSets))]
    public void TheCurve_PassesThroughEverySlider(double[] sliders)
    {
        var settings = Settings(s => s.BandsDb = sliders);
        for (var i = 0; i < sliders.Length; i++)
            Assert.Equal(sliders[i], ToneCurve.ResponseDb(settings, ToneCurve.BandFrequencies[i]), 0.15);
        // ...and stays near them in between: no humps where neighbors overlap.
        double low = Math.Min(0, sliders.Min()) - 2.5, high = Math.Max(0, sliders.Max()) + 2.5;
        for (var frequency = 25.0; frequency < 19000; frequency *= 1.06)
            Assert.InRange(ToneCurve.ResponseDb(settings, frequency), low, high);
    }

    [Fact]
    public void TheCurve_IsTheSameAtASongsOwnRate()
    {
        // The filters are worked out for 48 kHz; at 44.1 kHz the top band must still land on its slider.
        var settings = Settings(s => EqPresets.Apply(s, "treble"));
        for (var i = 0; i < ToneSettings.BandCount; i++)
            Assert.Equal(settings.BandsDb[i], ToneCurve.ResponseDb(settings, ToneCurve.BandFrequencies[i], 44100), 0.5);
        Assert.Equal(settings.BandsDb[5], MeasuredGainDb(settings, 1000, 44100), 0.2);
        Assert.Equal(settings.BandsDb[9], MeasuredGainDb(settings, 16000, 44100), 0.5);
    }

    [Fact]
    public void FlatSliders_LeaveEveryFilterOff()
    {
        Span<double> gains = stackalloc double[ToneCurve.FilterCount];
        ToneCurve.FilterGains(Settings(), gains);
        Assert.All(gains.ToArray(), g => Assert.Equal(0, g));
    }

    [Fact]
    public void Shelves_ReachTheirGainAtTheirEnd_AndLeaveTheOtherEndAlone()
    {
        var low = BiquadCoefficients.LowShelf(Rate, ToneCurve.BassFrequency, ToneCurve.ShelfQ, 8);
        Assert.Equal(8, low.MagnitudeDb(Rate, 10), 1);
        Assert.Equal(4, low.MagnitudeDb(Rate, ToneCurve.BassFrequency), 1);
        Assert.InRange(Math.Abs(low.MagnitudeDb(Rate, 8000)), 0, 0.1);

        var high = BiquadCoefficients.HighShelf(Rate, ToneCurve.TrebleFrequency, ToneCurve.ShelfQ, -6);
        Assert.Equal(-6, high.MagnitudeDb(Rate, 23000), 1);
        Assert.Equal(-3, high.MagnitudeDb(Rate, ToneCurve.TrebleFrequency), 1);
        Assert.InRange(Math.Abs(high.MagnitudeDb(Rate, 100)), 0, 0.1);
    }

    [Fact]
    public void ZeroGain_AndBandsAboveWhatTheRateCanCarry_AreIdentity()
    {
        Assert.Equal(BiquadCoefficients.Identity, BiquadCoefficients.Peaking(Rate, 1000, 1.41, 0));
        Assert.Equal(BiquadCoefficients.Identity, BiquadCoefficients.Peaking(22050, 16000, 1.41, 6));
        Assert.NotEqual(BiquadCoefficients.Identity, BiquadCoefficients.Peaking(44100, 16000, 1.41, 6));
    }

    [Theory]
    [InlineData(62.5, 44100)]
    [InlineData(1000, 44100)]
    [InlineData(1000, 96000)]
    [InlineData(8000, 48000)]
    [InlineData(300, 48000)]
    public void Processor_ChangesATone_ByWhatTheCurveSays(double frequency, int rate)
    {
        var settings = Settings(s =>
        {
            s.BandsDb = [3, 6, -4, 2, 0, 9, -3, 0, 5, -8];
            s.BassDb = 4;
            s.TrebleDb = -3;
            s.PreampDb = -2;
        });
        var expected = ToneCurve.ResponseDb(settings, frequency, rate);
        Assert.Equal(expected, MeasuredGainDb(settings, frequency, rate), 1);
    }

    [Fact]
    public void Processor_Off_GivesBackExactlyWhatWentIn()
    {
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(new ToneSettings { Enabled = false, BandsDb = [9, 9, 9, 9, 9, 9, 9, 9, 9, 9] }) };
        var random = new Random(1);
        var input = new float[Rate * 2];
        for (var i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 1.6 - 0.8);
        var output = (float[])input.Clone();
        for (var at = 0; at < output.Length; at += 1000) processor.Process(output.AsSpan(at, Math.Min(1000, output.Length - at)));

        var delay = processor.LatencyFrames * 2;
        for (var i = delay; i < output.Length; i++) Assert.Equal(input[i - delay], output[i]);
    }

    [Fact]
    public void Limiter_NeverLetsABoostPastFullScale_AndLeavesQuietSoundAlone()
    {
        var settings = Settings(s => { s.BandsDb = [12, 12, 12, 12, 12, 12, 12, 12, 12, 12]; s.BassDb = 12; s.TrebleDb = 12; s.PreampDb = 12; s.Width = 2; });
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(settings) };
        var random = new Random(7);
        var data = new float[Rate * 4];
        for (var i = 0; i < data.Length; i++)
        {
            // Full-scale noise with silences and sudden bursts in between.
            var loud = (i / 9000) % 3 != 1;
            data[i] = loud ? (float)(random.NextDouble() * 2 - 1) : 0;
        }
        for (var at = 0; at < data.Length; at += 882) processor.Process(data.AsSpan(at, Math.Min(882, data.Length - at)));

        Assert.All(data, v => Assert.InRange(Math.Abs(v), 0, 1f));
        Assert.True(processor.TakeLimiterReductionDb() > 6);
        Assert.Equal(0, processor.TakeLimiterReductionDb());

        var limiter = new PeakLimiter(Rate);
        var quiet = Sine(440, 0.9, Rate);
        var copy = (float[])quiet.Clone();
        limiter.Process(copy);
        for (var i = limiter.LatencyFrames * 2; i < copy.Length; i++) Assert.Equal(quiet[i - limiter.LatencyFrames * 2], copy[i]);
    }

    [Fact]
    public void LimiterOff_LetsTheBoostThrough_AndOnAgainHoldsIt()
    {
        var boosted = Settings(s => { s.PreampDb = 12; s.Limiter = false; });
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(boosted) };
        var data = Sine(1000, 0.5, Rate);
        processor.Process(data);
        Assert.Equal(0.5 * Math.Pow(10, 12 / 20.0), data.Skip(Rate).Max(Math.Abs), 2); // +12 dB, untouched
        Assert.Equal(0, processor.TakeLimiterReductionDb());

        boosted.Limiter = true;
        processor.Config = ToneConfig.From(boosted);
        data = Sine(1000, 0.5, Rate);
        processor.Process(data);
        Assert.All(data.Skip(Rate / 10), v => Assert.InRange(Math.Abs(v), 0, 1f));
        Assert.True(processor.TakeLimiterReductionDb() > 5);
        Assert.True(new ToneSettings().Limiter); // on unless switched off
    }

    [Fact]
    public void Limiter_TurnsALoudToneDownToTheCeiling_WithoutDistortingItsShape()
    {
        var limiter = new PeakLimiter(Rate);
        var data = Sine(1000, 2.0, Rate);
        limiter.Process(data);
        var peak = 0f;
        for (var i = Rate; i < data.Length; i++) peak = Math.Max(peak, Math.Abs(data[i]));
        Assert.InRange(peak, 0.9f, PeakLimiter.Ceiling + 1e-4f);
        // Still a sine: its level is peak / sqrt(2), not the higher level of a clipped (squared-off) wave.
        Assert.Equal(peak / Math.Sqrt(2), Rms(data, Rate / 2), 2);
    }

    [Fact]
    public void Limiter_HoldsSteadyThroughABassNote_InsteadOfWobblingWithEachCycle()
    {
        // A boosted 40 Hz note: a limiter that eases off between its peaks (every 25 ms) turns the
        // level up and down 40 times a second, and that wobble is heard as a smeared sound.
        var limiter = new PeakLimiter(Rate);
        var data = Sine(40, 2.0, Rate * 3);
        limiter.Process(data);
        // after it has settled: the peak of every cycle the same, to within a hundredth of a dB
        var cycle = Rate / 40 * 2; // samples, both channels
        double lowest = double.MaxValue, highest = 0;
        for (var start = Rate * 2; start + cycle <= data.Length; start += cycle)
        {
            var peak = 0f;
            for (var i = start; i < start + cycle; i++) peak = Math.Max(peak, Math.Abs(data[i]));
            lowest = Math.Min(lowest, peak);
            highest = Math.Max(highest, peak);
        }
        Assert.True(20 * Math.Log10(highest / lowest) < 0.01, $"{20 * Math.Log10(highest / lowest):0.000} dB");
        Assert.InRange(highest, 0.9, PeakLimiter.Ceiling + 1e-4);

        // and once the loud part is over, the level comes back (to within 0.2 dB) within seconds
        var after = Sine(1000, 0.5, Rate * 8);
        limiter.Process(after);
        var down = 20 * Math.Log10(0.5 / Math.Sqrt(2) / Rms(after, Rate * 15 / 2));
        Assert.InRange(down, -0.01, 0.2);
    }

    [Fact]
    public void ChangingSettingsWhilePlaying_NeverJumps()
    {
        // A steady low tone: any jump in the output shows as a sample-to-sample step far above the tone's own.
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(Settings()) };
        var data = Sine(200, 0.2, Rate * 4);
        var configs = new[]
        {
            Settings(s => EqPresets.Apply(s, "rock")),
            Settings(s => { s.BandsDb = [12, 12, 12, 12, 12, 12, 12, 12, 12, 12]; s.BassDb = 12; }),
            new ToneSettings { Enabled = false },
            Settings(s => { s.BandsDb = [-12, -12, -12, -12, -12, -12, -12, -12, -12, -12]; s.Width = 0; s.Balance = 1; }),
            Settings(s => { EqPresets.Apply(s, "bass"); s.AutoHeadroom = true; }),
            Settings(),
            Settings(s => s.PreampDb = 12),
        };
        const int block = 480;
        var blocks = data.Length / 2 / block;
        var next = 0;
        for (var b = 0; b < blocks; b++)
        {
            if (b > 0 && b % 50 == 0) processor.Config = ToneConfig.From(configs[next++ % configs.Length]); // every half second
            if (b == 220) processor.Volume = 0;  // mute...
            if (b == 260) processor.Volume = 1;  // ...and back
            processor.Process(data.AsSpan(b * block * 2, block * 2));
        }

        // The tone at its loudest possible here (boosts are limited to full scale) moves at most
        // 2*pi*200/48000 = 0.026 per sample.
        var largest = 0f;
        var where = 0;
        for (var i = 2; i < data.Length; i += 2) if (Math.Abs(data[i] - data[i - 2]) > largest) { largest = Math.Abs(data[i] - data[i - 2]); where = i / 2; }
        Assert.True(largest is > 0.001f and < 0.035f, $"step {largest} at frame {where} (block {where / 480.0:F2}): {data[where * 2 - 4]} {data[where * 2 - 2]} {data[where * 2]} {data[where * 2 + 2]}");
    }

    [Fact]
    public void Processor_GoesBackToSilence_AfterTheSoundStops()
    {
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(Settings(s => EqPresets.Apply(s, "loudness"))) };
        var burst = Sine(100, 0.5, 4800);
        processor.Process(burst);
        var silence = new float[Rate * 2 * 3];
        processor.Process(silence);
        Assert.All(silence.AsSpan(silence.Length - 2000).ToArray(), v => Assert.InRange(Math.Abs(v), 0, 1e-12f));
    }

    [Fact]
    public void AutoHeadroom_TakesOffExactlyTheCurvesHighestBoost()
    {
        var settings = Settings(s => { EqPresets.Apply(s, "bass"); s.AutoHeadroom = true; });
        var config = ToneConfig.From(settings);
        Assert.InRange(config.HeadroomDb, 6, 12);
        Assert.Equal(-config.HeadroomDb, config.GainDb, 6);
        // With it, no frequency comes out louder than it went in (to within the curve's sampling, a tenth of a dB).
        foreach (var f in new[] { 25.0, 40, 62.5, 90, 125, 500, 4000 })
            Assert.True(ToneCurve.ResponseDb(settings, f) - config.HeadroomDb <= 0.1);

        // Only cuts: next to nothing to make room for (the curve rises a hair between a cut band and a flat one).
        Assert.InRange(ToneConfig.From(Settings(s => { s.BandsDb = [-3, -3, 0, 0, 0, 0, 0, 0, 0, -6]; s.AutoHeadroom = true; })).HeadroomDb, 0, 0.6);
    }

    [Fact]
    public void BalanceAndWidth_DoWhatTheySay()
    {
        var data = new float[Rate * 2];
        for (var i = 0; i < Rate; i++)
        {
            data[2 * i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / Rate));
            data[2 * i + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 660 * i / Rate));
        }
        var right = (float[])data.Clone();
        new ToneProcessor(Rate) { Config = ToneConfig.From(Settings(s => s.Balance = 1)) }.Process(right);
        var mono = (float[])data.Clone();
        new ToneProcessor(Rate) { Config = ToneConfig.From(Settings(s => s.Width = 0)) }.Process(mono);

        for (var i = Rate; i < data.Length; i += 2)
        {
            Assert.Equal(0f, right[i]);          // left silent
            Assert.Equal(mono[i], mono[i + 1]);  // both channels the same
        }
        // The right channel alone carries half the energy of the pair.
        Assert.Equal(Rms(data, Rate / 2), Rms(right, Rate / 2) * Math.Sqrt(2), 2);
    }

    [Fact]
    public void BuiltInPresets_AreWellFormed()
    {
        Assert.Equal(EqPresets.FlatId, EqPresets.BuiltIn[0].Id);
        Assert.Equal(EqPresets.BuiltIn.Count, EqPresets.BuiltIn.Select(p => p.Id).Distinct().Count());
        Assert.Equal(EqPresets.BuiltIn.Count, EqPresets.BuiltIn.Select(p => p.Name).Distinct().Count());
        Assert.All(EqPresets.BuiltIn, p =>
        {
            Assert.Equal(ToneSettings.BandCount, p.BandsDb.Length);
            Assert.All(p.BandsDb, g => Assert.InRange(g, -ToneSettings.MaxGainDb, ToneSettings.MaxGainDb));
        });
        Assert.All(EqPresets.BuiltIn[0].BandsDb, g => Assert.Equal(0, g));
        foreach (var name in new[] { "bass", "vocal", "rock", "pop", "jazz", "classical", "electronic", "loudness", "latenight" })
            Assert.NotNull(EqPresets.Find(name));
    }

    [Fact]
    public void UserPresets_SaveApplyRenameDelete()
    {
        var s = new ToneSettings { BandsDb = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], PreampDb = -3 };
        Assert.False(EqPresets.Save(s, "록"));   // a built-in's name
        Assert.False(EqPresets.Save(s, "  "));
        Assert.True(EqPresets.Save(s, " 내 헤드폰 "));
        Assert.Equal("내 헤드폰", s.Preset);

        Assert.True(EqPresets.Apply(s, "rock"));
        Assert.Equal(EqPresets.Find("rock")!.BandsDb[0], s.BandsDb[0]);
        Assert.Equal(0, s.PreampDb);
        Assert.True(EqPresets.Apply(s, "내 헤드폰"));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], s.BandsDb);
        Assert.Equal(-3, s.PreampDb);
        Assert.False(EqPresets.Apply(s, "없는 것"));

        // Changing the bands afterwards doesn't reach into the saved preset.
        s.BandsDb[0] = -12;
        Assert.Equal(1, s.UserPresets[0].BandsDb[0]);

        Assert.False(EqPresets.Rename(s, "내 헤드폰", "팝"));
        Assert.True(EqPresets.Rename(s, "내 헤드폰", "거실 스피커"));
        Assert.Equal("거실 스피커", s.Preset);
        Assert.True(EqPresets.Save(s, "거실 스피커")); // saving again replaces
        Assert.Single(s.UserPresets);
        Assert.Equal(-12, s.UserPresets[0].BandsDb[0]);

        Assert.True(EqPresets.Delete(s, "거실 스피커"));
        Assert.Empty(s.UserPresets);
        Assert.Null(s.Preset);
        Assert.False(EqPresets.Delete(s, "거실 스피커"));
    }

    [Fact]
    public void Settings_SurviveARestart_AndOutOfRangeValuesAreBroughtBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunesync-tone-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings();
            Assert.False(settings.Tone.Enabled); // off until the user turns it on
            Assert.False(settings.Tone.AutoHeadroom);
            settings.Tone = new ToneSettings
            {
                Enabled = true, PreampDb = -2.5, BandsDb = [1, -2, 3, -4, 5, -6, 7, -8, 9, -10],
                BassDb = 4, TrebleDb = -1.5, Width = 1.4, Balance = -0.25, AutoHeadroom = true, Preset = null,
                UserPresets = [new EqPreset { Name = "내 것", PreampDb = 1, BandsDb = [0, 1, 0, 1, 0, 1, 0, 1, 0, 1] }],
            };
            AppSettingsStore.Save(settings, path);
            var loaded = AppSettingsStore.Load(path).Tone;

            Assert.True(loaded.Enabled);
            Assert.Equal(-2.5, loaded.PreampDb);
            Assert.Equal([1, -2, 3, -4, 5, -6, 7, -8, 9, -10], loaded.BandsDb);
            Assert.Equal(4, loaded.BassDb);
            Assert.Equal(-1.5, loaded.TrebleDb);
            Assert.Equal(1.4, loaded.Width);
            Assert.Equal(-0.25, loaded.Balance);
            Assert.True(loaded.AutoHeadroom);
            Assert.Null(loaded.Preset);
            Assert.Equal("내 것", Assert.Single(loaded.UserPresets).Name);
            Assert.Equal([0, 1, 0, 1, 0, 1, 0, 1, 0, 1], loaded.UserPresets[0].BandsDb);

            // A settings file from before the equalizer existed: defaults.
            File.WriteAllText(path, "{\"Volume\":0.5}");
            var old = AppSettingsStore.Load(path);
            Assert.False(old.Tone.Enabled);

            // One from when the headroom was on by default: that default doesn't carry over.
            File.WriteAllText(path, "{\"Tone\":{\"Enabled\":true,\"AutoHeadroom\":true}}");
            Assert.False(AppSettingsStore.Load(path).Tone.AutoHeadroom);
            Assert.Equal(ToneSettings.BandCount, old.Tone.BandsDb.Length);

            // Hand-edited nonsense.
            File.WriteAllText(path, "{\"Tone\":{\"Enabled\":true,\"BandsDb\":[99,-99,1],\"Width\":7,\"Balance\":-3,\"PreampDb\":40,\"UserPresets\":[{\"Name\":\"\",\"BandsDb\":[]},{\"Name\":\"a\",\"BandsDb\":[50]}]}}");
            var odd = AppSettingsStore.Load(path).Tone;
            Assert.Equal([12, -12, 1, 0, 0, 0, 0, 0, 0, 0], odd.BandsDb);
            Assert.Equal(2, odd.Width);
            Assert.Equal(-1, odd.Balance);
            Assert.Equal(12, odd.PreampDb);
            Assert.Equal(12, Assert.Single(odd.UserPresets).BandsDb[0]);
            Assert.Equal(ToneSettings.BandCount, odd.UserPresets[0].BandsDb.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
