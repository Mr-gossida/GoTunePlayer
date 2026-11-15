using Whisper.net;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Speech recognition with a local Whisper model (whisper.cpp): nothing leaves the PC.
/// Returns each heard word anchored at its start. (Whisper's word segments run word into word,
/// folding every pause into the word before it, so their end times aren't used.)
///
/// The song is listened to in 30 s pieces, each on its own. Left to run over a whole song,
/// Whisper carries what it decided earlier into the rest: once it loses its place in a long
/// instrumental or a slow ballad it often stays lost (or repeats itself) to the end, and the
/// lyrics after that couldn't be placed. Measured on songs with known timings (tools/SyncBench),
/// the pieces got far more lines placed. (whisper.cpp's DTW token times were tried as well: more
/// lines placed than whole-song decoding, but about 0.3 s late and less exact than these.)</summary>
public sealed class WhisperTranscriber(string modelPath) : IDisposable
{
    private const int WindowSeconds = 30;

    private WhisperFactory? _factory;

    /// <param name="language">"ko", "en", … or null to let Whisper decide.</param>
    /// <param name="prompt">Text that biases recognition toward the expected words (the lyrics),
    /// or null to let it hear freely.</param>
    /// <param name="progress">0..100.</param>
    public async Task<List<TimedText>> TranscribeAsync(float[] samples, string? language, string? prompt,
        IProgress<int>? progress, CancellationToken ct, IReadOnlyList<(double Start, double End)>? windows = null)
    {
        _factory ??= WhisperFactory.FromPath(modelPath);
        var builder = _factory.CreateBuilder()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .WithTokenTimestamps()
            .SplitOnWord()
            .WithMaxSegmentLength(1) // one word per piece, each with its own time
            .WithNoSpeechThreshold(0.6f);
        builder = language is null ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);

        if (windows is null)
        {
            var fixedWindows = new List<(double, double)>();
            var total = samples.Length / (double)AudioDecoder.SampleRate;
            for (var t = 0.0; t < total; t += WindowSeconds) fixedWindows.Add((t, Math.Min(total, t + WindowSeconds)));
            windows = fixedWindows;
        }

        var words = new List<(double Onset, string Text)>();
        for (var w = 0; w < windows.Count; w++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = Math.Clamp((int)(windows[w].Start * AudioDecoder.SampleRate), 0, samples.Length);
            var length = Math.Min((int)((windows[w].End - windows[w].Start) * AudioDecoder.SampleRate), samples.Length - offset);
            if (length < AudioDecoder.SampleRate / 2) continue; // a scrap
            var start = offset / (double)AudioDecoder.SampleRate;
            // Whisper won't take less than a second: a short piece is padded with silence.
            var piece = new float[Math.Max(length, AudioDecoder.SampleRate + AudioDecoder.SampleRate / 10)];
            samples.AsSpan(offset, length).CopyTo(piece);
            using var processor = builder.Build();
            await foreach (var segment in processor.ProcessAsync(piece, ct))
            {
                var text = segment.Text.Trim();
                if (text.Length == 0 || text.StartsWith('[') || text.StartsWith('(') || text.Contains('♪')) continue;
                words.Add((start + Math.Min(segment.Start.TotalSeconds, length / (double)AudioDecoder.SampleRate), text));
            }
            progress?.Report((int)(100.0 * (w + 1) / windows.Count));
        }

        // Each word from its onset, as long as its letters take at a steady pace - but never
        // running into the next word.
        var pieces = new List<TimedText>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var (onset, text) = words[i];
            var end = onset + Math.Max(1, text.Length) * TypicalCharSeconds;
            if (i + 1 < words.Count && words[i + 1].Onset > onset) end = Math.Min(end, words[i + 1].Onset);
            pieces.Add(new TimedText(TimeSpan.FromSeconds(onset), TimeSpan.FromSeconds(Math.Max(end, onset)), text));
        }
        return pieces;
    }

    private const double TypicalCharSeconds = 0.22;

    public void Dispose() => _factory?.Dispose();
}
