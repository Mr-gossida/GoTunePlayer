using TuneSync.Core.Audio;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class MfDecoderTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", name);

    private static float[] ReadAll(MfDecoder decoder)
    {
        var all = new List<float>();
        var buffer = new float[1024 * decoder.Channels];
        int frames;
        while ((frames = decoder.Read(buffer)) > 0) all.AddRange(buffer.AsSpan(0, frames * decoder.Channels).ToArray());
        return [.. all];
    }

    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void Decodes_TheWholeSong_AsSamplesInRange(string fileName)
    {
        using var decoder = new MfDecoder(Asset(fileName));
        var samples = ReadAll(decoder);

        Assert.InRange(decoder.Channels, 1, 2);
        Assert.True(decoder.SampleRate >= 8000);
        var seconds = samples.Length / decoder.Channels / (double)decoder.SampleRate;
        var expected = TagService.ReadDuration(Asset(fileName)).TotalSeconds;
        Assert.InRange(seconds, expected * 0.9, expected * 1.1 + 0.1);
        Assert.InRange(decoder.Duration.TotalSeconds, expected * 0.9, expected * 1.1 + 0.1);
        Assert.All(samples, s => Assert.InRange(s, -1.01f, 1.01f));
        Assert.True(samples.Max(Math.Abs) > 0.05f, "the sample is an audible tone");
    }

    [Fact]
    public void Seek_LandsOnTheExactFrame()
    {
        // Two seconds of noise as a 16-bit stereo WAV: every frame is different, so where a
        // seek landed can be told exactly.
        const int rate = 44100, frames = rate * 2;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        var random = new Random(3);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4);
            writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 4);
            for (var i = 0; i < frames * 2; i++) writer.Write((short)random.Next(-20000, 20000));
        }
        try
        {
            using var decoder = new MfDecoder(path);
            var all = ReadAll(decoder);
            Assert.Equal(frames * 2, all.Length);

            foreach (var frame in new[] { 16317, 70001, 0, 44100, 12345 })
            {
                // A tick (100 ns) past the frame's start, so rounding can't fall into the frame before.
                decoder.Seek(TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / rate + 1));
                var buffer = new float[256 * 2];
                Assert.Equal(256, decoder.Read(buffer));
                var landed = Enumerable.Range(Math.Max(0, frame - 2000), 4000).FirstOrDefault(f => f + 256 <= frames && all.AsSpan(f * 2, 512).SequenceEqual(buffer), -1);
                Assert.Equal(frame, landed);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsOn_AfterTheEnd_OnceSoughtBack()
    {
        using var decoder = new MfDecoder(Asset("sample.mp3"));
        var first = ReadAll(decoder);
        Assert.Equal(0, decoder.Read(new float[64 * decoder.Channels]));
        decoder.Seek(TimeSpan.Zero);
        Assert.Equal(first.Length, ReadAll(decoder).Length);
    }

    [Fact]
    public void Throws_ForAFileThatIsNotAudio()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp3");
        File.WriteAllText(path, "not audio");
        try
        {
            Assert.ThrowsAny<Exception>(() => new MfDecoder(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
