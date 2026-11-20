using Microsoft.ML.OnnxRuntime;
using Whisper.net;

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
    private readonly string _modelPath;
    private readonly string? _vocalModelPath;
    private readonly LyricsSyncOptions _options;
    private VocalSeparator? _separator;
    private volatile bool _disposed;

    public LyricsSyncEngine() : this(LyricsSyncOptions.Default) { }

    public LyricsSyncEngine(LyricsSyncOptions options)
        : this(WhisperModelStore.ModelPath, VocalModelStore.IsDownloaded ? VocalModelStore.ModelPath : null, options) { }

    /// <param name="vocalModelPath">The vocal separation model, or null to do without.</param>
    public LyricsSyncEngine(string modelPath, string? vocalModelPath, LyricsSyncOptions? options = null)
    {
        _options = options ?? LyricsSyncOptions.Default;
        _transcriber = new WhisperTranscriber(modelPath, _options);
        _modelPath = modelPath;
        _vocalModelPath = vocalModelPath;
    }

    /// <summary>Where the listening runs, for the status line: the graphics card's name, "CPU",
    /// or null until it has begun.</summary>
    public static string? ListeningDevice => WhisperTranscriber.Device;

    /// <summary>Whether the vocal separation runs on the graphics card, or null until the model
    /// has been opened (the first separation).</summary>
    public bool? SeparatesOnGpu => _separator?.UsesGpu;

    public enum Stage { Decoding, Separating, Listening, Aligning }

    /// <summary>Where a sync is: the stage and 0..1 within it. While listening, that's within the
    /// current pass (<paramref name="PassIndex"/>, 0-based; <paramref name="Listen"/> is what it
    /// listens to) - how many passes there will be is only known once the first two are compared.
    /// <paramref name="Relistening"/>: listening again to a stretch no pass heard (PassIndex
    /// counts those stretches).</summary>
    public readonly record struct SyncProgress(Stage Stage, double Value, int PassIndex = 0, Pass Listen = default, bool Relistening = false);

    /// <summary>True when the singing is separated first (the vocal separation model is there).</summary>
    public bool Separates => _vocalModelPath is not null;

    /// <summary>What a pass listens to.</summary>
    public enum Source { Song, Vocals }

    /// <summary>One listen: to what, and whether with the lyrics as Whisper's prompt.</summary>
    public readonly record struct Pass(Source Source, bool Hinted);

    /// <summary>The passes in the order they're listened to (and, where they disagree with no
    /// majority, trusted).</summary>
    public static IReadOnlyList<Pass> Passes(bool separated) => separated
        ? [new(Source.Vocals, false), new(Source.Song, false), new(Source.Song, true)]
        : [new(Source.Song, false), new(Source.Song, true)];

    public async Task<List<AlignedLine>> SyncAsync(string audioPath, string plainLyrics,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var lines = LrcWriter.PlainLines(plainLyrics);
        if (lines.Count == 0) return [];

        progress?.Report(new(Stage.Decoding, 0));
        var song = await Task.Run(() => AudioDecoder.ReadMono16k(audioPath, ct), ct);
        var duration = TimeSpan.FromSeconds(song.Length / (double)AudioDecoder.SampleRate);

        float[]? vocals = null;
        if (_vocalModelPath is not null)
        {
            progress?.Report(new(Stage.Separating, 0));
            vocals = await Task.Run(() => SeparateVocals(audioPath,
                new Progress<double>(p => progress?.Report(new(Stage.Separating, p))), ct), ct);
        }
        var (windows, voice) = UseVocals(vocals);

        WhisperTranscriber.ForgetDevice();
        var language = LanguageOf(lines);
        var passes = Passes(vocals is not null);
        int done = 0, gaps = 0;
        List<AlignedLine> aligned;
        try
        {
            aligned = await SyncAsync(lines, duration, voice, passes, pass =>
            {
                var index = done++;
                progress?.Report(new(Stage.Listening, 0, index, pass));
                return Task.Run(() => _transcriber.TranscribeAsync(pass.Source == Source.Vocals ? vocals! : song, language,
                    pass.Hinted ? Prompt(lines) : null,
                    new Progress<int>(p => progress?.Report(new(Stage.Listening, p / 100.0, index, pass))), ct, windows), ct);
            }, gap =>
            {
                var index = gaps++;
                progress?.Report(new(Stage.Listening, 0, index, default, true));
                return Task.Run(() => _transcriber.TranscribeAsync(gap.Source == Source.Vocals ? vocals! : song, language, gap.Prompt,
                    new Progress<int>(p => progress?.Report(new(Stage.Listening, p / 100.0, index, default, true))), ct, Pieces(gap)), ct);
            });
        }
        catch (WhisperModelLoadException ex)
        {
            // whisper.cpp couldn't read the model even on the CPU: the file is damaged, though
            // the right size. Deleted, the next sync downloads it again.
            if (!WhisperModelStore.Discard(_modelPath)) throw;
            throw new InvalidDataException("음성 인식 모델 파일을 읽지 못해 지웠습니다. 자동으로 맞추기를 다시 누르면 새로 받습니다.", ex);
        }
        progress?.Report(new(Stage.Aligning, 1));
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_separator is null)
        {
            var separator = OpenSeparator();
            // Disposed while the model was opening: nothing else would free it.
            if (_disposed)
            {
                separator.Dispose();
                throw new ObjectDisposedException(nameof(LyricsSyncEngine));
            }
            _separator = separator;
        }
        var (left, right) = AudioDecoder.ReadStereo(audioPath, VocalSeparator.SampleRate, ct);
        var vocals = _separator.Separate(left, right, progress, ct);
        return AudioDecoder.Resample(vocals, VocalSeparator.SampleRate, AudioDecoder.SampleRate);
    }

    private VocalSeparator OpenSeparator()
    {
        try
        {
            return new VocalSeparator(_vocalModelPath!, _options.UseGpu, _options.Threads);
        }
        catch (OnnxRuntimeException ex)
        {
            // ONNX Runtime couldn't read the model even on the CPU (the graphics card is tried
            // first and given up quietly): the file is damaged, though the right size. Deleted,
            // the next sync downloads it again.
            if (!VocalModelStore.Discard(_vocalModelPath!)) throw;
            throw new InvalidDataException("보컬 분리 모델 파일을 읽지 못해 지웠습니다. 자동으로 맞추기를 다시 누르면 새로 받습니다.", ex);
        }
    }

    /// <summary>One more listen, to a stretch of the song where no pass heard the lines: with
    /// those lines (and the heard ones around them) as Whisper's prompt.</summary>
    public readonly record struct Gap(Source Source, string Prompt, double Start, double End);

    /// <summary>The decision between passes, apart from how the listening is done (tools/SyncBench
    /// keeps what was heard between runs, so aligner changes can be measured in seconds).</summary>
    /// <param name="relisten">Listens to a stretch again (see <see cref="Gap"/>), or null not to.</param>
    public static async Task<List<AlignedLine>> SyncAsync(IReadOnlyList<string> lines, TimeSpan duration,
        VoiceActivity? voice, IReadOnlyList<Pass> passes, Func<Pass, Task<List<TimedText>>> listen,
        Func<Gap, Task<List<TimedText>>>? relisten = null)
    {
        var separated = passes.Any(p => p.Source == Source.Vocals);
        var results = new List<List<AlignedLine>>();
        foreach (var pass in passes)
        {
            results.Add(LyricsAligner.Align(lines, await listen(pass), duration, voice));
            if (separated ? results.Count == 2 && Agreement(results[0], results[1]) >= AgreeEnough
                          : Placed(results[^1]) >= PlacedEnough) break;
        }
        if (!separated)
        {
            var best = results.MaxBy(Placed)!;
            return relisten is null ? best : await FillGapsAsync(lines, best, [.. best.Select(l => l.IsConfident)], duration, voice, Source.Song, relisten);
        }
        var heard = Enumerable.Range(0, lines.Count).Select(i => results.Any(r => r[i].IsConfident)).ToArray();
        var combined = LyricsAligner.Refill(Combine(results), duration, voice, heard);
        return relisten is null ? combined : await FillGapsAsync(lines, combined, heard, duration, voice, Source.Vocals, relisten);
    }


    /// <summary>The lines no pass heard, listened to again a stretch at a time - from the heard
    /// line before them to the heard line after - with just those lines as the prompt. Whisper
    /// given the whole song's opening lyrics, or none, loses its place in such stretches (a held
    /// note, a key change, a chant it took for a song title); told what comes there, it often
    /// hears it. A line heard this way keeps its time where it falls between its neighbors
    /// (still marked to check: Whisper given the words may write them where it isn't sure).
    /// The vocals are listened to (on the songs with known timings they did better than the
    /// song as it is: 91.4% of lines within 1 s against 91.0%).</summary>
    private static async Task<List<AlignedLine>> FillGapsAsync(IReadOnlyList<string> lines, List<AlignedLine> result,
        bool[] heard, TimeSpan duration, VoiceActivity? voice, Source source, Func<Gap, Task<List<TimedText>>> relisten)
    {
        var updated = result.ToList();
        var known = (bool[])heard.Clone();
        var found = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (updated[i].IsConfident) continue;
            var j = i;
            while (j + 1 < lines.Count && !updated[j + 1].IsConfident) j++;
            int before = i - 1, after = j + 1;
            var start = before >= 0 ? updated[before].Time.TotalSeconds : 0;
            var end = after < lines.Count ? updated[after].Time.TotalSeconds + 1 : duration.TotalSeconds;
            if (end - start >= 1 && j - i < MaxGapLines)
            {
                int from = Math.Max(0, before), to = Math.Min(lines.Count - 1, after);
                var part = lines.Skip(from).Take(to - from + 1).ToList();
                var prompt = string.Join(" ", part);
                var again = await relisten(new Gap(source, prompt.Length <= 300 ? prompt : prompt[..300], start, end));
                var aligned = LyricsAligner.Align(part, again, duration, voice);
                var texts = part.Select(LyricsAligner.Normalize).ToList();
                var floor = start;
                for (var k = i; k <= j; k++)
                {
                    var line = aligned[k - from];
                    // A line repeated in the stretch ("La-la-la" eight times, a chant): given
                    // them all, Whisper writes them out wherever, not where each is sung.
                    if (texts.Where((t, n) => n != k - from && LyricsAligner.Similar(t, texts[k - from])).Any()) continue;
                    var ceiling = after < lines.Count ? updated[after].Time.TotalSeconds : duration.TotalSeconds;
                    if (!line.IsConfident || line.Time.TotalSeconds <= floor + 0.25 || line.Time.TotalSeconds >= ceiling - 0.25) continue;
                    updated[k] = updated[k] with { Time = line.Time };
                    known[k] = found = true;
                    floor = line.Time.TotalSeconds;
                }
            }
            i = j;
        }
        return found ? LyricsAligner.Refill(updated, duration, voice, known) : result;
    }

    /// <summary>A stretch as pieces Whisper takes (up to 30 s each).</summary>
    public static List<(double Start, double End)> Pieces(Gap gap)
    {
        var count = Math.Max(1, (int)Math.Ceiling((gap.End - gap.Start) / 28));
        var length = (gap.End - gap.Start) / count;
        return [.. Enumerable.Range(0, count).Select(k => (gap.Start + k * length, gap.Start + (k + 1) * length))];
    }

    /// <summary>Longer runs of unheard lines aren't listened to again: given many lines at once,
    /// Whisper tends to write them out rather than hear them (with up to 12: 91.3% within 1 s,
    /// 2.2% off by over 3 s; up to 6: 91.4%, 2.0%).</summary>
    private const int MaxGapLines = 6;

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

    /// <summary>Frees the models. Not while a sync runs: cancel it and wait for it to end first
    /// (ONNX Runtime and whisper.cpp don't expect their models freed while they're working).</summary>
    public void Dispose()
    {
        _disposed = true;
        _transcriber.Dispose();
        _separator?.Dispose();
    }
}
