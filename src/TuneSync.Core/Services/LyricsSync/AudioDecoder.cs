using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Decodes a song (Windows' own codecs: MP3, AAC/M4A, FLAC, WAV, …) to the 16 kHz mono
/// samples speech recognition expects.</summary>
public static class AudioDecoder
{
    public const int SampleRate = 16000;

    /// <summary>The longest song read: the sync holds a song in memory several times over (as
    /// 44.1 kHz stereo while separating the vocals), so a recording of hours - or a file made to
    /// look like one - would take gigabytes.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    /// <summary>Room is made up front for a song as long as its header says, up to this: a header
    /// can claim days (an MP3's frame count is just a number in it).</summary>
    private const double PresizeSeconds = 15 * 60;

    public static float[] ReadMono16k(string path, CancellationToken ct = default)
    {
        using var reader = Audio.AudioFiles.Open(path);
        var source = reader.Provider;
        if (source.WaveFormat.Channels > 1) source = new MonoMix(source);
        if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);

        var samples = new List<float>(Capacity(reader.Duration, SampleRate));
        var max = MaxSamples(SampleRate);
        var buffer = new float[SampleRate];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (samples.Count + read > max) throw TooLong();
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);
        }
        return [.. samples];
    }

    /// <summary>The song as two channels at <paramref name="sampleRate"/> (a mono song twice,
    /// the first two channels of a surround one).</summary>
    public static (float[] Left, float[] Right) ReadStereo(string path, int sampleRate, CancellationToken ct = default)
    {
        using var reader = Audio.AudioFiles.Open(path);
        var source = reader.Provider;
        var channels = source.WaveFormat.Channels;
        if (source.WaveFormat.SampleRate != sampleRate) source = new WdlResamplingSampleProvider(source, sampleRate);

        var capacity = Capacity(reader.Duration, sampleRate);
        List<float> left = new(capacity), right = new(capacity);
        var max = MaxSamples(sampleRate);
        var buffer = new float[sampleRate / 10 * channels];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (left.Count + read / channels > max) throw TooLong();
            for (var i = 0; i + channels <= read; i += channels)
            {
                left.Add(buffer[i]);
                right.Add(buffer[channels > 1 ? i + 1 : i]);
            }
        }
        return ([.. left], [.. right]);
    }

    private static int Capacity(TimeSpan claimed, int sampleRate) =>
        (int)(Math.Clamp(claimed.TotalSeconds, 0, PresizeSeconds) * sampleRate) + sampleRate;

    private static long MaxSamples(int sampleRate) => (long)(MaxDuration.TotalSeconds * sampleRate);

    private static NotSupportedException TooLong() =>
        new($"곡이 너무 깁니다. 자동으로 맞추기는 {MaxDuration.TotalMinutes:0}분 이하의 곡만 할 수 있습니다.");

    /// <summary>One channel of sound at another sample rate.</summary>
    public static float[] Resample(float[] samples, int from, int to)
    {
        if (from == to) return samples;
        var source = new WdlResamplingSampleProvider(new Samples(samples, from), to);
        var result = new List<float>((int)((long)samples.Length * to / from) + to);
        var buffer = new float[to];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++) result.Add(buffer[i]);
        return [.. result];
    }

    private sealed class Samples(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            count = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, count);
            _position += count;
            return count;
        }
    }

    /// <summary>Averages all channels into one.</summary>
    private sealed class MonoMix(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = source.Read(_buffer, 0, needed);
            var frames = read / _channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[offset + f] = sum / _channels;
            }
            return frames;
        }
    }
}
