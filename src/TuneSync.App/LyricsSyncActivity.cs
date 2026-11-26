namespace TuneSync.App;

/// <summary>Keeps "싱크 가사 만들기" to one at a time: a single song's 자동으로 맞추기 and a batch
/// running together would fight over the graphics card and the CPU, each taking about twice as
/// long. Used from the UI thread only.</summary>
public static class LyricsSyncActivity
{
    private static int _single;

    /// <summary>The batch window open now (running or not), if any.</summary>
    public static BatchSyncWindow? Batch { get; set; }

    /// <summary>A batch is working through its songs.</summary>
    public static bool BatchRunning => Batch?.ViewModel.IsRunning == true;

    /// <summary>A single song's 자동으로 맞추기 is running.</summary>
    public static bool SingleRunning => _single > 0;

    public static void SingleStarted() => _single++;

    public static void SingleEnded() => _single = Math.Max(0, _single - 1);
}
