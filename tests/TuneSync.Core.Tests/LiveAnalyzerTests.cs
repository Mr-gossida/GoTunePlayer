using TuneSync.Core.Audio;

namespace TuneSync.Core.Tests;

public class LiveAnalyzerTests
{
    private const int Rate = 44100;

    /// <summary>A tone going through the player's chain, a screen frame's worth at a time.</summary>
    private sealed class Tone(double hz, float left, float right)
    {
        public ToneProcessor Processor { get; } = new(Rate);
        private long _at;

        public void Play(int frames)
        {
            var buffer = new float[frames * 2];
            for (var i = 0; i < frames; i++, _at++)
            {
                var s = (float)Math.Sin(2 * Math.PI * hz * _at / Rate);
                buffer[i * 2] = s * left;
                buffer[i * 2 + 1] = s * right;
            }
            Processor.Process(buffer);
        }
    }

    [Fact]
    public void Spectrum_peaks_at_the_tone_and_meters_read_its_level()
    {
        var tone = new Tone(1000, 0.5f, 0.125f);
        var analyzer = new LiveAnalyzer();
        const int bands = 60;
        // the same scale as the equalizer's: ~22 Hz .. ~22.6 kHz
        double low = 31.25 / Math.Sqrt(2), high = 16000 * Math.Sqrt(2);
        for (var i = 0; i < 30; i++)
        {
            tone.Play(Rate / 60);
            analyzer.Update(tone.Processor.Scope, bands, low, high, 1 / 60.0);
        }

        var toneBand = (int)(Math.Log(1000 / low) / Math.Log(high / low) * bands);
        var loudest = Array.IndexOf(analyzer.Levels, analyzer.Levels.Max());
        Assert.InRange(loudest, toneBand - 1, toneBand + 1);
        Assert.True(analyzer.Levels[toneBand] > 0.85f);   // -6 dB on a 72 dB scale
        Assert.True(analyzer.Levels[5] < 0.3f);            // far below: next to nothing

        Assert.InRange(analyzer.PeakDb[0], -6.5, -5.5);
        Assert.InRange(analyzer.RmsDb[0], -6.5, -5.5);
        Assert.InRange(analyzer.PeakDb[1], -18.5, -17.5);
        Assert.False(analyzer.Clipped[0]);
        Assert.False(analyzer.IsQuiet);
    }

    [Fact]
    public void Without_sound_everything_sinks_to_nothing()
    {
        var tone = new Tone(440, 0.8f, 0.8f);
        var analyzer = new LiveAnalyzer();
        tone.Play(Rate / 2);
        analyzer.Update(tone.Processor.Scope, 40, 22, 22000, 1 / 60.0);
        Assert.False(analyzer.IsQuiet);
        // the scope stops moving (paused): it's silence from then on
        for (var i = 0; i < 60 * 6; i++) analyzer.Update(tone.Processor.Scope, 40, 22, 22000, 1 / 60.0);
        Assert.True(analyzer.IsQuiet);
        Assert.All(analyzer.Levels, level => Assert.Equal(0, level));
    }

    [Fact]
    public void Reads_what_is_being_heard_not_what_was_just_written()
    {
        var scope = new ToneProcessor(Rate).Scope;
        Assert.Equal(0, scope.HeardFrames);
        var tone = new Tone(440, 0.5f, 0.5f);
        tone.Play(4096);
        tone.Processor.Scope.DelayFrames = 1000;
        Assert.Equal(4096 - 1000, tone.Processor.Scope.HeardFrames);
        var copy = new float[200];
        Assert.Equal(4096 - 1000, tone.Processor.Scope.Read(copy));
    }
}
