using System.Collections.Concurrent;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services.Remote;

/// <summary>The one place tags are opened from, local or remote. Every tag/cover/lyrics read and
/// write in TuneSync goes through <see cref="Open"/> / <see cref="Edit"/>, so the tag manager,
/// auto-fill and "가사 찾기" work on network files exactly as on local ones:
/// reads fetch only the parts of the file they need; edits are made on a local copy (the
/// streaming cache's) and then uploaded back over the original.</summary>
public static class RemoteFiles
{
    /// <summary>Source id → its connected client (set up by the app).</summary>
    public static Func<string, IRemoteClient?>? ClientFor { get; set; }

    /// <summary>A complete local copy of a remote file, downloading it if needed (the app's
    /// streaming cache). Blocking; called off the UI thread. It must be the version the server
    /// holds now: the edit is uploaded over the server's file, and one made on an older copy would
    /// undo whatever was changed there since.</summary>
    public static Func<string, string>? LocalCopyOf { get; set; }

    /// <summary>Told after a local copy was edited and uploaded (so the cache keeps it).</summary>
    public static Action<string, string>? Uploaded { get; set; }

    /// <summary>Told when an edit of the local copy couldn't be uploaded: that copy no longer
    /// matches the server and must be fetched again.</summary>
    public static Action<string>? Discarded { get; set; }

    private static readonly ConcurrentDictionary<string, RemoteVersion> Seen = new(StringComparer.Ordinal);

    /// <summary>Sizes are known from folder listings; remembering them saves a round trip per file.
    /// (A date known from before is kept while the size stays the same.)</summary>
    public static void RememberSize(string path, long size) =>
        Seen.AddOrUpdate(path, new RemoteVersion(size, default),
            (_, seen) => seen.Size == size ? seen : new RemoteVersion(size, default));

    /// <summary>The size and date a folder listing gives. With the date, the streaming cache knows
    /// which version a song it fetches is, and notices a copy the server has replaced since -
    /// without asking the server.</summary>
    public static void RememberSize(string path, long size, DateTime modified) => Seen[path] = new RemoteVersion(size, modified);

    /// <summary>What the server was last seen to hold for a file (folder listings, reads, uploads), if known.</summary>
    public static RemoteVersion? LastSeen(string path) => Seen.TryGetValue(path, out var seen) ? seen : null;

    public static IRemoteClient Client(string path, out string remotePath)
    {
        if (!RemotePath.TryParse(path, out var sourceId, out remotePath))
            throw new ArgumentException("원격 경로가 아닙니다.", nameof(path));
        return ClientFor?.Invoke(sourceId) ?? throw new IOException("연결 정보를 찾을 수 없는 네트워크 위치입니다.");
    }

    public static TagLib.File Open(string path)
    {
        if (!RemotePath.IsRemote(path)) return TagLib.File.Create(path);
        var client = Client(path, out var remotePath);
        long size;
        if (Seen.TryGetValue(path, out var seen)) size = seen.Size;
        else RememberSize(path, size = client.GetSize(remotePath));
        return TagLib.File.Create(new Abstraction(path, new RemoteReadStream(client, remotePath, size)));
    }

    /// <summary>"Used by another process" (ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION).</summary>
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Changes a file's tags. Remote files are edited on a local copy that is then
    /// uploaded back over the original (via a temporary name, so a dropped connection never leaves
    /// a broken file on the server).</summary>
    public static void Edit(string path, Action<TagLib.File> edit)
    {
        if (!RemotePath.IsRemote(path))
        {
            // Something may still be reading the file for a moment (the spectrum analysis of a
            // song that just started, an indexer, a virus scan): wait for it instead of failing.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var local = TagLib.File.Create(path);
                    edit(local);
                    local.Save();
                    return;
                }
                catch (IOException ex) when (attempt < 12 && IsSharingViolation(ex))
                {
                    Thread.Sleep(250);
                }
            }
        }

        var copy = LocalCopyOf?.Invoke(path) ?? throw new IOException("원격 파일을 받아오지 못했습니다.");
        try
        {
            using (var file = TagLib.File.Create(copy))
            {
                edit(file);
                file.Save();
            }

            var client = Client(path, out var remotePath);
            using (var content = File.OpenRead(copy))
                client.Upload(remotePath, content);
        }
        catch
        {
            Seen.TryRemove(path, out _);
            Discarded?.Invoke(path);
            throw;
        }
        // The upload gave the file a new date, not known until the server is asked again (the
        // cache does that, told below).
        Seen[path] = new RemoteVersion(new FileInfo(copy).Length, default);
        Uploaded?.Invoke(path, copy);
    }

    /// <summary>TagLib reading a remote file through <see cref="RemoteReadStream"/>. The name keeps
    /// the real extension, which is how TagLib picks the format.</summary>
    private sealed class Abstraction(string name, Stream stream) : TagLib.File.IFileAbstraction
    {
        public string Name { get; } = name;
        public Stream ReadStream { get; } = stream;
        public Stream WriteStream => throw new NotSupportedException("원격 파일은 Edit으로 수정합니다.");
        public void CloseStream(Stream stream) => stream.Dispose();
    }
}
