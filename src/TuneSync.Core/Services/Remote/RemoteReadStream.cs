namespace TuneSync.Core.Services.Remote;

/// <summary>A seekable, read-only view of a remote file that only fetches the parts actually read,
/// in 128 KB blocks (kept in a small cache). Reading a song's tags touches the start and maybe the
/// end of the file, so even a 50 MB FLAC costs a few hundred KB over the network.</summary>
public sealed class RemoteReadStream : Stream
{
    private const int BlockSize = 128 * 1024;
    private const int MaxCachedBlocks = 48;

    private readonly IRemoteClient _client;
    private readonly string _path;
    private readonly long _length;
    private readonly Dictionary<long, byte[]> _blocks = [];
    private readonly LinkedList<long> _recent = new();
    private long _position;

    // A connection left open after the last block, so reading straight on doesn't reconnect.
    private Stream? _live;
    private long _livePosition = -1;

    public RemoteReadStream(IRemoteClient client, string path, long length)
    {
        _client = client;
        _path = path;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (count > 0 && _position < _length)
        {
            var index = _position / BlockSize;
            var block = GetBlock(index);
            var inBlock = (int)(_position - index * BlockSize);
            var n = Math.Min(count, block.Length - inBlock);
            if (n <= 0) break;
            Buffer.BlockCopy(block, inBlock, buffer, offset, n);
            _position += n;
            offset += n;
            count -= n;
            total += n;
        }
        return total;
    }

    private byte[] GetBlock(long index)
    {
        if (_blocks.TryGetValue(index, out var cached))
        {
            _recent.Remove(index);
            _recent.AddFirst(index);
            return cached;
        }

        var start = index * BlockSize;
        var size = (int)Math.Min(BlockSize, _length - start);
        var data = new byte[size];
        if (_live is null || _livePosition != start)
        {
            _live?.Dispose();
            _live = _client.OpenRead(_path, start);
            _livePosition = start;
        }
        var read = 0;
        while (read < size)
        {
            var n = _live.Read(data, read, size - read);
            if (n == 0) break;
            read += n;
        }
        _livePosition += read;
        if (read < size) Array.Resize(ref data, read);

        _blocks[index] = data;
        _recent.AddFirst(index);
        if (_recent.Count > MaxCachedBlocks)
        {
            _blocks.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }
        return data;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _live?.Dispose();
        _live = null;
        base.Dispose(disposing);
    }
}
