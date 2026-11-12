using Whisper.net;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Speech recognition with a local Whisper model (whisper.cpp): nothing leaves the PC.
/// Returns each heard word anchored at its start. (Whisper's word segments run word into word,
/// folding every pause into the word before it, so their end times aren't used. whisper.cpp's
/// DTW timestamps would be finer, but with them decoding stops after the first 30 s window.)</summary>
public sealed class WhisperTranscriber(string modelPath) : IDisposable
{
    private WhisperFactory? _factory;

    /// <param name="language">"ko", "en", … or null to let Whisper decide.</param>
    /// <param name="prompt">Text that biases recognition toward the expected words (the lyrics).</param>
    /// <param name="progress">0..100.</param>
    public async Task<List<TimedText>> TranscribeAsync(float[] samples, string? language, string? prompt,
        IProgress<int>? progress, CancellationToken ct)
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
        if (progress is not null) builder = builder.WithProgressHandler(p => progress.Report(p));

        using var processor = builder.Build();
        var words = new List<(double Onset, string Text)>();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            var text = segment.Text.Trim();
            if (text.Length == 0 || text.StartsWith('[') || text.StartsWith('(') || text.Contains('♪')) continue;
            words.Add((segment.Start.TotalSeconds, text));
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
