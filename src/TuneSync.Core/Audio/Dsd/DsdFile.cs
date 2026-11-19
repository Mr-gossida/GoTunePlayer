using System.Buffers.Binary;
using System.Text;

namespace TuneSync.Core.Audio.Dsd;

/// <summary>A DSD file: Sony's DSF or Philips' DSDIFF (.dff, uncompressed DSD only - DST-compressed
/// files are refused). Hands out each channel's bytes in DSD's usual bit order (the first bit in
/// the top bit of each byte), whatever order the file keeps them in.</summary>
public sealed class DsdFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly bool _dsf;
    private readonly bool _lsbFirst;
    private readonly long _dataOffset;
    private readonly int _blockSize;          // DSF: bytes per channel per block
    private byte[] _block = [];
    private long _blockIndex = -1;
    private long _position;                   // next byte (per channel) to read

    public int Channels { get; private set; }

    /// <summary>Bits per second per channel (2 822 400 for DSD64).</summary>
    public int SampleRate { get; private set; }

    /// <summary>Bytes of sound per channel.</summary>
    public long Length { get; private set; }

    public TimeSpan Duration => TimeSpan.FromSeconds(Length * 8.0 / SampleRate);

    /// <summary>"DSD64", "DSD128"... (multiples of 44.1 kHz × 64; the 48 kHz family is named the same way).</summary>
    public string Name => $"DSD{SampleRate / (SampleRate % 44100 == 0 ? 44100 : 48000)}";

    /// <summary>Title and artist from a DSDIFF file's own DIIN chunk (DSF keeps an ID3 tag instead).</summary>
    public string? Title { get; private set; }
    public string? Artist { get; private set; }

    public static bool IsDsdPath(string? path) =>
        path is not null && Path.GetExtension(path).ToLowerInvariant() is ".dsf" or ".dff";

    public DsdFile(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        try
        {
            var id = ReadId();
            if (id == "DSD ")
            {
                _dsf = true;
                Skip(8 + 8 + 8);                              // chunk size, file size, metadata offset
                if (ReadId() != "fmt ") throw Bad("no fmt chunk");
                var fmtSize = ReadLittle64();
                var fmtStart = _stream.Position;
                Skip(4);                                      // format version
                if (ReadLittle32() != 0) throw Bad("not raw DSD");
                Skip(4);                                      // channel type
                Channels = (int)ReadLittle32();
                SampleRate = (int)ReadLittle32();
                _lsbFirst = ReadLittle32() == 1;
                var samples = ReadLittle64();
                _blockSize = (int)ReadLittle32();
                _stream.Position = fmtStart + fmtSize - 12;
                if (ReadId() != "data") throw Bad("no data chunk");
                var dataSize = ReadLittle64() - 12;
                _dataOffset = _stream.Position;
                Length = Math.Min((long)Math.Ceiling(samples / 8.0), dataSize / Math.Max(1, Channels));
            }
            else if (id == "FRM8")
            {
                Skip(8);
                if (ReadId() != "DSD ") throw Bad("not a DSD form");
                var found = false;
                while (_stream.Position + 12 <= _stream.Length && !found)
                {
                    var chunk = ReadId();
                    var size = ReadBig64();
                    var start = _stream.Position;
                    switch (chunk)
                    {
                        case "PROP":
                            ReadProperties(start + size);
                            break;
                        case "DSD ":
                            _dataOffset = start;
                            Length = size / Math.Max(1, Channels);
                            found = true;
                            break;
                        case "DST ":
                            throw new NotSupportedException("DST-compressed DSDIFF isn't supported.");
                        case "DIIN":
                            ReadInfo(start + size);
                            break;
                    }
                    if (!found) _stream.Position = start + size + (size & 1);
                }
                if (!found) throw Bad("no sound");
                _lsbFirst = false;
            }
            else
            {
                throw Bad("not a DSF or DSDIFF file");
            }
            if (Channels is < 1 or > 8 || SampleRate < 2_000_000 || SampleRate % 8 != 0) throw Bad("unsupported layout");
            if (_dsf && _blockSize <= 0) throw Bad("bad block size");
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    private void ReadProperties(long end)
    {
        if (ReadId() != "SND ") return;
        while (_stream.Position + 12 <= end)
        {
            var chunk = ReadId();
            var size = ReadBig64();
            var start = _stream.Position;
            switch (chunk)
            {
                case "FS  ":
                    SampleRate = (int)ReadBig32();
                    break;
                case "CHNL":
                    Channels = ReadBig16();
                    break;
                case "CMPR":
                    if (ReadId() != "DSD ") throw new NotSupportedException("Compressed DSDIFF isn't supported.");
                    break;
            }
            _stream.Position = start + size + (size & 1);
        }
    }

    private void ReadInfo(long end)
    {
        while (_stream.Position + 12 <= end)
        {
            var chunk = ReadId();
            var size = ReadBig64();
            var start = _stream.Position;
            if (chunk is "DITI" or "DIAR" && size > 4)
            {
                var count = (int)Math.Min(ReadBig32(), size - 4);
                var bytes = new byte[count];
                _stream.ReadExactly(bytes);
                var text = Encoding.Latin1.GetString(bytes).TrimEnd('\0', ' ');
                if (chunk == "DITI") Title = text;
                else Artist = text;
            }
            _stream.Position = start + size + (size & 1);
        }
    }

    /// <summary>The next read starts at this byte (per channel).</summary>
    public void Seek(long byteIndex) => _position = Math.Clamp(byteIndex, 0, Length);

    public long Position => _position;

    /// <summary>Reads up to <paramref name="count"/> bytes of each channel into
    /// <paramref name="channels"/> (one array per channel). Returns how many; 0 at the end.</summary>
    public int Read(byte[][] channels, int count)
    {
        count = (int)Math.Min(count, Length - _position);
        if (count <= 0) return 0;
        if (_dsf)
        {
            var done = 0;
            while (done < count)
            {
                var index = (_position + done) / _blockSize;
                var at = (int)((_position + done) % _blockSize);
                LoadBlock(index);
                var take = Math.Min(count - done, _blockSize - at);
                for (var c = 0; c < Channels; c++)
                {
                    var source = _block.AsSpan(c * _blockSize + at, take);
                    var target = channels[c].AsSpan(done, take);
                    if (_lsbFirst)
                        for (var i = 0; i < take; i++) target[i] = Reverse[source[i]];
                    else
                        source.CopyTo(target);
                }
                done += take;
            }
        }
        else
        {
            var raw = new byte[count * Channels];
            _stream.Position = _dataOffset + _position * Channels;
            _stream.ReadExactly(raw);
            for (var i = 0; i < count; i++)
                for (var c = 0; c < Channels; c++)
                    channels[c][i] = raw[i * Channels + c];
        }
        _position += count;
        return count;
    }

    private void LoadBlock(long index)
    {
        if (index == _blockIndex) return;
        var size = _blockSize * Channels;
        if (_block.Length != size) _block = new byte[size];
        _stream.Position = _dataOffset + index * size;
        var read = _stream.ReadAtLeast(_block, size, throwOnEndOfStream: false);
        if (read < size) _block.AsSpan(read).Fill(0x69);
        _blockIndex = index;
    }

    /// <summary>Each byte with its bits in the opposite order (DSF keeps the first bit at the bottom).</summary>
    private static readonly byte[] Reverse = BuildReverse();

    private static byte[] BuildReverse()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var r = 0;
            for (var b = 0; b < 8; b++) if ((i >> b & 1) != 0) r |= 0x80 >> b;
            table[i] = (byte)r;
        }
        return table;
    }

    private string ReadId()
    {
        Span<byte> b = stackalloc byte[4];
        _stream.ReadExactly(b);
        return Encoding.ASCII.GetString(b);
    }

    private void Skip(int bytes) => _stream.Position += bytes;

    private uint ReadLittle32() { Span<byte> b = stackalloc byte[4]; _stream.ReadExactly(b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    private long ReadLittle64() { Span<byte> b = stackalloc byte[8]; _stream.ReadExactly(b); return BinaryPrimitives.ReadInt64LittleEndian(b); }
    private ushort ReadBig16() { Span<byte> b = stackalloc byte[2]; _stream.ReadExactly(b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
    private uint ReadBig32() { Span<byte> b = stackalloc byte[4]; _stream.ReadExactly(b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    private long ReadBig64() { Span<byte> b = stackalloc byte[8]; _stream.ReadExactly(b); return BinaryPrimitives.ReadInt64BigEndian(b); }

    private static InvalidDataException Bad(string why) => new($"Not a readable DSD file: {why}.");

    public void Dispose() => _stream.Dispose();
}
