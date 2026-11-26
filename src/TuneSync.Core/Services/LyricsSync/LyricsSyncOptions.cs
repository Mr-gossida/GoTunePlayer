using TuneSync.Core.Models;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>What "싱크 가사 만들기" may use of the PC.</summary>
/// <param name="UseGpu">Listen (Whisper, through Vulkan) and separate the vocals (DirectML) on
/// the graphics card when there's one that works - with the CPU alongside when it's built into
/// the CPU (see <see cref="WorkShare"/>); otherwise, or when false, on the CPU.</param>
/// <param name="Threads">CPU threads for whatever runs on the CPU.</param>
public sealed record LyricsSyncOptions(bool UseGpu, int Threads)
{
    public static LyricsSyncOptions Default { get; } = From(true, LyricsSyncCpuUsage.Normal);

    public static LyricsSyncOptions From(AppSettings settings) => From(settings.LyricsSyncUseGpu, settings.LyricsSyncCpuUsage);

    public static LyricsSyncOptions From(bool useGpu, LyricsSyncCpuUsage cpu) => new(useGpu, ThreadsFor(cpu, Environment.ProcessorCount));

    /// <summary>Threads for a CPU usage level. Whisper on the CPU runs about as fast on one thread
    /// per core as on every logical processor (an 8-core, 16-thread PC: 37 s on 8 threads, 34 s
    /// on 15), so "보통" takes half the logical processors - about half the CPU - and "최대" all
    /// but one.</summary>
    public static int ThreadsFor(LyricsSyncCpuUsage cpu, int processors) => cpu switch
    {
        LyricsSyncCpuUsage.Low => Math.Max(1, processors / 4),
        LyricsSyncCpuUsage.Max => Math.Max(1, processors - 1),
        _ => Math.Max(1, processors / 2),
    };
}
