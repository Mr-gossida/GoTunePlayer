using TuneSync.Core.Audio;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

/// <summary>The 31-band equalizer, and carrying ten-band settings and presets over to it.</summary>
public class EqBandsTests
{
    private const int Rate = 48000;
    private const int Third = ToneSettings.ThirdOctaveBands;

    private static ToneSettings Settings(Action<ToneSettings>? change = null)
    {
        var s = new ToneSettings { Enabled = true, AutoHeadroom = false };
        change?.Invoke(s);
        return s;
    }

    private static ToneSettings Third31(double[] bands) => Settings(s => { s.BandCount = Third; s.BandsDb = bands; });

    /// <summary>Runs a tone through the processor and measures how much louder it came out.</summary>
    private static double MeasuredGainDb(ToneSettings settings, double frequency, int rate)
    {
        var processor = new ToneProcessor(rate) { Config = ToneConfig.From(settings) };
        var data = new float[rate * 4];
        for (var i = 0; i < rate * 2; i++)
            data[2 * i] = data[2 * i + 1] = (float)(0.05 * Math.Sin(2 * Math.PI * frequency * i / rate));
        var before = Rms(data, rate);
        processor.Process(data);
        return 20 * Math.Log10(Rms(data, rate) / before);
    }

    private static double Rms(ReadOnlySpan<float> data, int fromFrame)
    {
        double sum = 0;
        var n = 0;
        for (var i = fromFrame * 2; i < data.Length; i++, n++) sum += data[i] * (double)data[i];
        return Math.Sqrt(sum / n);
    }

    [Fact]
    public void ThirdOctaveBands_AreTheIsoSeries_AndIncludeTheTen()
    {
        var centers = ToneCurve.ThirdOctaveFrequencies;
        Assert.Equal(Third, centers.Length);
        Assert.Equal(Third, ToneCurve.ThirdOctaveNames.Length);
        Assert.InRange(centers[0], 19.6, 20.1);
        Assert.Equal(1000, centers[17], 9);
        Assert.InRange(centers[^1], 19900, 20200);
        for (var i = 0; i < ToneSettings.OctaveBands; i++)
            Assert.Equal(ToneCurve.BandFrequencies[i], centers[2 + 3 * i], 6);
        // the 31 bells are a third as wide as the ten's: a bell of about an octave becomes one of a third (Q 1.4 -> 4.28)
        Assert.InRange(ToneCurve.BandQ(12, Third), 4.2, 4.35);
        Assert.True(ToneCurve.BandQ(1, Third) < ToneCurve.BandQ(12, Third));
    }

    public static IEnumerable<object[]> SliderSets()
    {
        foreach (var preset in EqPresets.BuiltIn) yield return [ToneCurve.Resample(preset.BandsDb, Third)];
        yield return [Enumerable.Repeat(12.0, Third).ToArray()];
        yield return [Enumerable.Range(0, Third).Select(i => i == 17 ? 12.0 : 0).ToArray()];   // a lone 1 kHz peak
        yield return [Enumerable.Range(0, Third).Select(i => i == 20 ? -12.0 : 0).ToArray()];  // a lone 2 kHz notch
        yield return [Enumerable.Range(0, Third).Select(i => i % 2 == 0 ? 4.0 : -4).ToArray()]; // every other band
        yield return [Enumerable.Range(0, Third).Select(i => Math.Round(10 * Math.Sin(i * 0.7), 1)).ToArray()];
        yield return [Enumerable.Range(0, Third).Select(i => i == 0 || i == Third - 1 ? 12.0 : 0).ToArray()]; // the two ends
    }

    [Theory]
    [MemberData(nameof(SliderSets))]
    public void TheCurve_PassesThroughEverySlider_On31Bands(double[] sliders)
    {
        var settings = Third31(sliders);
        for (var i = 0; i < Third; i++)
            Assert.Equal(sliders[i], ToneCurve.ResponseDb(settings, ToneCurve.ThirdOctaveFrequencies[i]), 0.15);
        // ...and stays near them in between: no humps where neighbors overlap.
        double low = Math.Min(0, sliders.Min()) - 2.5, high = Math.Max(0, sliders.Max()) + 2.5;
        for (var frequency = 18.0; frequency < 21000; frequency *= 1.03)
            Assert.InRange(ToneCurve.ResponseDb(settings, frequency), low, high);
    }

    [Fact]
    public void Peaking_IsStableAndFinite_ForEvery31BandRateAndLevel()
    {
        foreach (var rate in new[] { 8000, 22050, 32000, 44100, 48000, 88200, 96000, 192000, 384000 })
            for (var band = 0; band < Third; band++)
                for (var gain = -ToneCurve.MaxFilterGainDb; gain <= ToneCurve.MaxFilterGainDb; gain += 1.5)
                {
                    var center = ToneCurve.ThirdOctaveFrequencies[band];
                    var f = BiquadCoefficients.Peaking(rate, center, ToneCurve.BandQ(gain, Third), gain);
                    Assert.True(double.IsFinite(f.B0) && double.IsFinite(f.B1) && double.IsFinite(f.B2) && double.IsFinite(f.A1) && double.IsFinite(f.A2));
                    Assert.True(Math.Abs(f.A2) < 1 && Math.Abs(f.A1) < 1 + f.A2, $"unstable at {rate} Hz, {center} Hz, {gain} dB");
                    if (f != BiquadCoefficients.Identity) Assert.Equal(gain, f.MagnitudeDb(rate, center), 2);
                }
    }

    [Theory]
    [InlineData(20, 44100)]
    [InlineData(1000, 44100)]
    [InlineData(1250, 48000)]
    [InlineData(5000, 96000)]
    [InlineData(16000, 44100)]
    [InlineData(20000, 96000)]
    public void Processor_ChangesATone_ByWhatThe31BandCurveSays(double frequency, int rate)
    {
        var settings = Third31([.. Enumerable.Range(0, Third).Select(i => Math.Round(9 * Math.Sin(i * 0.9), 1))]);
        settings.BassDb = 2;
        settings.TrebleDb = -2;
        var expected = ToneCurve.ResponseDb(settings, frequency, rate);
        Assert.Equal(expected, MeasuredGainDb(settings, frequency, rate), 1);
    }

    [Fact]
    public void ABandAboveWhatTheRateCarries_IsLeftOut_AndTheRestStillPlay()
    {
        // at 32 kHz the 16 and 20 kHz bands can't exist: they drop out, and the others are unaffected
        var settings = Third31([.. Enumerable.Repeat(6.0, Third)]);
        Assert.Equal(ToneCurve.ResponseDb(settings, 1000, 32000), MeasuredGainDb(settings, 1000, 32000), 1);
        Assert.InRange(MeasuredGainDb(settings, 1000, 32000), 4, 8);
    }

    // --- carrying the ten bands over ---

    [Fact]
    public void TenBandSettings_LoadAsTheyWere_AndSwitchingTo31KeepsTheCurve()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunesync-eq31-{Guid.NewGuid():N}.json");
        try
        {
            // a settings file from before the 31 bands: no BandCount, ten levels
            File.WriteAllText(path, "{\"Tone\":{\"Enabled\":true,\"BandsDb\":[5,4,2,-1,-2,-1,1,3,4,5],\"Preset\":null}}");
            var tone = AppSettingsStore.Load(path).Tone;
            Assert.Equal(ToneSettings.OctaveBands, tone.BandCount);
            double[] ten = [5, 4, 2, -1, -2, -1, 1, 3, 4, 5];
            Assert.Equal(ten, tone.BandsDb);
            var before = Settings(s => s.BandsDb = [.. ten]);

            tone.ChangeBandCount(Third);
            Assert.Equal(Third, tone.BandCount);
            Assert.Equal(Third, tone.BandsDb.Length);
            // the sliders shared with the ten keep their levels...
            for (var i = 0; i < ToneSettings.OctaveBands; i++)
                Assert.Equal(ten[i], tone.BandsDb[2 + 3 * i], 0.15);
            // ...and what is heard stays the same all the way across (within a fraction of a dB)
            for (var frequency = 25.0; frequency < 18000; frequency *= 1.05)
                Assert.Equal(ToneCurve.ResponseDb(before, frequency), ToneCurve.ResponseDb(tone, frequency), 0.6);
            // below 31 Hz the new sliders follow where the ten's curve went, not a copy of the 31 Hz one
            Assert.InRange(tone.BandsDb[0], 2, 5);

            // saved and loaded again: 31 bands
            var settings = AppSettingsStore.Load(path);
            settings.Tone = tone;
            AppSettingsStore.Save(settings, path);
            var loaded = AppSettingsStore.Load(path).Tone;
            Assert.Equal(Third, loaded.BandCount);
            Assert.Equal(tone.BandsDb, loaded.BandsDb);

            // and back to ten: where it started
            loaded.ChangeBandCount(ToneSettings.OctaveBands);
            Assert.Equal(ToneSettings.OctaveBands, loaded.BandsDb.Length);
            for (var i = 0; i < ten.Length; i++) Assert.Equal(ten[i], loaded.BandsDb[i], 0.2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resample_IsExactForTheSameBands_AndFlatStaysFlat()
    {
        double[] ten = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(ten, ToneCurve.Resample(ten, ToneSettings.OctaveBands));
        Assert.All(ToneCurve.Resample(new double[10], Third), g => Assert.Equal(0, g));
        Assert.All(ToneCurve.Resample(new double[Third], ToneSettings.OctaveBands), g => Assert.Equal(0, g));
        // whatever the curve, the new sliders stay within their range and to a tenth of a dB
        var resampled = ToneCurve.Resample([12, 12, 12, 12, 12, 12, 12, 12, 12, 12], Third);
        Assert.All(resampled, g =>
        {
            Assert.InRange(g, -ToneSettings.MaxGainDb, ToneSettings.MaxGainDb);
            Assert.Equal(g, Math.Round(g, 1));
        });
    }

    [Fact]
    public void ABandCountThatDoesntExist_IsTakenAsTen_AndMismatchedLevelsAreCarriedOver()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunesync-eq31-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"Tone\":{\"BandCount\":17,\"BandsDb\":[1,2,3,4,5,6,7,8,9,10]}}");
            var odd = AppSettingsStore.Load(path).Tone;
            Assert.Equal(ToneSettings.OctaveBands, odd.BandCount);
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], odd.BandsDb);

            // 31 bands asked for, ten levels saved (written by hand, or by a build that crashed mid-switch): the curve, not zeros
            File.WriteAllText(path, "{\"Tone\":{\"BandCount\":31,\"BandsDb\":[6,6,6,6,6,6,6,6,6,6]}}");
            var carried = AppSettingsStore.Load(path).Tone;
            Assert.Equal(Third, carried.BandsDb.Length);
            Assert.Equal(6, carried.BandsDb[17], 0.15);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Presets_ApplyToEitherSetOfBands()
    {
        var s = Settings(s => s.BandsDb = [0, 0, 0, 0, 0, 6, 0, 0, 0, 0]);
        Assert.True(EqPresets.Save(s, "십 밴드"));                 // saved with ten
        s.ChangeBandCount(Third);
        s.BandsDb[17] = -9;
        Assert.True(EqPresets.Save(s, "삼십일 밴드"));             // saved with 31
        Assert.Equal(Third, s.UserPresets[1].BandsDb.Length);
        Assert.Equal(ToneSettings.OctaveBands, s.UserPresets[0].BandsDb.Length);

        // a ten-band preset on 31 bands: its curve
        Assert.True(EqPresets.Apply(s, "십 밴드"));
        Assert.Equal(Third, s.BandsDb.Length);
        Assert.Equal(6, s.BandsDb[17], 0.15);
        Assert.Equal(0, s.BandsDb[5], 0.5);

        // a built-in on 31 bands: its curve too
        Assert.True(EqPresets.Apply(s, "rock"));
        Assert.Equal(Third, s.BandsDb.Length);
        Assert.Equal(EqPresets.Find("rock")!.BandsDb[0], s.BandsDb[2], 0.15);
        Assert.Equal("rock", s.Preset);

        // a 31-band preset on ten bands
        s.ChangeBandCount(ToneSettings.OctaveBands);
        Assert.True(EqPresets.Apply(s, "삼십일 밴드"));
        Assert.Equal(ToneSettings.OctaveBands, s.BandsDb.Length);
        Assert.Equal(-9, s.BandsDb[5], 0.15);

        // the saved 31 stay 31 through a save/load
        var path = Path.Combine(Path.GetTempPath(), $"tunesync-eq31-{Guid.NewGuid():N}.json");
        try
        {
            AppSettingsStore.Save(new AppSettings { Tone = s }, path);
            var loaded = AppSettingsStore.Load(path).Tone;
            Assert.Equal(Third, loaded.UserPresets[1].BandsDb.Length);
            Assert.Equal(s.UserPresets[1].BandsDb, loaded.UserPresets[1].BandsDb);
            Assert.Equal(s.UserPresets[0].BandsDb, loaded.UserPresets[0].BandsDb);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SwitchingBandsWhilePlaying_NeverJumps_AndEndsOnTheNewCurve()
    {
        var ten = Settings(s => EqPresets.Apply(s, "rock"));
        var processor = new ToneProcessor(Rate) { Config = ToneConfig.From(ten) };
        var data = new float[Rate * 2 * 3];
        for (var i = 0; i < Rate * 3; i++)
            data[2 * i] = data[2 * i + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 200 * i / Rate));
        const int block = 480;
        var thirty = Settings(s => { EqPresets.Apply(s, "rock"); s.ChangeBandCount(Third); s.BandsDb[10] = 9; });
        for (var b = 0; b < data.Length / 2 / block; b++)
        {
            if (b == 100) processor.Config = ToneConfig.From(thirty);
            if (b == 200) processor.Config = ToneConfig.From(ten);
            processor.Process(data.AsSpan(b * block * 2, block * 2));
        }
        var largest = 0f;
        for (var i = 2; i < data.Length; i += 2) largest = Math.Max(largest, Math.Abs(data[i] - data[i - 2]));
        Assert.True(largest < 0.035f, $"step {largest}");

        // the 31-band config runs the 31 filters only
        var gains = ToneConfig.From(thirty).FilterGainsDb;
        var first = ToneCurve.FirstFilterOf(Third);
        for (var i = ToneCurve.FirstFilterOf(ToneSettings.OctaveBands); i < first; i++) Assert.Equal(0, gains[i]);
        Assert.NotEqual(0, gains[first + 10]);
    }

    [Fact]
    public void Off_StillGivesBackExactlyWhatWentIn_With31Bands()
    {
        var processor = new ToneProcessor(Rate)
        {
            Config = ToneConfig.From(new ToneSettings { Enabled = false, BandCount = Third, BandsDb = [.. Enumerable.Repeat(9.0, Third)] }),
        };
        var random = new Random(3);
        var input = new float[Rate];
        for (var i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 1.6 - 0.8);
        var output = (float[])input.Clone();
        processor.Process(output);
        var delay = processor.LatencyFrames * 2;
        for (var i = delay; i < output.Length; i++) Assert.Equal(input[i - delay], output[i]);
    }

    [Fact]
    public void AutoHeadroom_Works_On31Bands()
    {
        var settings = Third31(ToneCurve.Resample(EqPresets.Find("bass")!.BandsDb, Third));
        settings.AutoHeadroom = true;
        var config = ToneConfig.From(settings);
        Assert.InRange(config.HeadroomDb, 5, 12);
        foreach (var f in new[] { 20.0, 31.5, 50, 90, 400, 4000 })
            Assert.True(ToneCurve.ResponseDb(settings, f) - config.HeadroomDb <= 0.1);
    }
}
