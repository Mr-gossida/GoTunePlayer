using TuneSync.Core.Audio.Dsd;
using Xunit.Abstractions;

namespace TuneSync.Core.Tests;

/// <summary>DSF and DSDIFF files, read back as PCM and as DoP; remastering's DoP decodes back to the tone.</summary>
public class DsdFileTests(ITestOutputHelper log)
{
    private const int Rate = DsdTests.Dsd64;

    /// <summary>A stereo DSF file (bits stored first-bit-lowest, 4096-byte blocks per channel).</summary>
    internal static string WriteDsf(byte[] left, byte[] right)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gotune-test-{Guid.NewGuid():N}.dsf");
        const int block = 4096;
        var blocks = (left.Length + block - 1) / block;
        using var w = new BinaryWriter(File.Create(path));
        var dataSize = 12L + blocks * block * 2;
        w.Write("DSD "u8); w.Write(28L); w.Write(28L + 52 + dataSize); w.Write(0L);
        w.Write("fmt "u8); w.Write(52L); w.Write(1); w.Write(0); w.Write(2); w.Write(2); w.Write(Rate); w.Write(1);
        w.Write((long)left.Length * 8); w.Write(block); w.Write(0);
        w.Write("data"u8); w.Write(dataSize);
        static byte Rev(byte b) { var r = 0; for (var i = 0; i < 8; i++) if ((b >> i & 1) != 0) r |= 0x80 >> i; return (byte)r; }
        for (var k = 0; k < blocks; k++)
            foreach (var channel in new[] { left, right })
                for (var i = 0; i < block; i++)
                {
                    var at = k * block + i;
                    w.Write(at < channel.Length ? Rev(channel[at]) : (byte)0);
                }
        return path;
    }

    /// <summary>A stereo DSDIFF file (bytes interleaved, first bit on top).</summary>
    private static string WriteDff(byte[] left, byte[] right)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gotune-test-{Guid.NewGuid():N}.dff");
        using var w = new BinaryWriter(File.Create(path));
        void Id(string s) => w.Write(System.Text.Encoding.ASCII.GetBytes(s));
        void Big64(long v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); w.Write(b); }
        void Big32(int v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); w.Write(b); }
        void Big16(short v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); w.Write(b); }
        var title = System.Text.Encoding.ASCII.GetBytes("Test Tone");
        var prop = 4 + (12 + 4) + (12 + 2 + 8) + (12 + 4 + 2);
        var diin = 12 + 4 + title.Length + (title.Length & 1);
        var data = (long)left.Length * 2;
        Id("FRM8"); Big64(4 + 12 + prop + 12 + diin + 12 + data); Id("DSD ");
        Id("PROP"); Big64(prop); Id("SND ");
        Id("FS  "); Big64(4); Big32(Rate);
        Id("CHNL"); Big64(2 + 8); Big16(2); Id("SLFT"); Id("SRGT");
        Id("CMPR"); Big64(4 + 2); Id("DSD "); w.Write((byte)0); w.Write((byte)0);
        Id("DIIN"); Big64(diin);
        Id("DITI"); Big64(4 + title.Length); Big32(title.Length); w.Write(title); if ((title.Length & 1) != 0) w.Write((byte)0);
        Id("DSD "); Big64(data);
        for (var i = 0; i < left.Length; i++) { w.Write(left[i]); w.Write(right[i]); }
        return path;
    }

    private static double LevelOf(float[] samples, int channels, int channel, int rate, double hz)
    {
        int frames = samples.Length / channels, from = frames / 4, to = frames * 3 / 4;
        double s = 0, c = 0;
        for (var i = from; i < to; i++)
        {
            s += samples[i * channels + channel] * Math.Sin(2 * Math.PI * hz * i / rate);
            c += samples[i * channels + channel] * Math.Cos(2 * Math.PI * hz * i / rate);
        }
        return 20 * Math.Log10(2 * Math.Sqrt(s * s + c * c) / (to - from));
    }

    [Theory]
    [InlineData(".dsf")]
    [InlineData(".dff")]
    public void File_DecodesToItsTones(string kind)
    {
        // a different tone in each channel, so a swap would show
        var left = DsdTests.Modulate(1000, 0.5, 5);
        var right = DsdTests.Modulate(3000, 0.25, 5);
        var path = kind == ".dsf" ? WriteDsf(left, right) : WriteDff(left, right);
        try
        {
            using var decoder = new DsdDecoder(path, dop: false);
            Assert.Equal(176400, decoder.SampleRate);
            Assert.Equal(2, decoder.Channels);
            Assert.InRange(decoder.Duration.TotalSeconds, 0.49, 0.51);
            var pcm = new float[(int)(decoder.Duration.TotalSeconds * 176400) * 2 + 1000];
            var frames = decoder.Read(pcm);
            Assert.InRange(frames, 176400 / 2 - 10, 176400 / 2 + 10);
            var samples = pcm[..(frames * 2)];
            double l1k = LevelOf(samples, 2, 0, 176400, 1000), r3k = LevelOf(samples, 2, 1, 176400, 3000), l3k = LevelOf(samples, 2, 0, 176400, 3000);
            log.WriteLine($"{kind}: left 1 kHz {l1k:0.00} dB, right 3 kHz {r3k:0.00} dB, left at 3 kHz {l3k:0.0} dB");
            Assert.InRange(l1k, -6.1, -5.9);
            Assert.InRange(r3k, -12.1, -11.9);
            Assert.True(l3k < -100);
            if (kind == ".dff")
            {
                using var file = new DsdFile(path);
                Assert.Equal("Test Tone", file.Title);
            }

            // seeking: the sound carries on from there (a running tone, not silence)
            decoder.Seek(TimeSpan.FromSeconds(0.25));
            var after = new float[2000];
            decoder.Read(after);
            Assert.Contains(after.Skip(400), v => Math.Abs(v) > 0.1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DoP_CarriesTheBytesUntouched()
    {
        var left = DsdTests.Modulate(1000, 0.5, 1);
        var right = DsdTests.Modulate(2000, 0.5, 1);
        var path = WriteDsf(left, right);
        try
        {
            using var decoder = new DsdDecoder(path, dop: true);
            Assert.True(decoder.IsDoP);
            Assert.Equal(176400, decoder.SampleRate);
            var words = new float[left.Length]; // two bytes per frame per channel
            var frames = decoder.Read(words);
            Assert.Equal(left.Length / 2, frames);
            for (var f = 0; f < frames; f++)
            {
                for (var c = 0; c < 2; c++)
                {
                    var value = (int)Math.Round(words[f * 2 + c] * 8388608.0) & 0xFFFFFF;
                    Assert.Equal(f % 2 == 0 ? 0x05 : 0xFA, value >> 16);
                    var source = c == 0 ? left : right;
                    Assert.Equal(source[2 * f], (byte)(value >> 8));
                    Assert.Equal(source[2 * f + 1], (byte)value);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Remastering_DecodesBackToTheTone()
    {
        var remaster = new DsdRemasterer(44100, Rate, Models.ResampleFilter.Sharp);
        const int seconds10ths = 5;
        var input = new double[44100 / 10 * seconds10ths * 2];
        for (var i = 0; i < input.Length / 2; i++) input[2 * i] = input[2 * i + 1] = 0.5 * Math.Sin(2 * Math.PI * 1000 * i / 44100.0);
        var dop = new double[input.Length / 2 * 4 * 2 + 1000];
        var made = remaster.Process(input, dop);
        Assert.Equal(0, remaster.Resets);
        // all but the converter's lookahead (held until the song ends)
        Assert.InRange(made, 176400 / 10 * seconds10ths - 600, 176400 / 10 * seconds10ths);

        // unpack the left channel's DSD and decode it
        var bytes = new byte[made * 2];
        for (var f = 0; f < made; f++)
        {
            var value = (int)Math.Round(dop[f * 2] * 8388608.0) & 0xFFFFFF;
            Assert.Equal(f % 2 == 0 ? 0x05 : 0xFA, value >> 16);
            bytes[2 * f] = (byte)(value >> 8);
            bytes[2 * f + 1] = (byte)value;
        }
        var decimator = new DsdDecimator(Rate, 176400);
        var pcm = new double[bytes.Length];
        var count = decimator.Process(bytes, pcm);
        var level = LevelOf([.. pcm[..count].Select(v => (float)v)], 1, 0, 176400, 1000);
        log.WriteLine($"remastered 1 kHz at -6 dBFS came back at {level:0.00} dB");
        Assert.InRange(level, -6.1, -5.9);
    }

    [Theory]
    [InlineData("form")]
    [InlineData("prop")]
    [InlineData("diin")]
    public async Task Dff_NegativeChunkSize_IsRefusedNotGoneRoundIn(string where)
    {
        // a size of -12 points back at the chunk's own header: the reader went round and round
        var junk = Chunk("JUNK", -12);
        var path = WriteDffOf(
            inProp: where == "prop" ? junk : [],
            beforeSound: where switch { "form" => junk, "diin" => Chunk("DIIN", null, junk), _ => [] });
        try
        {
            var opening = Task.Run(() => new DsdFile(path).Dispose());
            Assert.Same(opening, await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(10))));
            await Assert.ThrowsAsync<InvalidDataException>(() => opening);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dff_ClaimingMoreThanItHas_OpensWithWhatIsThere()
    {
        // the title's chunk and its count claim 2 GB (the reader made room for all of it), and the
        // sound chunk claims a megabyte where the file has 2000 bytes (a cut-short copy)
        var title = Chunk("DIIN", null, Chunk("DITI", int.MaxValue, Big(int.MaxValue, 4), Id("Test Title")));
        var path = WriteDffOf(inProp: [], beforeSound: title, soundSize: 1_000_000);
        try
        {
            using var file = new DsdFile(path);
            Assert.Equal("Test Title", file.Title);
            Assert.Equal(1000, file.Length);
            byte[][] channels = [new byte[4096], new byte[4096]];
            Assert.Equal(1000, file.Read(channels, 4096));
            Assert.Equal(0, file.Read(channels, 4096));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dsf_DamagedHeader_IsRefusedOrEmpty()
    {
        var path = WriteDsf(new byte[100], new byte[100]);
        try
        {
            // a 2 GB block (the buffer for all channels' blocks overflowed) and a rate above any DSD's
            Patch(path, 72, BitConverter.GetBytes(int.MaxValue));
            Assert.Throws<InvalidDataException>(() => new DsdFile(path));
            Patch(path, 72, BitConverter.GetBytes(4096));
            Patch(path, 56, BitConverter.GetBytes(int.MaxValue - 7));
            Assert.Throws<InvalidDataException>(() => new DsdFile(path));
            Patch(path, 56, BitConverter.GetBytes(Rate));

            // a negative sample count: an empty song (seeking in it threw)
            Patch(path, 64, BitConverter.GetBytes(-800L));
            using var file = new DsdFile(path);
            Assert.Equal(0, file.Length);
            file.Seek(1000);
            Assert.Equal(0, file.Read([new byte[16], new byte[16]], 16));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Id(string id) => System.Text.Encoding.ASCII.GetBytes(id);

    /// <summary>The low <paramref name="bytes"/> bytes of <paramref name="value"/>, highest first.</summary>
    private static byte[] Big(long value, int bytes)
    {
        var b = BitConverter.GetBytes(value)[..bytes];
        Array.Reverse(b);
        return b;
    }

    /// <summary>A DSDIFF chunk: its id, its size (the body's length unless given) and its body.</summary>
    private static byte[] Chunk(string id, long? size, params byte[][] body)
    {
        var content = body.SelectMany(b => b).ToArray();
        return [.. Id(id), .. Big(size ?? content.Length, 8), .. content];
    }

    /// <summary>A stereo DSDIFF file of 2000 bytes of DSD silence, with <paramref name="inProp"/>
    /// after the rate in PROP and <paramref name="beforeSound"/> just before the sound chunk.</summary>
    private static string WriteDffOf(byte[] inProp, byte[] beforeSound, long? soundSize = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gotune-test-{Guid.NewGuid():N}.dff");
        var prop = Chunk("PROP", null, Id("SND "),
            Chunk("FS  ", null, Big(Rate, 4)),
            inProp,
            Chunk("CHNL", null, Big(2, 2), Id("SLFT"), Id("SRGT")),
            Chunk("CMPR", null, Id("DSD "), new byte[2]));
        var sound = Chunk("DSD ", soundSize, Enumerable.Repeat((byte)0x69, 2000).ToArray());
        File.WriteAllBytes(path, Chunk("FRM8", null, Id("DSD "), prop, beforeSound, sound));
        return path;
    }

    private static void Patch(string path, long offset, byte[] bytes)
    {
        using var stream = File.OpenWrite(path);
        stream.Position = offset;
        stream.Write(bytes);
    }
}
