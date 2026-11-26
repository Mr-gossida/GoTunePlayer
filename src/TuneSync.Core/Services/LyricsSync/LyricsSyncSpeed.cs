using TuneSync.Core.Models;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>How long "싱크 가사 만들기" takes on this PC, so a batch of songs can say up front
/// how long it will run. Every sync records its speed - seconds of work per minute of song,
/// which is about how the time grows - for the device setting it ran with (the graphics card or
/// the CPU only, and how much of the CPU), smoothed over the last few songs.
///
/// Until a song has been synced with a setting, a rough figure stands in: measured on a Core
/// Ultra 5 225 with its Intel graphics (tools/SyncBench, 6 songs), about 41 s per minute of song
/// with the graphics and the CPU sharing the work; a separate graphics card is faster, so with
/// the graphics allowed that's on the safe side. CPU only, from the same PC's CPU share of it.</summary>
public static class LyricsSyncSpeed
{
    /// <summary>How much a new song counts against what was measured before (the rest).</summary>
    private const double NewWeight = 0.4;

    public static string Key(bool useGpu, LyricsSyncCpuUsage cpu) => $"{(useGpu ? "gpu" : "cpu")}-{cpu.ToString().ToLowerInvariant()}";

    public static string Key(AppSettings settings) => Key(settings.LyricsSyncUseGpu, settings.LyricsSyncCpuUsage);

    /// <summary>Seconds per minute of song before anything was measured.</summary>
    public static double Default(bool useGpu, LyricsSyncCpuUsage cpu) => (useGpu, cpu) switch
    {
        (true, LyricsSyncCpuUsage.Low) => 60,
        (true, _) => 45,
        (false, LyricsSyncCpuUsage.Low) => 120,
        (false, LyricsSyncCpuUsage.Max) => 55,
        (false, _) => 65,
    };

    /// <summary>Seconds per minute of song with the settings' devices, and whether that was
    /// measured on this PC (false: the rough figure above).</summary>
    public static (double SecondsPerMinute, bool Measured) Rate(AppSettings settings) =>
        settings.LyricsSyncSecondsPerMinute.TryGetValue(Key(settings), out var rate) && rate > 0
            ? (rate, true)
            : (Default(settings.LyricsSyncUseGpu, settings.LyricsSyncCpuUsage), false);

    /// <summary>How long a song this long should take. A song of unknown length counts as
    /// <see cref="TypicalSong"/>.</summary>
    public static TimeSpan Estimate(AppSettings settings, TimeSpan duration) =>
        TimeSpan.FromSeconds(Rate(settings).SecondsPerMinute * Minutes(duration));

    /// <summary>The length a song of unknown length is taken to have.</summary>
    public static readonly TimeSpan TypicalSong = TimeSpan.FromMinutes(3.5);

    private static double Minutes(TimeSpan duration) => (duration > TimeSpan.Zero ? duration : TypicalSong).TotalMinutes;

    /// <summary>Records that a song this long took <paramref name="took"/> (the work itself, not
    /// a download before it). Returns false when it's too short to tell anything.</summary>
    public static bool Record(AppSettings settings, TimeSpan duration, TimeSpan took)
    {
        if (duration < TimeSpan.FromSeconds(20) || took <= TimeSpan.Zero) return false;
        var rate = took.TotalSeconds / duration.TotalMinutes;
        var key = Key(settings);
        // The first song only halfway from the rough figure: songs differ a lot (a song whose
        // first two listens agree skips the third; one with many unheard lines is listened to
        // again in parts), and one of them is a poor guide for the next.
        var before = settings.LyricsSyncSecondsPerMinute.TryGetValue(key, out var measured) && measured > 0
            ? (Rate: measured, Weight: NewWeight)
            : (Rate: Default(settings.LyricsSyncUseGpu, settings.LyricsSyncCpuUsage), Weight: 0.5);
        settings.LyricsSyncSecondsPerMinute[key] = before.Rate * (1 - before.Weight) + rate * before.Weight;
        return true;
    }

    /// <summary>"약 2분 20초", "약 45초", "약 1시간 10분": rounded the way a person would say it.</summary>
    public static string Describe(TimeSpan time)
    {
        var seconds = time.TotalSeconds;
        if (seconds < 60) return $"약 {Math.Max(10, Math.Round(seconds / 10) * 10):0}초";
        if (seconds < 600)
        {
            // under ten minutes: to the nearest 10 s
            var rounded = (int)(Math.Round(seconds / 10) * 10);
            return rounded % 60 == 0 ? $"약 {rounded / 60}분" : $"약 {rounded / 60}분 {rounded % 60}초";
        }
        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 60) return $"약 {minutes}분";
        // an hour or more: to the nearest 5 minutes
        minutes = (int)(Math.Round(minutes / 5.0) * 5);
        return minutes % 60 == 0 ? $"약 {minutes / 60}시간" : $"약 {minutes / 60}시간 {minutes % 60}분";
    }
}
