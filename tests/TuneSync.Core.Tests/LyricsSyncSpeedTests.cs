using TuneSync.Core.Models;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

public class LyricsSyncSpeedTests
{
    [Fact]
    public void Before_any_song_a_rough_figure_stands_in()
    {
        var settings = new AppSettings();
        var (rate, measured) = LyricsSyncSpeed.Rate(settings);
        Assert.False(measured);
        Assert.Equal(LyricsSyncSpeed.Default(true, LyricsSyncCpuUsage.Normal), rate);
    }

    [Fact]
    public void The_first_song_moves_the_speed_halfway_from_the_rough_figure()
    {
        var settings = new AppSettings();
        var rough = LyricsSyncSpeed.Default(true, LyricsSyncCpuUsage.Normal);
        Assert.True(LyricsSyncSpeed.Record(settings, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(4 * 25)));

        Assert.Equal(((rough + 25) / 2, true), LyricsSyncSpeed.Rate(settings));
        // only for the setting it ran with
        settings.LyricsSyncUseGpu = false;
        Assert.False(LyricsSyncSpeed.Rate(settings).Measured);
    }

    [Fact]
    public void Later_songs_move_the_speed_part_of_the_way()
    {
        var settings = new AppSettings();
        settings.LyricsSyncSecondsPerMinute[LyricsSyncSpeed.Key(settings)] = 40;
        LyricsSyncSpeed.Record(settings, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60));
        Assert.Equal(48, LyricsSyncSpeed.Rate(settings).SecondsPerMinute, 6);
    }

    [Fact]
    public void A_scrap_of_a_song_tells_nothing()
    {
        var settings = new AppSettings();
        Assert.False(LyricsSyncSpeed.Record(settings, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        Assert.False(LyricsSyncSpeed.Rate(settings).Measured);
    }

    [Fact]
    public void The_estimate_follows_the_song_length()
    {
        var settings = new AppSettings();
        settings.LyricsSyncSecondsPerMinute[LyricsSyncSpeed.Key(settings)] = 30;
        Assert.Equal(TimeSpan.FromSeconds(120), LyricsSyncSpeed.Estimate(settings, TimeSpan.FromMinutes(4)));
        // unknown length: a typical song
        Assert.Equal(TimeSpan.FromSeconds(30 * LyricsSyncSpeed.TypicalSong.TotalMinutes), LyricsSyncSpeed.Estimate(settings, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(4, "약 10초")]
    [InlineData(44, "약 40초")]
    [InlineData(137, "약 2분 20초")]
    [InlineData(120, "약 2분")]
    [InlineData(1500, "약 25분")]
    [InlineData(3600, "약 1시간")]
    [InlineData(4300, "약 1시간 10분")]
    public void Times_read_the_way_a_person_says_them(double seconds, string expected) =>
        Assert.Equal(expected, LyricsSyncSpeed.Describe(TimeSpan.FromSeconds(seconds)));
}
