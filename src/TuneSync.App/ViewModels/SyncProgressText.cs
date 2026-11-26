using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.App.ViewModels;

/// <summary>What a sync's progress reads as on screen, the same in the single-song editor and
/// the batch window: the step it's on (0-based, of <see cref="StepNames"/>), the status line,
/// the step's own 0..1 and percentage, and the device badge ("GPU", "CPU", "GPU + CPU", or empty
/// while that isn't known).</summary>
public static class SyncProgressText
{
    public readonly record struct Shown(int Step, string Status, double Value, string Percent, string Device);

    /// <summary>The steps as chips: with the vocal separation model, three.</summary>
    public static string[] StepNames(bool separates) => separates ? ["목소리 분리", "노래 듣기", "줄 맞추기"] : ["노래 듣기", "줄 맞추기"];

    public static Shown Describe(LyricsSyncEngine.SyncProgress p, LyricsSyncEngine? engine, int stepCount)
    {
        var listening = stepCount == 3 ? 1 : 0;
        switch (p.Stage)
        {
            case LyricsSyncEngine.Stage.Decoding:
                // Reading the song comes before the first step proper.
                return new(0, "노래를 읽는 중…", 0, "", "");
            case LyricsSyncEngine.Stage.Separating:
                return new(0, "반주를 빼고 목소리만 남기는 중…", p.Value, Percent(p.Value), engine?.SeparationDevice ?? "");
            case LyricsSyncEngine.Stage.Listening:
                // Until whisper.cpp says which device it took, it's still loading the model (on
                // the graphics card that can take several seconds the first time).
                var device = LyricsSyncEngine.ListeningDevice;
                var badge = device switch { null => "", "CPU" => "CPU", _ => LyricsSyncEngine.ListeningWithCpu ? "GPU + CPU" : "GPU" };
                return device is null
                    ? new(listening, $"{PassName(p)} · 음성 인식 준비 중…", 0, "", badge)
                    : new(listening, PassName(p), p.Value, Percent(p.Value), badge);
            default:
                return new(stepCount - 1, "들은 내용과 가사 줄을 맞추는 중…", 1, "", "");
        }
    }

    public static string Percent(double value) => $"{(int)(value * 100)}%";

    /// <summary>"첫 번째 듣기 · 목소리만" and so on: a third listen only happens when the first two
    /// disagree, so it's "한 번 더", not "세 번째" of a count that was never announced.
    /// Then the stretches no listen heard: "놓친 부분 다시 듣기 · 2번째 구간".</summary>
    private static string PassName(LyricsSyncEngine.SyncProgress p)
    {
        if (p.Relistening) return $"놓친 부분 다시 듣기 · {p.PassIndex + 1}번째 구간";
        var which = p.PassIndex switch { 0 => "첫 번째 듣기", 1 => "두 번째 듣기", _ => "한 번 더 듣기" };
        var what = p.Listen.Source == LyricsSyncEngine.Source.Vocals ? "목소리만" : p.Listen.Hinted ? "가사를 참고해서" : "원곡";
        return $"{which} · {what}";
    }
}
