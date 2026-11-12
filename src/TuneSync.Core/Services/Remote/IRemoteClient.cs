using TuneSync.Core.Models;

namespace TuneSync.Core.Services.Remote;

/// <summary>What TuneSync needs from a network location: browse folders, read a file from any
/// offset (for streaming and for reading tags without downloading the whole file), and write a
/// file back (tag/lyrics/cover edits). Implementations are safe to use from several threads.</summary>
public interface IRemoteClient : IDisposable
{
    /// <summary>Connects and lists the root, throwing a readable error when it can't.</summary>
    Task TestAsync(CancellationToken ct);

    Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct);

    long GetSize(string path);

    /// <summary>A forward-only stream of the file from <paramref name="offset"/>.</summary>
    Stream OpenRead(string path, long offset);

    /// <summary>Replaces the file with <paramref name="content"/> - uploaded under a temporary name
    /// first and then renamed over the original, so a dropped connection never leaves a
    /// half-written song behind.</summary>
    void Upload(string path, Stream content);
}

public static class RemoteClientFactory
{
    public static IRemoteClient Create(RemoteSource source, string password) => source.Kind switch
    {
        RemoteKind.WebDav => new WebDavRemoteClient(source, password),
        RemoteKind.Ftp or RemoteKind.Ftps => new FtpRemoteClient(source, password),
        RemoteKind.Sftp => new SftpRemoteClient(source, password),
        _ => throw new NotSupportedException($"{source.KindLabel}은(는) 파일 시스템 경로로 접근합니다."),
    };
}

/// <summary>Wraps a stream so a pooled connection goes back to the pool when it's disposed.</summary>
internal sealed class ReturningStream(Stream inner, Action onDispose) : Stream
{
    private bool _disposed;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            try { inner.Dispose(); } catch { /* connection already gone */ }
            onDispose();
        }
        base.Dispose(disposing);
    }
}
