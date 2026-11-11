using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class WaveformAnalyzerTests
{
    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void Analyze_ProducesOneLevelPerSliceOfTheTrack(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);
        var duration = TagService.ReadDuration(path).TotalSeconds;

        var waveform = WaveformAnalyzer.Analyze(path);

        Assert.NotNull(waveform);
        var expectedSlices = duration / WaveformAnalyzer.BucketSeconds;
        Assert.InRange(waveform.Levels.Length, expectedSlices * 0.8, expectedSlices * 1.2 + 2);
        Assert.All(waveform.Levels, level => Assert.InRange(level, 0f, 1f));
        Assert.True(waveform.Levels.Max() > 0.5f, "the sample is an audible tone, so it should reach the loud end");
    }

    [Fact]
    public void Analyze_ReturnsNullForUnreadableFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp3");
        File.WriteAllText(path, "not audio");
        try
        {
            Assert.Null(WaveformAnalyzer.Analyze(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LevelAt_InterpolatesBetweenSlices()
    {
        var waveform = new WaveformData([0f, 1f, 0.2f, 0.6f], 0.05);

        Assert.Equal(0.5f, waveform.LevelAt(0.025), 3);
        Assert.Equal(0.4f, waveform.LevelAt(0.125), 3); // halfway between 0.2 and 0.6
        Assert.Equal(0f, waveform.LevelAt(10)); // past the end of the track: silence
        Assert.Equal(0f, waveform.LevelAt(-1));
        Assert.Equal(0.2, waveform.Duration, 6);
    }
}
