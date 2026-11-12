using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Decodes a song (Windows' own codecs: MP3, AAC/M4A, FLAC, WAV, …) to the 16 kHz mono
/// samples speech recognition expects.</summary>
public static class AudioDecoder
{
    public const int SampleRate = 16000;

    public static float[] ReadMono16k(string path, CancellationToken ct = default)
    {
        using var reader = new MediaFoundationReader(path);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels > 1) source = new MonoMix(source);
        if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);

        var samples = new List<float>((int)Math.Min(int.MaxValue / 2, reader.TotalTime.TotalSeconds * SampleRate + SampleRate));
        var buffer = new float[SampleRate];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);
        }
        return [.. samples];
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
