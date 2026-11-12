namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Plain lyrics in, timed lines out: decode the song, let Whisper listen to it (biased
/// toward the lyrics' own words), then line the lyrics up with what it heard.</summary>
public sealed class LyricsSyncEngine : IDisposable
{
    private readonly WhisperTranscriber _transcriber = new(WhisperModelStore.ModelPath);

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
        var heard = await Task.Run(() => _transcriber.TranscribeAsync(samples, LanguageOf(lines), Prompt(lines),
            new Progress<int>(p => progress?.Report((Stage.Listening, p / 100.0))), ct), ct);

        progress?.Report((Stage.Aligning, 0));
        return LyricsAligner.Align(lines, heard, duration);
    }

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
