using NAudio.Dsp;
using NAudio.Wave;

namespace TuneSync.Core.Services;

/// <summary>A song's spectrum over time: <see cref="Bands"/> levels (0-255) per frame, at
/// <see cref="FramesPerSecond"/>. Frame <c>i</c> describes the sound around <c>i / fps</c> s.</summary>
public sealed class SpectrumData(int bands, double framesPerSecond, byte[] levels)
{
    public int Bands { get; } = bands;
    public double FramesPerSecond { get; } = framesPerSecond;
    public int FrameCount => levels.Length / Bands;

    /// <summary>Band <paramref name="band"/> of frame <paramref name="frame"/>, 0..1.</summary>
    public float Level(int frame, int band) =>
        frame < 0 || frame >= FrameCount ? 0 : levels[frame * Bands + band] / 255f;

    /// <summary>All bands at a moment, blended between the two nearest frames.</summary>
    public void Sample(double seconds, Span<float> into)
    {
        var position = seconds * FramesPerSecond;
        var frame = (int)Math.Floor(position);
        var t = (float)(position - frame);
        for (var b = 0; b < Bands && b < into.Length; b++)
            into[b] = Level(frame, b) * (1 - t) + Level(frame + 1, b) * t;
    }
}

/// <summary>Works out a song's spectrum in advance, the way the seek bar's waveform is: the
/// player (Windows Media) doesn't expose the sound it plays, so a live analyzer would have to
/// capture all of the PC's output. Decoding the file instead keeps the display exactly in step
/// with the playing position, at any volume, and costs about a second in the background.</summary>
public static class SpectrumAnalyzer
{
    public const int DefaultBands = 96;
    private const int FftOrder = 11; // 2048-point FFT: ~46 ms of sound at 44.1 kHz
    private const int FftSize = 1 << FftOrder;
    private const double LowHz = 40, HighHz = 16000;
    private const float Range = 54; // dB shown, from silence-ish to the song's loudest

    public static SpectrumData Analyze(string path, int bands = DefaultBands, double framesPerSecond = 60, CancellationToken ct = default)
    {
        using var reader = new MediaFoundationReader(path);
        var source = reader.ToSampleProvider();
        int channels = source.WaveFormat.Channels, rate = source.WaveFormat.SampleRate;
        var hop = Math.Max(1, (int)Math.Round(rate / framesPerSecond));
        var fps = rate / (double)hop;

        // Each band's FFT bins, log-spaced so every octave gets a similar share of the bars.
        var high = Math.Min(HighHz, rate / 2.0 * 0.95);
        var binLow = new int[bands];
        var binHigh = new int[bands];
        var tilt = new float[bands];
        for (var b = 0; b < bands; b++)
        {
            var f0 = LowHz * Math.Pow(high / LowHz, b / (double)bands);
            var f1 = LowHz * Math.Pow(high / LowHz, (b + 1) / (double)bands);
            binLow[b] = Math.Clamp((int)Math.Floor(f0 * FftSize / rate), 1, FftSize / 2 - 1);
            binHigh[b] = Math.Clamp((int)Math.Ceiling(f1 * FftSize / rate), binLow[b] + 1, FftSize / 2);
            // Music has far less energy up high; lift it ~3 dB per octave so the treble bars move too.
            tilt[b] = (float)(3 * Math.Log2(Math.Sqrt(f0 * f1) / 1000));
        }

        var window = new float[FftSize];
        for (var i = 0; i < FftSize; i++) window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);

        var ring = new float[FftSize];
        var ringPos = 0;
        var sinceFrame = 0;
        var fft = new Complex[FftSize];
        var decibels = new List<float>(capacity: (int)(reader.TotalTime.TotalSeconds * fps * bands) + bands);
        var buffer = new float[hop * channels * 8];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i + channels <= read; i += channels)
            {
                float mono = 0;
                for (var c = 0; c < channels; c++) mono += buffer[i + c];
                ring[ringPos] = mono / channels;
                ringPos = (ringPos + 1) % FftSize;
                if (++sinceFrame < hop) continue;
                sinceFrame = 0;

                for (var k = 0; k < FftSize; k++)
                {
                    fft[k].X = ring[(ringPos + k) % FftSize] * window[k];
                    fft[k].Y = 0;
                }
                FastFourierTransform.FFT(true, FftOrder, fft); // NAudio scales the result by 1/N
                for (var b = 0; b < bands; b++)
                {
                    float peak = 0;
                    for (var k = binLow[b]; k < binHigh[b]; k++)
                    {
                        var m = fft[k].X * fft[k].X + fft[k].Y * fft[k].Y;
                        if (m > peak) peak = m;
                    }
                    decibels.Add(10 * MathF.Log10(peak + 1e-14f) + tilt[b]);
                }
            }
        }

        // Scaled to this song: its loud moments reach the top whatever its overall level.
        var levels = new byte[decibels.Count];
        if (decibels.Count > 0)
        {
            var sorted = decibels.ToArray();
            Array.Sort(sorted);
            var top = sorted[(int)(sorted.Length * 0.995)];
            var floor = top - Range;
            for (var i = 0; i < levels.Length; i++)
                levels[i] = (byte)Math.Clamp((decibels[i] - floor) / Range * 255, 0, 255);
        }
        return new SpectrumData(bands, fps, levels);
    }
}
