using NAudio.Wave;
using TuneSync.Core.Audio.Dsd;

namespace TuneSync.Core.Audio;

/// <summary>A song as float samples for analysis (the seek bar's loudness, the spectrum, lyrics
/// timing): Windows' codecs, or GoTune's own DSD reader for DSF/DFF.</summary>
public static class AudioFiles
{
    public sealed class Samples(ISampleProvider provider, TimeSpan duration, IDisposable owner) : IDisposable
    {
        public ISampleProvider Provider { get; } = provider;
        public TimeSpan Duration { get; } = duration;
        public void Dispose() => owner.Dispose();
    }

    public static Samples Open(string path)
    {
        if (DsdFile.IsDsdPath(path))
        {
            // 88.2 kHz is plenty for analysis, and half the work of the player's 176.4 kHz
            int rate;
            using (var file = new DsdFile(path)) rate = DsdDecoder.PcmRate(file.SampleRate) / 2;
            var decoder = new DsdDecoder(path, dop: false, pcmRate: rate);
            return new Samples(new DsdSampleProvider(decoder), decoder.Duration, decoder);
        }
        var reader = new MediaFoundationReader(path);
        return new Samples(reader.ToSampleProvider(), reader.TotalTime, reader);
    }

    private sealed class DsdSampleProvider(DsdDecoder decoder) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(decoder.SampleRate, decoder.Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = decoder.Read(buffer.AsSpan(offset, count - count % decoder.Channels));
            return frames * decoder.Channels;
        }
    }
}
