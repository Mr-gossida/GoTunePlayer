using NAudio.Wave;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Decodes a track (through Windows' own Media Foundation codecs: MP3, AAC/M4A, FLAC,
/// WMA, WAV) and measures its loudness every 50 ms, so the seek bar can draw - and pulse with -
/// the real sound instead of a decorative wave.</summary>
public static class WaveformAnalyzer
{
    public const double BucketSeconds = 0.05;

    /// <summary>Quietest level that still shows; anything below draws as flat.</summary>
    private const double FloorDb = -48;

    /// <summary>Null when the format has no system decoder (e.g. OGG) or the file can't be read -
    /// the seek bar then just shows a plain line.</summary>
    public static WaveformData? Analyze(string path, CancellationToken ct = default)
    {
        try
        {
            using var reader = new MediaFoundationReader(path);
            var samples = reader.ToSampleProvider();
            var channels = samples.WaveFormat.Channels;
            var framesPerBucket = Math.Max(1, (int)(samples.WaveFormat.SampleRate * BucketSeconds));
            var buffer = new float[framesPerBucket * channels];
            var rms = new List<float>((int)(reader.TotalTime.TotalSeconds / BucketSeconds) + 1);

            int read;
            while ((read = ReadFully(samples, buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                double sum = 0;
                for (var i = 0; i < read; i++) sum += buffer[i] * (double)buffer[i];
                rms.Add((float)Math.Sqrt(sum / read));
            }

            return rms.Count == 0 ? null : new WaveformData(Normalize(rms), BucketSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static int ReadFully(ISampleProvider provider, float[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = provider.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>Loudness in decibels (how hearing works), stretched between the track's own quiet
    /// (10th percentile) and loud (99th percentile) moments and curved to emphasize peaks. Modern
    /// masters are loud almost throughout, so an absolute scale would draw a flat block; relative
    /// to the track itself, beats, verses vs. choruses and intros stand out.</summary>
    private static float[] Normalize(List<float> rms)
    {
        var db = rms.Select(v => v > 0 ? 20 * Math.Log10(v) : FloorDb).Select(d => Math.Max(FloorDb, d)).ToArray();
        var sorted = db.OrderBy(d => d).ToArray();
        var high = sorted[(int)((sorted.Length - 1) * 0.99)];
        var low = Math.Min(sorted[(int)((sorted.Length - 1) * 0.10)], high - 6); // at least a 6 dB span
        return [.. db.Select(d => (float)Math.Pow(Math.Clamp((d - low) / (high - low), 0, 1), 1.6))];
    }
}
