using NAudio.Wave;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class SpectrumAnalyzerTests
{
    /// <summary>A WAV file: <paramref name="hz"/> for the first second, then silence.</summary>
    private static string ToneThenSilence(double hz)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tone-{Guid.NewGuid():N}.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        using var writer = new WaveFileWriter(path, format);
        var samples = new float[44100 * 2];
        for (var i = 0; i < 44100; i++) samples[i] = 0.5f * (float)Math.Sin(2 * Math.PI * hz * i / 44100);
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static int BandOf(double hz, int bands) =>
        (int)(Math.Log(hz / 40) / Math.Log(16000.0 / 40) * bands);

    [Fact]
    public void A_tone_lights_up_its_own_band_and_silence_none()
    {
        var path = ToneThenSilence(1000);
        try
        {
            var data = SpectrumAnalyzer.Analyze(path);

            Assert.Equal(SpectrumAnalyzer.DefaultBands, data.Bands);
            Assert.InRange(data.FramesPerSecond, 59, 61);
            Assert.InRange(data.FrameCount, 110, 125); // two seconds

            var during = new float[data.Bands];
            data.Sample(0.5, during);
            var loudest = Array.IndexOf(during, during.Max());
            Assert.InRange(loudest, BandOf(1000, data.Bands) - 2, BandOf(1000, data.Bands) + 2);
            Assert.True(during[loudest] > 0.9f);
            Assert.True(during[BandOf(100, data.Bands)] < 0.2f);   // far below the tone: dark
            Assert.True(during[BandOf(8000, data.Bands)] < 0.2f);  // far above: dark

            var after = new float[data.Bands];
            data.Sample(1.7, after);
            Assert.True(after.Max() < 0.05f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sampling_blends_neighbouring_frames_and_is_empty_past_the_end()
    {
        var path = ToneThenSilence(440);
        try
        {
            var data = SpectrumAnalyzer.Analyze(path);
            var band = BandOf(440, data.Bands);
            var a = data.Level(10, band);
            var b = data.Level(11, band);
            var mid = new float[data.Bands];
            data.Sample(10.5 / data.FramesPerSecond, mid);
            Assert.Equal((a + b) / 2, mid[band], 3);

            var beyond = new float[data.Bands];
            data.Sample(60, beyond);
            Assert.All(beyond, v => Assert.Equal(0, v));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
