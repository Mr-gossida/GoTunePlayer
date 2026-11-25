using Whisper.net;
using Whisper.net.Logger;

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
/// lines placed than whole-song decoding, but about 0.3 s late and less exact than these.)
///
/// It runs on the graphics card through Vulkan when asked to and one works (whisper.cpp falls
/// back to the CPU by itself otherwise; without a Vulkan driver at all, Whisper.net goes on to
/// its CPU-only library).</summary>
public sealed class WhisperTranscriber(string modelPath, LyricsSyncOptions options) : IDisposable
{
    private const int WindowSeconds = 30;

    private WhisperFactory? _factory;
    private bool _factoryOnGpu; // read for the graphics card (whisper.cpp may still have found none)
    private volatile bool _disposed;

    public WhisperTranscriber(string modelPath) : this(modelPath, LyricsSyncOptions.Default) { }

    /// <summary>Where the listening runs: the graphics card's name, "CPU", or null until the first
    /// piece has started (read from whisper.cpp's log, which says which device it took).</summary>
    public static string? Device => !_deviceKnown ? null : _gpuInUse ?? "CPU";

    private static volatile string? _gpuInUse;
    private static volatile bool _deviceKnown;

    /// <summary>Forgets where the last listening ran, so a new one isn't shown with the old device.</summary>
    public static void ForgetDevice() => _deviceKnown = false;
    private static string? _gpuName;
    private static volatile bool _gpuIntegrated;
    private static int _logging;

    private static void WatchLog()
    {
        if (Interlocked.Exchange(ref _logging, 1) == 1) return;
        LogProvider.AddLogger((_, text) =>
        {
            if (string.IsNullOrEmpty(text)) return;
            // "ggml_vulkan: 0 = NVIDIA GeForce RTX 5080 (NVIDIA) | uma: 0 | ..."
            if (text.Contains("ggml_vulkan: 0 = "))
            {
                var name = text[(text.IndexOf("0 = ", StringComparison.Ordinal) + 4)..];
                _gpuName = name.Split('|')[0].Trim();
                // "uma: 1": the graphics shares the PC's memory, i.e. it's built into the CPU
                // (whisper.cpp takes a separate card first when there is one)
                _gpuIntegrated = text.Contains("uma: 1");            }
            else if (text.Contains("whisper_backend_init_gpu: using")) { _gpuInUse = _gpuName ?? "GPU"; _deviceKnown = true; }
            else if (text.Contains("whisper_backend_init_gpu: no GPU found")) { _gpuInUse = null; _deviceKnown = true; }
        });
    }

    /// <param name="language">"ko", "en", … or null to let Whisper decide.</param>
    /// <param name="prompt">Text that biases recognition toward the expected words (the lyrics),
    /// or null to let it hear freely.</param>
    /// <param name="progress">0..100.</param>
    public async Task<List<TimedText>> TranscribeAsync(float[] samples, string? language, string? prompt,
        IProgress<int>? progress, CancellationToken ct, IReadOnlyList<(double Start, double End)>? windows = null)
    {
        WatchLog();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_factory is null)
        {
            var (factory, onGpu) = Open(modelPath, options.UseGpu);
            // Disposed while the model was being read: nothing else would free it.
            if (_disposed)
            {
                factory.Dispose();
                throw new ObjectDisposedException(nameof(WhisperTranscriber));
            }
            _factory = factory;
            _factoryOnGpu = onGpu;
        }
        // After the model is read: a try on the graphics card that gave up mustn't count.
        _deviceKnown = false;
        var builder = _factory.CreateBuilder()
            .WithThreads(options.Threads)
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
            // Cancelled, ProcessAsync throws at once while whisper.cpp still runs on its own
            // thread until it next checks: DisposeAsync waits for that, so this returns only once
            // nothing runs on the model any more (and it can be freed).
            // On the CPU, the encoder takes most of the time and always works through 30 s of
            // sound, though most pieces (cut at the singer's pauses) are far shorter: it's given
            // just the piece and a little more. Measured (tools/SyncBench, CPU, 8 threads):
            // listening 40% faster, lines placed as well as before (10 songs, within 1 s: 76% /
            // 75%). Not on the graphics card: there a different length each piece made it slower.
            if (!_factoryOnGpu || _deviceKnown && _gpuInUse is null)
                builder = builder.WithAudioContextSize(ContextFor(piece.Length));
            await using var processor = builder.Build();
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

    /// <summary>Reads the model: on the graphics card first when asked, then on the CPU. The card
    /// may have no room for it, and whisper.cpp says no more than that it couldn't load the
    /// model; when the CPU can't either, the file itself is at fault
    /// (<see cref="WhisperModelLoadException"/>).</summary>
    private static (WhisperFactory Factory, bool OnGpu) Open(string modelPath, bool useGpu)
    {
        if (useGpu)
        {
            try
            {
                var gpu = Load(modelPath, true);
                // Graphics built into the CPU listens slower than the CPU itself (seen on
                // integrated graphics; whisper.cpp's Vulkan code is made for separate cards), so
                // there the CPU listens. Its log names the device as the model is read.
                if (!_gpuIntegrated) return (gpu, true);
                gpu.Dispose();
            }
            catch (WhisperModelLoadException) { /* the CPU tells whether the file is at fault */ }
        }
        return (Load(modelPath, false), false);
    }

    /// <summary>The encoder's length for a piece, in its 20 ms steps (1500 = 30 s): the piece and
    /// about 1.3 s more (64 steps; the full 30 s was no better on songs with known timings).</summary>
    private static int ContextFor(int samples) =>
        Math.Min(1500, (int)Math.Ceiling(samples / (double)AudioDecoder.SampleRate * 50) + 64);

    private static WhisperFactory Load(string modelPath, bool useGpu)
    {
        var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        try
        {
            factory.CreateBuilder(); // the model is read on first use
            return factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _factory?.Dispose();
    }
}
