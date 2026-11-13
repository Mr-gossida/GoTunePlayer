namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Plain lyrics in, timed lines out: decode the song, let Whisper listen to it, then line
/// the lyrics up with what it heard.
///
/// It listens first without the lyrics as a hint: given them, Whisper tends to write lyric lines
/// where it isn't sure, at the wrong times. On some songs, though (fast or heavily produced ones),
/// it recognizes little without the hint - then it listens again with the lyrics as its prompt,
/// and the pass that placed more lines wins. (On songs with known timings, tools/SyncBench: lines
/// within 1 s went from 49% to about 60%, lines off by over 3 s from 35% to under 20%.)</summary>
public sealed class LyricsSyncEngine : IDisposable
{
    private readonly WhisperTranscriber _transcriber;

    public LyricsSyncEngine() : this(WhisperModelStore.ModelPath) { }

    public LyricsSyncEngine(string modelPath) => _transcriber = new WhisperTranscriber(modelPath);

    public enum Stage { Decoding, Listening, Aligning }

    /// <param name="progress">Stage and 0..1 within it.</param>
    public async Task<List<AlignedLine>> SyncAsync(string audioPath, string plainLyrics,
        IProgress<(Stage Stage, double Value)>? progress, CancellationToken ct)
    {
        var lines = LrcWriter.PlainLines(plainLyrics);
        if (lines.Count == 0) return [];

        progress?.Report((Stage.Decoding, 0));
        var samples = await Task.Run(() => AudioDecoder.ReadMono16k(audioPath, ct), ct);
        var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioDecoder.SampleRate);

        progress?.Report((Stage.Listening, 0));
        var language = LanguageOf(lines);
        var free = await Task.Run(() => _transcriber.TranscribeAsync(samples, language, null,
            new Progress<int>(p => progress?.Report((Stage.Listening, p / 200.0))), ct), ct);
        var result = LyricsAligner.Align(lines, free, duration);
        if (Placed(result) >= GoodEnough) return result;

        var hinted = await Task.Run(() => _transcriber.TranscribeAsync(samples, language, Prompt(lines),
            new Progress<int>(p => progress?.Report((Stage.Listening, 0.5 + p / 200.0))), ct), ct);
        progress?.Report((Stage.Aligning, 0));
        var second = LyricsAligner.Align(lines, hinted, duration);
        return Placed(second) > Placed(result) ? second : result;
    }

    /// <summary>Share of lines whose time was heard rather than estimated.</summary>
    private static double Placed(List<AlignedLine> lines) =>
        lines.Count == 0 ? 0 : lines.Count(l => l.IsConfident) / (double)lines.Count;

    /// <summary>Above this share placed, the first pass is kept without a second listen.</summary>
    private const double GoodEnough = 0.85;

    /// <summary>Korean when the lyrics are mostly Hangul, English when mostly Latin letters,
    /// otherwise let Whisper detect it.</summary>
    public static string? LanguageOf(IReadOnlyList<string> lines)
    {
        int hangul = 0, latin = 0, kana = 0, letters = 0;
        foreach (var c in string.Concat(lines))
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c is >= '가' and <= '힣') hangul++;
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
            else if (c is >= '぀' and <= 'ヿ') kana++;
        }
        if (letters == 0) return null;
        if (hangul >= letters * 0.25) return "ko";
        if (kana >= letters * 0.25) return "ja";
        if (latin >= letters * 0.8) return "en";
        return null;
    }

    /// <summary>The start of the lyrics as Whisper's prompt (it reads ~200 tokens of it), so it
    /// writes the words the way the lyrics do.</summary>
    public static string Prompt(IReadOnlyList<string> lines)
    {
        var prompt = string.Join(" ", lines.Distinct());
        return prompt.Length <= 300 ? prompt : prompt[..300];
    }

    public void Dispose() => _transcriber.Dispose();
}
