using TuneSync.Core.Models;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Tests;

/// <summary>Knowing which version of a network file a cached copy is, so a tag edit is never made
/// on an old copy and uploaded over a newer file.</summary>
public class RemoteVersionTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 11, 16, 9, 30, 0);
    private static readonly DateTime Tuesday = new(2026, 11, 17, 21, 5, 12);

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "TuneSyncTests", Guid.NewGuid().ToString("N"));

    public RemoteVersionTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { /* best effort */ }
    }

    /// <summary>A server whose folders list what they hold; nothing else is needed to look a file up.</summary>
    private sealed class FolderClient : IRemoteClient
    {
        public Dictionary<string, List<RemoteEntry>> Folders { get; } = new(StringComparer.Ordinal);
        public List<string> Listed { get; } = [];

        public Task TestAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct)
        {
            await Task.Yield(); // like a real listing, it finishes later
            Listed.Add(folder);
            return Folders.TryGetValue(folder, out var entries) ? entries : throw new IOException("경로를 찾을 수 없습니다.");
        }

        public long GetSize(string path) => throw new NotSupportedException();
        public Stream OpenRead(string path, long offset) => throw new NotSupportedException();
        public void Upload(string path, Stream content) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public void Stat_ListsTheFilesFolder_AndPicksTheFileOut()
    {
        var server = new FolderClient();
        server.Folders["/Music/K Pop"] =
        [
            new RemoteEntry("곡.flac", "/Music/K Pop/곡.flac", false, 31_000_000, Tuesday),
            new RemoteEntry("앨범", "/Music/K Pop/앨범", true, 0, Monday),
            new RemoteEntry("다른 곡.mp3", "/Music/K Pop/다른 곡.mp3", false, 4_000_000, Monday),
        ];
        server.Folders["/"] = [new RemoteEntry("a.mp3", "/a.mp3", false, 123, Monday)];
        IRemoteClient client = server;

        Assert.Equal(new RemoteVersion(31_000_000, Tuesday), RemoteVersion.Of(client.Stat("/Music/K Pop/곡.flac")!));
        Assert.Equal("/Music/K Pop", server.Listed[^1]);
        Assert.Null(client.Stat("/Music/K Pop/앨범"));        // a folder, not a file
        Assert.Null(client.Stat("/Music/K Pop/없는 곡.mp3")); // not there (any more)
        Assert.Equal(123, client.Stat("/a.mp3")!.Size);
        Assert.Equal("/", server.Listed[^1]);
    }

    [Fact]
    public void Versions_AreTheSame_OnlyWhenSizeAndDateAreKnownAndAlike()
    {
        var copy = new RemoteVersion(5_000_000, Monday);
        Assert.True(copy.IsSameAs(new RemoteVersion(5_000_000, Monday)));
        // Tags edited in place elsewhere: the same size, a later date.
        Assert.False(copy.IsSameAs(new RemoteVersion(5_000_000, Tuesday)));
        Assert.True(copy.DiffersFrom(new RemoteVersion(5_000_000, Tuesday)));
        // Without a date nothing is certain either way - except another size.
        var dateless = new RemoteVersion(5_000_000, default);
        Assert.False(dateless.IsSameAs(dateless));
        Assert.False(copy.DiffersFrom(dateless));
        Assert.True(copy.DiffersFrom(new RemoteVersion(5_100_000, default)));
    }

    [Fact]
    public void ACopyIsOutdated_OnlyWhenTheLastListingCertainlyShowsAnotherVersion()
    {
        var recorded = new RemoteVersion(5_000_000, Monday);
        // Changed elsewhere since: another date, or another size.
        Assert.True(RemoteVersion.IsOutdated(recorded, 5_000_000, new RemoteVersion(5_000_000, Tuesday)));
        Assert.True(RemoteVersion.IsOutdated(recorded, 5_000_000, new RemoteVersion(5_200_000, Tuesday)));
        // The same, or nothing certain to go by.
        Assert.False(RemoteVersion.IsOutdated(recorded, 5_000_000, new RemoteVersion(5_000_000, Monday)));
        Assert.False(RemoteVersion.IsOutdated(recorded, 5_000_000, new RemoteVersion(5_000_000, default)));
        Assert.False(RemoteVersion.IsOutdated(recorded, 5_000_000, null));
        // A copy of an unknown version (cached before versions were kept) goes by its size alone.
        Assert.False(RemoteVersion.IsOutdated(null, 5_000_000, new RemoteVersion(5_000_000, Tuesday)));
        Assert.True(RemoteVersion.IsOutdated(null, 5_000_000, new RemoteVersion(5_200_000, Tuesday)));
        // So does a copy that no longer has its recorded size (edited here and uploaded).
        Assert.False(RemoteVersion.IsOutdated(recorded, 5_300_000, new RemoteVersion(5_300_000, default)));
        Assert.Null(RemoteVersion.Held(recorded, 5_300_000));
    }

    [Fact]
    public void AFinishedDownload_HoldsTheVersionKnownBeforeIt_OnlyWhenItCameWhole()
    {
        var listed = new RemoteVersion(5_000_000, Monday);
        Assert.Equal(listed, RemoteVersion.OfDownload(listed, 5_000_000, 5_000_000));
        // Cut short, or changed on the server meanwhile: not known (fetched again before an edit).
        Assert.Null(RemoteVersion.OfDownload(listed, 5_000_000, 4_000_000));
        Assert.Null(RemoteVersion.OfDownload(listed, 5_000_000, 6_000_000));
        // The listing is of another size: only the size the server gave is certain.
        Assert.Equal(new RemoteVersion(5_200_000, default), RemoteVersion.OfDownload(listed, 5_200_000, 5_200_000));
        Assert.Equal(new RemoteVersion(5_200_000, default), RemoteVersion.OfDownload(null, 5_200_000, 5_200_000));
        // The server gave no size: the listing still tells, when it matches what came.
        Assert.Equal(listed, RemoteVersion.OfDownload(listed, 0, 5_000_000));
        Assert.Null(RemoteVersion.OfDownload(listed, 0, 4_000_000));
        Assert.Null(RemoteVersion.OfDownload(null, -1, 4_000_000));
    }

    [Fact]
    public void CopyVersions_SurviveARestart_AndForgetCopiesThatAreGone()
    {
        var file = Path.Combine(_temp, "cache-versions.json");
        var versions = new CopyVersions(file);
        versions.Set("A1B2.flac", new RemoteVersion(31_000_000, Tuesday));
        versions.Set("C3D4.mp3", new RemoteVersion(4_000_000, default));
        versions.Set("E5F6.mp3", new RemoteVersion(6_000_000, Monday));

        var reloaded = new CopyVersions(file);
        Assert.Equal(new RemoteVersion(31_000_000, Tuesday), reloaded.Get("a1b2.FLAC")); // file names, in any case
        Assert.Equal(new RemoteVersion(4_000_000, default), reloaded.Get("C3D4.mp3"));

        reloaded.Set("C3D4.mp3", null);              // being replaced by a download
        reloaded.Retain(["A1B2.flac", "C3D4.mp3"]);  // E5F6 was deleted by hand
        var again = new CopyVersions(file);
        Assert.NotNull(again.Get("A1B2.flac"));
        Assert.Null(again.Get("C3D4.mp3"));
        Assert.Null(again.Get("E5F6.mp3"));

        again.Forget(["A1B2.flac"]);                 // removed by the size limit
        Assert.Null(new CopyVersions(file).Get("A1B2.flac"));
    }

    [Fact]
    public void CopyVersions_ThatCantBeRead_KnowNothing()
    {
        var file = Path.Combine(_temp, "cache-versions.json");
        File.WriteAllText(file, "{ not json");
        Assert.Null(new CopyVersions(file).Get("A1B2.flac"));
    }

    [Fact]
    public void RememberSize_KeepsAKnownDate_WhileTheSizeStaysTheSame()
    {
        var path = RemotePath.Build("src", $"/Music/{Guid.NewGuid():N}.mp3");
        Assert.Null(RemoteFiles.LastSeen(path));

        RemoteFiles.RememberSize(path, 5_000_000, Monday);
        RemoteFiles.RememberSize(path, 5_000_000); // a listing that gives sizes only
        Assert.Equal(new RemoteVersion(5_000_000, Monday), RemoteFiles.LastSeen(path));

        RemoteFiles.RememberSize(path, 5_200_000); // another file now
        Assert.Equal(new RemoteVersion(5_200_000, default), RemoteFiles.LastSeen(path));
    }
}
