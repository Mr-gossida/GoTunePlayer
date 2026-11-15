namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Plain lyrics in, timed lines out: decode the song, let Whisper listen to it, then line
/// the lyrics up with what it heard.
///
/// The singing is first taken out of the song (<see cref="VocalSeparator"/>), which shows where
/// the singer pauses: the song is then listened to in pieces that start where singing starts
/// (<see cref="VoiceActivity.Windows"/>), not in fixed 30 s pieces that cut through words and
/// leave Whisper guessing where in a long pause the next phrase begins.
///
/// It listens up to three times, until one pass places enough lines, and the pass that placed
/// the most wins: the song as it is; the vocals alone (better on some songs, worse on others);
/// the song again with the lyrics as Whisper's prompt (given them it recognizes more on fast or
/// heavily produced songs, but it also writes lyric lines where it isn't sure, at wrong times -
/// so that comes last).
///
/// On ten songs with known timings (tools/SyncBench, 378 lines): lines within 1 s went from 61%
/// to 71%, lines off by over 3 s from 19% to 9%. Without the separation model it falls back to
/// fixed pieces and two passes.</summary>
public sealed class LyricsSyncEngine : IDisposable
{
    private readonly WhisperTranscriber _transcriber;
    private readonly string? _vocalModelPath;
    private VocalSeparator? _separator;

    public LyricsSyncEngine() : this(WhisperModelStore.ModelPath, VocalModelStore.IsDownloaded ? VocalModelStore.ModelPath : null) { }

    /// <param name="vocalModelPath">The vocal separation model, or null to do without.</param>
    public LyricsSyncEngine(string modelPath, string? vocalModelPath)
    {
        _transcriber = new WhisperTranscriber(modelPath);
        _vocalModelPath = vocalModelPath;
    }

    public enum Stage { Decoding, Separating, Listening, Aligning }

    /// <summary>What a pass listens to.</summary>
    public enum Source { Song, Vocals }

    /// <summary>One listen: to what, and whether with the lyrics as Whisper's prompt.</summary>
    public readonly record struct Pass(Source Source, bool Hinted);

    /// <summary>The passes in the order they're tried.</summary>
    public static IReadOnlyList<Pass> Passes(bool separated) => separated
        ? [new(Source.Song, false), new(Source.Vocals, false), new(Source.Song, true)]
        : [new(Source.Song, false), new(Source.Song, true)];

    /// <param name="progress">Stage and 0..1 within it.</param>
    public async Task<List<AlignedLine>> SyncAsync(string audioPath, string plainLyrics,
        IProgress<(Stage Stage, double Value)>? progress, CancellationToken ct)
    {
        var lines = LrcWriter.PlainLines(plainLyrics);
        if (lines.Count == 0) return [];

        progress?.Report((Stage.Decoding, 0));
        var song = await Task.Run(() => AudioDecoder.ReadMono16k(audioPath, ct), ct);
        var duration = TimeSpan.FromSeconds(song.Length / (double)AudioDecoder.SampleRate);

        float[]? vocals = null;
        if (_vocalModelPath is not null)
        {
            progress?.Report((Stage.Separating, 0));
            vocals = await Task.Run(() => SeparateVocals(audioPath,
                new Progress<double>(p => progress?.Report((Stage.Separating, p))), ct), ct);
        }
        var windows = vocals is null ? null : VoiceActivity.Detect(vocals).Windows();

        progress?.Report((Stage.Listening, 0));
        var language = LanguageOf(lines);
        var passes = Passes(vocals is not null);
        var done = 0;
        return await SyncAsync(lines, duration, passes, pass =>
        {
            var from = done++ / (double)passes.Count;
            return Task.Run(() => _transcriber.TranscribeAsync(pass.Source == Source.Vocals ? vocals! : song, language,
                pass.Hinted ? Prompt(lines) : null,
                new Progress<int>(p => progress?.Report((Stage.Listening, from + p / 100.0 / passes.Count))), ct, windows), ct);
        });
    }

    /// <summary>The singing alone, at the 16 kHz speech recognition expects.</summary>
    public float[] SeparateVocals(string audioPath, IProgress<double>? progress, CancellationToken ct)
    {
        _separator ??= new VocalSeparator(_vocalModelPath!);
        var (left, right) = AudioDecoder.ReadStereo(audioPath, VocalSeparator.SampleRate, ct);
        var vocals = _separator.Separate(left, right, progress, ct);
        return AudioDecoder.Resample(vocals, VocalSeparator.SampleRate, AudioDecoder.SampleRate);
    }

    /// <summary>The decision between passes, apart from how the listening is done (tools/SyncBench
    /// keeps what was heard between runs, so aligner changes can be measured in seconds).</summary>
    public static async Task<List<AlignedLine>> SyncAsync(IReadOnlyList<string> lines, TimeSpan duration,
        IReadOnlyList<Pass> passes, Func<Pass, Task<List<TimedText>>> listen)
    {
        List<AlignedLine> best = [];
        foreach (var pass in passes)
        {
            var result = LyricsAligner.Align(lines, await listen(pass), duration);
            if (best.Count == 0 || Placed(result) > Placed(best)) best = result;
            if (Placed(best) >= GoodEnough) break;
        }
        return best;
    }

    /// <summary>Share of lines whose time was heard rather than estimated.</summary>
    private static double Placed(List<AlignedLine> lines) =>
        lines.Count == 0 ? 0 : lines.Count(l => l.IsConfident) / (double)lines.Count;

    /// <summary>Above this share placed, no further pass is listened to.</summary>
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

    public void Dispose()
    {
        _transcriber.Dispose();
        _separator?.Dispose();
    }
}
