using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class AudioQualityTests
{
    private static string Asset(string fileName) => Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);

    [Fact]
    public void Flac_IsLossless_WithBitDepthAndSampleRate()
    {
        var quality = TagService.ReadAudioQuality(Asset("sample.flac"));

        Assert.NotNull(quality);
        Assert.Equal("FLAC", quality.Format);
        Assert.True(quality.IsLossless);
        Assert.True(quality.BitsPerSample > 0);
        Assert.True(quality.SampleRate > 0);
        Assert.StartsWith("FLAC · ", quality.Summary);
    }

    [Fact]
    public void Mp3_IsLossy_WithBitrate()
    {
        var quality = TagService.ReadAudioQuality(Asset("sample.mp3"));

        Assert.NotNull(quality);
        Assert.Equal("MP3", quality.Format);
        Assert.False(quality.IsLossless);
        Assert.Null(quality.Badge);
        Assert.True(quality.BitrateKbps > 0);
    }

    [Theory]
    [InlineData(24, 96000, "Hi-Res")]
    [InlineData(16, 44100, "Lossless")]
    [InlineData(16, 96000, "Hi-Res")]
    public void LosslessBadge_FollowsCdQualityLine(int bits, int rate, string expected)
    {
        var quality = new AudioQuality("FLAC", 1411, rate, bits, 2, IsLossless: true);
        Assert.Equal(expected, quality.Badge);
    }

    [Fact]
    public void Summary_ReadsLikeAStreamingServiceLabel()
    {
        Assert.Equal("FLAC · 24bit · 96kHz · 2,304kbps", new AudioQuality("FLAC", 2304, 96000, 24, 2, true).Summary);
        Assert.Equal("MP3 · 44.1kHz · 320kbps", new AudioQuality("MP3", 320, 44100, 0, 2, false).Summary);
    }
}
