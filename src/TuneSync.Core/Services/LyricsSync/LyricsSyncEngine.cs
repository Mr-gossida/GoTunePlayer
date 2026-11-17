namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Plain lyrics in, timed lines out: decode the song, let Whisper listen to it, then line
/// the lyrics up with what it heard.
///
/// The singing is first taken out of the song (<see cref="VocalSeparator"/>), which shows where
/// the singer pauses: the song is then listened to in pieces that start where singing starts
/// (<see cref="VoiceActivity.Windows"/>), not in fixed 30 s pieces that cut through words and
/// leave Whisper guessing where in a long pause the next phrase begins.
///
/// It listens to the vocals alone and to the song as it is (each is better on some songs), and
/// where those two disagree on too many lines, a third time to the song with the lyrics as
/// Whisper's prompt (given them it recognizes more on fast or heavily produced songs, but it
/// also writes lyric lines where it isn't sure, at wrong times). Each line then gets the time
/// most passes agree on. Which single pass is best for a song can't be told from how many lines
/// it placed - a pass can place lines confidently at wrong times - but two passes rarely make
/// the same mistake on the same line. The lines no pass heard are then spread again between the
/// lines the passes settled on, over the singing (see <see cref="LyricsAligner.Refill"/>).
///
/// On ten songs with known timings (tools/SyncBench, 378 lines): lines within 1 s went from 61%
/// to 69%, lines off by over 3 s from 19% to 12%. Without the separation model it falls back
/// to fixed pieces and up to two passes, of which the one that placed more lines wins.</summary>
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

    /// <summary>The passes in the order they're listened to (and, where they disagree with no
    /// majority, trusted).</summary>
    public static IReadOnlyList<Pass> Passes(bool separated) => separated
        ? [new(Source.Vocals, false), new(Source.Song, false), new(Source.Song, true)]
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
        var (windows, voice) = UseVocals(vocals);

        progress?.Report((Stage.Listening, 0));
        var language = LanguageOf(lines);
        var passes = Passes(vocals is not null);
        var done = 0;
        var aligned = await SyncAsync(lines, duration, voice, passes, pass =>
        {
            var from = done++ / (double)passes.Count;
            return Task.Run(() => _transcriber.TranscribeAsync(pass.Source == Source.Vocals ? vocals! : song, language,
                pass.Hinted ? Prompt(lines) : null,
                new Progress<int>(p => progress?.Report((Stage.Listening, from + p / 100.0 / passes.Count))), ct, windows), ct);
        });
        // The last pass is skipped when the first ones already agree: finish the bar anyway.
        progress?.Report((Stage.Aligning, 1));
        return aligned;
    }

    /// <summary>What the vocals are used for: the pieces to listen to, cut at the singer's pauses
    /// (found generously - a breath shouldn't end a piece), and the singing clock that unheard
    /// lines are spread over (found strictly - accompaniment left in the vocals mustn't make an
    /// instrumental count as singing; on songs with known timings, a quarter fewer lines off by
    /// over 3 s than with the generous one).</summary>
    public static (List<(double Start, double End)>? Windows, VoiceActivity? Voice) UseVocals(float[]? vocals) =>
        vocals is null ? (null, null) : (VoiceActivity.Detect(vocals).Windows(), VoiceActivity.Detect(vocals, threshold: 0.2));

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
        VoiceActivity? voice, IReadOnlyList<Pass> passes, Func<Pass, Task<List<TimedText>>> listen)
    {
        var separated = passes.Any(p => p.Source == Source.Vocals);
        var results = new List<List<AlignedLine>>();
        foreach (var pass in passes)
        {
            results.Add(LyricsAligner.Align(lines, await listen(pass), duration, voice));
            if (separated ? results.Count == 2 && Agreement(results[0], results[1]) >= AgreeEnough
                          : Placed(results[^1]) >= PlacedEnough) break;
        }
        if (!separated) return results.MaxBy(Placed)!;
        var heard = Enumerable.Range(0, lines.Count).Select(i => results.Any(r => r[i].IsConfident)).ToArray();
        return LyricsAligner.Refill(Combine(results), duration, voice, heard);
    }

    /// <summary>Each line's time from several passes: the time the largest group of passes that
    /// heard the line agree on (their median); with no two agreeing, the earliest pass that heard
    /// it - marked to check when passes heard it at different times.</summary>
    public static List<AlignedLine> Combine(IReadOnlyList<List<AlignedLine>> passes)
    {
        var combined = new List<AlignedLine>(passes[0].Count);
        for (var i = 0; i < passes[0].Count; i++)
        {
            var heard = passes.Where(p => p[i].IsConfident).Select(p => p[i].Time.TotalSeconds).ToList();
            if (heard.Count == 0) { combined.Add(passes[0][i]); continue; } // an estimate
            var group = heard.Select(t => heard.Where(u => Math.Abs(u - t) <= AgreeSeconds).ToList()).MaxBy(g => g.Count)!;
            var time = heard[0];
            if (group.Count > 1)
            {
                group.Sort();
                time = group.Count % 2 == 1 ? group[group.Count / 2] : (group[group.Count / 2 - 1] + group[group.Count / 2]) / 2;
            }
            combined.Add(new AlignedLine(passes[0][i].Text, TimeSpan.FromSeconds(time), group.Count > 1 || heard.Count == 1));
        }
        return combined;
    }

    /// <summary>Share of lines two passes both heard, at the same time.</summary>
    private static double Agreement(List<AlignedLine> a, List<AlignedLine> b) =>
        a.Count == 0 ? 0 : Enumerable.Range(0, a.Count).Count(i => a[i].IsConfident && b[i].IsConfident
            && Math.Abs((a[i].Time - b[i].Time).TotalSeconds) <= AgreeSeconds) / (double)a.Count;

    /// <summary>Share of lines whose time was heard rather than estimated.</summary>
    private static double Placed(List<AlignedLine> lines) =>
        lines.Count == 0 ? 0 : lines.Count(l => l.IsConfident) / (double)lines.Count;

    /// <summary>Two passes' times for a line this close count as the same.</summary>
    private const double AgreeSeconds = 1.0;

    /// <summary>With this share of lines agreed on by the first two passes, the third isn't needed.</summary>
    private const double AgreeEnough = 0.8;

    /// <summary>Without separation: above this share placed, the first pass is kept without a second listen.</summary>
    private const double PlacedEnough = 0.85;

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
