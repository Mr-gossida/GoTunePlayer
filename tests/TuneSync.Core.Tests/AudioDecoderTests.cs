using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

public class AudioDecoderTests
{
    [Fact]
    public void ReadMono16k_DoesNotTrustTheLengthAFileClaims()
    {
        // sample.mp3 is a second long; its Xing header made to say 16 million frames (five days).
        var path = TestFixtures.CopyFixture("sample.mp3");
        try
        {
            var bytes = File.ReadAllBytes(path);
            var xing = bytes.AsSpan().IndexOf("Xing"u8);
            Assert.True(xing > 0 && (bytes[xing + 7] & 1) == 1, "the sample has a Xing header with a frame count");
            bytes[xing + 8] = 0x00;
            bytes[xing + 9] = bytes[xing + 10] = bytes[xing + 11] = 0xFF;
            File.WriteAllBytes(path, bytes);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var samples = AudioDecoder.ReadMono16k(path);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.InRange(samples.Length / (double)AudioDecoder.SampleRate, 0.8, 1.5);
            // It used to make room for all five days at once: 4.3 GB.
            Assert.True(allocated < 300_000_000, $"{allocated / 1e6:0} MB allocated for a one-second song");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadMono16k_RefusesASongLongerThanTheLimit()
    {
        // Silence as 8-bit 16 kHz mono, a minute longer than the limit.
        const int rate = 16000;
        var bytes = (int)(AudioDecoder.MaxDuration.TotalSeconds + 60) * rate;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate);
            writer.Write((short)1); writer.Write((short)8); writer.Write("data"u8); writer.Write(bytes);
            var second = Enumerable.Repeat((byte)128, rate).ToArray();
            for (var written = 0; written < bytes; written += rate) writer.Write(second);
        }
        try
        {
            var ex = Assert.Throws<NotSupportedException>(() => AudioDecoder.ReadMono16k(path));
            Assert.Contains("너무 깁니다", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
