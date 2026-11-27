using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Takes the singing out of a song with a local MDX-Net model (ONNX; on the graphics
/// card through DirectML when there is one, otherwise on the CPU). The accompaniment is what
/// throws speech recognition off: with the vocals alone it's plain where the singer pauses, so
/// the song can be listened to phrase by phrase (see <see cref="VoiceActivity"/>).
///
/// The model works on spectrograms of 44.1 kHz stereo: 7680-point frames every 1024 samples,
/// the lowest 3072 bins, 256 frames (about 6 s) at a time. Each piece overlaps its neighbors
/// and only its middle is kept, where the model has context on both sides.
///
/// With graphics built into the CPU, the CPU takes pieces too (<see cref="WorkShare"/>): on a
/// Core Ultra 5 225 a song took 59 s on its Intel graphics, 67 s on the CPU (5 threads) and 40 s
/// on both. The CPU's copy of the model is opened for each song and closed after: while it
/// works it holds about 1.5 GB more.</summary>
public sealed class VocalSeparator : IDisposable
{
    public const int SampleRate = 44100;

    private const int FftSize = 7680;
    private const int Hop = 1024;
    private const int Bins = 3072;
    private const int Frames = 256;
    private const int Chunk = Hop * (Frames - 1);
    private const int Trim = FftSize / 2;
    private const int Step = Chunk - 2 * Trim;

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _windowSum = new float[FftSize + Chunk];
    private readonly Fft7680 _fft = new();

    /// <summary>True when the model runs on the graphics card.</summary>
    public bool UsesGpu { get; }

    /// <summary>Where the separation runs: "GPU", "CPU", or "GPU + CPU" (see the summary).</summary>
    public string Device => _cpuSpeed is null ? (UsesGpu ? "GPU" : "CPU")
        : _gpuSpeed.Retired ? "CPU" : _cpuSpeed.Retired ? "GPU" : "GPU + CPU";

    private readonly ParallelOptions _parallel;
    private readonly string _modelPath;
    private readonly int _threads;
    private readonly WorkShare.Device _gpuSpeed = new("GPU");
    private readonly WorkShare.Device? _cpuSpeed; // when the CPU shares the work with the graphics

    /// <param name="threads">CPU threads for the spectrograms (and the model itself, on the CPU).</param>
    /// <param name="shareWithCpu">When the model runs on the graphics card, let the CPU take
    /// pieces too (for graphics built into the CPU).</param>
    public VocalSeparator(string modelPath, bool allowGpu = true, int threads = 0, bool shareWithCpu = false)
    {
        if (threads <= 0) threads = Environment.ProcessorCount;
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = threads };
        (_modelPath, _threads) = (modelPath, threads);
        (_session, UsesGpu) = Open(modelPath, allowGpu, threads);
        if (UsesGpu && shareWithCpu) _cpuSpeed = new WorkShare.Device("CPU");
        _input = _session.InputMetadata.Keys.First();
        for (var i = 0; i < FftSize; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize));
        for (var t = 0; t < Frames; t++)
            for (var i = 0; i < FftSize; i++) _windowSum[t * Hop + i] += _window[i] * _window[i];
    }

    private static (InferenceSession, bool) Open(string modelPath, bool allowGpu, int threads)
    {
        if (allowGpu)
        {
            try
            {
                // DirectML wants its memory handled this way.
                using var gpu = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, IntraOpNumThreads = threads };
                gpu.AppendExecutionProvider_DML(0);
                return (new InferenceSession(modelPath, gpu), true);
            }
            catch (Exception) { /* no usable graphics card or driver: the CPU does it, slower */ }
        }
        using var cpu = CpuOptions(threads);
        return (new InferenceSession(modelPath, cpu), false);
    }

    private static SessionOptions CpuOptions(int threads) => new() { IntraOpNumThreads = threads };

    /// <summary>The vocals of a 44.1 kHz stereo song, as one channel at 44.1 kHz.</summary>
    /// <param name="progress">0..1.</param>
    public float[] Separate(float[] left, float[] right, IProgress<double>? progress, CancellationToken ct)
    {
        var n = left.Length;
        var steps = (n + Step - 1) / Step;
        // The song with silence around it, so the first and last pieces have a middle too.
        var padded = new float[2][];
        foreach (var (c, source) in new[] { (0, left), (1, right) })
        {
            padded[c] = new float[Trim + steps * Step + Trim];
            source.CopyTo(padded[c], Trim);
        }

        var vocals = new float[n];
        // Who works: both while both are in; the CPU alone once the graphics dropped out (it
        // failed - a lost device - or was far slower). Handing the pieces back to the graphics
        // alone then made a failed GPU fail again on every later song of a batch.
        var cpuOnly = _cpuSpeed is not null && _gpuSpeed.Retired;
        var shared = _cpuSpeed is { Retired: false } && !_gpuSpeed.Retired;
        using var cpuOptions = shared || cpuOnly ? CpuOptions(_threads) : null;
        using var cpu = cpuOptions is null ? null : new InferenceSession(_modelPath, cpuOptions);
        var sessions = cpuOnly ? [cpu!] : cpu is null ? [_session] : new[] { _session, cpu };
        WorkShare.Device[] speeds = cpuOnly ? [_cpuSpeed!] : cpu is null ? [_gpuSpeed] : [_gpuSpeed, _cpuSpeed!];
        // Each device works on one piece at a time, in buffers of its own.
        var buffers = new (float[] Spectrum, DenseTensor<float> Tensor, float[][] Piece)?[sessions.Length];

        bool SeparatePiece(int device, int s, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (buffers[device] is null)
            {
                var spectrum = new float[4 * Bins * Frames];
                buffers[device] = (spectrum, new DenseTensor<float>(spectrum, [1, 4, Bins, Frames]), [new float[Chunk], new float[Chunk]]);
            }
            var (spectrumOf, tensor, piece) = buffers[device]!.Value;
            for (var c = 0; c < 2; c++) Forward(padded[c].AsSpan(s * Step, Chunk), spectrumOf, c);
            using (var results = sessions[device].Run([NamedOnnxValue.CreateFromTensor(_input, tensor)]))
            {
                var output = results[0].AsTensor<float>();
                var data = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray();
                for (var c = 0; c < 2; c++) Inverse(data, c, piece[c]);
            }
            var to = s * Step;
            var count = Math.Min(Step, n - to);
            for (var i = 0; i < count; i++) vocals[to + i] = (piece[0][Trim + i] + piece[1][Trim + i]) * 0.5f;
            return true;
        }

        // Every piece is the same size; none is taken twice (each takes a device about as long).
        WorkShare.RunAsync(speeds, [.. Enumerable.Repeat(1.0, steps)],
                (device, s, token) => Task.FromResult(SeparatePiece(device, s, token)),
                hedge: false, progress is null ? null : done => progress.Report(done / (double)steps), ct)
            .GetAwaiter().GetResult();
        return vocals;
    }

    /// <summary>One channel's piece to the model's layout: [real, imaginary] x bins x frames.
    /// Frames are centered on every 1024th sample, the piece mirrored at its ends.</summary>
    private void Forward(ReadOnlySpan<float> samples, float[] spectrum, int channel)
    {
        var source = samples.ToArray();
        Parallel.For(0, Frames, _parallel, () => (new Complex[FftSize], new Complex[FftSize], new Complex[FftSize]), (t, _, buffers) =>
        {
            var (input, output, scratch) = buffers;
            for (var i = 0; i < FftSize; i++)
            {
                var k = t * Hop + i - FftSize / 2;
                if (k < 0) k = -k;
                else if (k >= Chunk) k = 2 * (Chunk - 1) - k;
                input[i] = source[k] * _window[i];
            }
            _fft.Transform(input, output, Bins, scratch);
            var re = (channel * 2) * Bins * Frames + t;
            var im = (channel * 2 + 1) * Bins * Frames + t;
            for (var b = 0; b < Bins; b++)
            {
                spectrum[re + b * Frames] = (float)output[b].Real;
                spectrum[im + b * Frames] = (float)output[b].Imaginary;
            }
            return buffers;
        }, _ => { });
    }

    /// <summary>The model's spectrogram of one channel back to samples (overlap-add).</summary>
    private void Inverse(ReadOnlySpan<float> spectrum, int channel, float[] samples)
    {
        var data = spectrum.ToArray();
        var frames = new float[Frames][];
        Parallel.For(0, Frames, _parallel, () => (new Complex[FftSize], new Complex[FftSize], new Complex[FftSize]), (t, _, buffers) =>
        {
            var (input, output, scratch) = buffers;
            var re = (channel * 2) * Bins * Frames + t;
            var im = (channel * 2 + 1) * Bins * Frames + t;
            // The inverse of a real signal's spectrum, through the forward transform:
            // conj(FFT(conj(X))) / N, with the upper half mirrored from the lower.
            Array.Clear(input);
            for (var b = 0; b < Bins; b++)
            {
                var value = new Complex(data[re + b * Frames], -data[im + b * Frames]);
                input[b] = value;
                if (b > 0) input[FftSize - b] = Complex.Conjugate(value);
            }
            _fft.Transform(input, output, FftSize, scratch);
            var frame = new float[FftSize];
            for (var i = 0; i < FftSize; i++) frame[i] = (float)(output[i].Real / FftSize) * _window[i];
            frames[t] = frame;
            return buffers;
        }, _ => { });

        var sum = new float[FftSize + Chunk];
        for (var t = 0; t < Frames; t++)
        {
            var frame = frames[t];
            for (var i = 0; i < FftSize; i++) sum[t * Hop + i] += frame[i];
        }
        for (var i = 0; i < Chunk; i++)
        {
            var k = i + FftSize / 2;
            samples[i] = sum[k] / Math.Max(_windowSum[k], 1e-8f);
        }
    }

    public void Dispose() => _session.Dispose();

    /// <summary>A 7680-point FFT (the model's frame size isn't a power of two): 7680 = 15 x 512,
    /// so fifteen 512-point transforms of every 15th sample, put together per output bin.</summary>
    private sealed class Fft7680
    {
        private const int Parts = 15, Small = 512;
        private readonly Complex[] _twiddle = new Complex[FftSize];
        private readonly Complex[] _smallTwiddle = new Complex[Small / 2];
        private readonly int[] _reversed = new int[Small];

        public Fft7680()
        {
            for (var i = 0; i < FftSize; i++) _twiddle[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / FftSize);
            for (var i = 0; i < Small / 2; i++) _smallTwiddle[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / Small);
            for (var i = 0; i < Small; i++)
            {
                var r = 0;
                for (var bit = 0; bit < 9; bit++) if ((i & (1 << bit)) != 0) r |= 1 << (8 - bit);
                _reversed[i] = r;
            }
        }

        /// <summary>The first <paramref name="bins"/> bins of the transform of <paramref name="input"/>.</summary>
        public void Transform(Complex[] input, Complex[] output, int bins, Complex[] scratch)
        {
            var parts = scratch.AsSpan();
            for (var r = 0; r < Parts; r++)
            {
                var part = parts.Slice(r * Small, Small);
                for (var m = 0; m < Small; m++) part[_reversed[m]] = input[Parts * m + r];
                for (var size = 2; size <= Small; size <<= 1)
                {
                    int half = size / 2, stride = Small / size;
                    for (var start = 0; start < Small; start += size)
                        for (var j = 0; j < half; j++)
                        {
                            var a = part[start + j];
                            var b = part[start + j + half] * _smallTwiddle[j * stride];
                            part[start + j] = a + b;
                            part[start + j + half] = a - b;
                        }
                }
            }
            for (var k = 0; k < bins; k++)
            {
                var sum = Complex.Zero;
                var small = k % Small;
                for (var r = 0; r < Parts; r++) sum += parts[r * Small + small] * _twiddle[r * k % FftSize];
                output[k] = sum;
            }
        }
    }
}
