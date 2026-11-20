using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Tests;

/// <summary>Walking a network folder whose server lists oddly - a folder inside itself, a parent,
/// a loop: every song comes once and the walk ends.</summary>
public class MediaLibraryScannerTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A server listing what the test sets up; any other folder can't be read.</summary>
    private sealed class ScriptedServer : IRemoteClient
    {
        public Dictionary<string, RemoteEntry[]> Folders { get; } = new(StringComparer.Ordinal);
        public Func<string, RemoteEntry[]>? Others { get; init; }
        public List<string> Listed { get; } = [];

        public Task TestAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct)
        {
            Listed.Add(folder);
            // A walk in circles is cut off here instead of hanging the test run.
            if (Listed.Count > 200) throw new IOException("Listed too many folders");
            var entries = Folders.TryGetValue(folder, out var known) ? known
                : Others?.Invoke(folder) ?? throw new IOException("No such folder");
            return Task.FromResult<IReadOnlyList<RemoteEntry>>(entries);
        }

        // Every song comes from the index, so no file is ever opened.
        public long GetSize(string path) => throw new NotSupportedException();
        public Stream OpenRead(string path, long offset) => throw new NotSupportedException();
        public void Upload(string path, Stream content) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static RemoteEntry Folder(string path) => new(NameOf(path), path, true, 0, Stamp);
    private static RemoteEntry Song(string path) => new(NameOf(path), path, false, 1000, Stamp);
    private static string NameOf(string path) => path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>Scans <paramref name="folder"/> with the given songs already in the index (so the
    /// scan never opens a file) and returns the remote paths of the songs it produced, in order.</summary>
    private static async Task<List<string>> ScanAsync(ScriptedServer server, string folder, params string[] songs)
    {
        var sourceId = "scan-" + Guid.NewGuid().ToString("N"); // shares no remote path with other tests
        var index = new LibraryIndex(Path.Combine(Path.GetTempPath(), $"tunesync-scan-{Guid.NewGuid():N}.json"));
        foreach (var song in songs)
        {
            var path = RemotePath.Build(sourceId, song);
            index.Put(new LibraryFile { Path = path, Tags = new TrackTags { Path = path } }, 1000, Stamp);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var found = new List<string>();
        await foreach (var file in MediaLibraryScanner.ScanRemoteAsync(server, sourceId, folder, true, index, timeout.Token))
            found.Add(RemotePath.TryParse(file.Path, out _, out var remote) ? remote : file.Path);
        return found;
    }

    [Fact]
    public async Task A_folder_the_server_reports_inside_itself_in_another_case_is_walked_once()
    {
        // Asked for /Music, a server that ignores case answers in its own spelling, /music.
        var server = new ScriptedServer();
        server.Folders["/Music"] = [Folder("/music"), Song("/music/a.mp3"), Song("/music/b.flac")];
        server.Folders["/music"] = [Song("/music/a.mp3"), Song("/music/b.flac")];

        var found = await ScanAsync(server, "/Music", "/music/a.mp3", "/music/b.flac");

        Assert.Equal(["/music/a.mp3", "/music/b.flac"], found);
        Assert.Equal(["/Music"], server.Listed);
    }

    [Fact]
    public async Task The_folder_itself_its_parent_and_the_root_are_not_walked_as_subfolders()
    {
        var server = new ScriptedServer();
        server.Folders["/Music/KPop"] =
        [
            Folder("/"), Folder("/Music"), Folder("Music/KPop/"), // the root, the parent, itself as a relative href
            Song("/Music/KPop/a.mp3"),
        ];
        server.Folders["/Music"] = [Folder("/Music/KPop"), Song("/Music/b.mp3")];
        server.Folders["/"] = [Folder("/Music"), Song("/c.mp3")];

        var found = await ScanAsync(server, "/Music/KPop", "/Music/KPop/a.mp3", "/Music/b.mp3", "/c.mp3");

        Assert.Equal(["/Music/KPop/a.mp3"], found);
        Assert.Equal(["/Music/KPop"], server.Listed);
    }

    [Fact]
    public async Task Folders_that_contain_each_other_are_walked_once_each()
    {
        var server = new ScriptedServer();
        server.Folders["/Music"] = [Folder("/Music/A"), Folder("/Music/B")];
        server.Folders["/Music/A"] = [Folder("/Music/B"), Song("/Music/A/a.mp3")];
        server.Folders["/Music/B"] = [Folder("/Music/A"), Song("/Music/B/b.mp3")];

        var found = await ScanAsync(server, "/Music", "/Music/A/a.mp3", "/Music/B/b.mp3");

        Assert.Equal(["/Music/A/a.mp3", "/Music/B/b.mp3"], found);
        Assert.Equal(["/Music", "/Music/A", "/Music/B"], server.Listed);
    }

    [Fact]
    public async Task A_chain_of_folders_that_never_ends_is_cut_off()
    {
        // A link loop the server follows: every folder holds one more, each time under a new path.
        var server = new ScriptedServer { Others = folder => [Folder(folder + "/loop")] };

        var found = await ScanAsync(server, "/Music");

        Assert.Empty(found);
        Assert.InRange(server.Listed.Count, 10, 100);
    }

    [Fact]
    public async Task A_song_listed_twice_comes_once_and_folders_differing_only_in_case_are_both_walked()
    {
        // Remote paths are case-sensitive: /Music/live and /Music/Live can be two folders.
        var server = new ScriptedServer();
        server.Folders["/Music"] = [Song("/Music/a.mp3"), Song("/Music/a.mp3"), Folder("/Music/live"), Folder("/Music/Live")];
        server.Folders["/Music/live"] = [Song("/Music/live/x.mp3")];
        server.Folders["/Music/Live"] = [Song("/Music/Live/x.mp3")];

        var found = await ScanAsync(server, "/Music", "/Music/a.mp3", "/Music/live/x.mp3", "/Music/Live/x.mp3");

        Assert.Equal(["/Music/a.mp3", "/Music/live/x.mp3", "/Music/Live/x.mp3"], found);
    }
}
