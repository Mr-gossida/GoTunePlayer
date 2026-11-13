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
    /// streaming cache). Blocking; called off the UI thread.</summary>
    public static Func<string, string>? LocalCopyOf { get; set; }

    /// <summary>Told after a local copy was edited and uploaded (so the cache keeps it).</summary>
    public static Action<string, string>? Uploaded { get; set; }

    /// <summary>Told when an edit of the local copy couldn't be uploaded: that copy no longer
    /// matches the server and must be fetched again.</summary>
    public static Action<string>? Discarded { get; set; }

    private static readonly ConcurrentDictionary<string, long> Sizes = new(StringComparer.Ordinal);

    /// <summary>Sizes are known from folder listings; remembering them saves a round trip per file.</summary>
    public static void RememberSize(string path, long size) => Sizes[path] = size;

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
        if (!Sizes.TryGetValue(path, out var size)) Sizes[path] = size = client.GetSize(remotePath);
        return TagLib.File.Create(new Abstraction(path, new RemoteReadStream(client, remotePath, size)));
    }

    /// <summary>Changes a file's tags. Remote files are edited on a local copy that is then
    /// uploaded back over the original (via a temporary name, so a dropped connection never leaves
    /// a broken file on the server).</summary>
    public static void Edit(string path, Action<TagLib.File> edit)
    {
        if (!RemotePath.IsRemote(path))
        {
            using var local = TagLib.File.Create(path);
            edit(local);
            local.Save();
            return;
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
            Sizes.TryRemove(path, out _);
            Discarded?.Invoke(path);
            throw;
        }
        Sizes[path] = new FileInfo(copy).Length;
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
