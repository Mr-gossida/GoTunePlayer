using System.Net;
using System.Text;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Tests;

/// <summary>Network songs: paths, reading only what's needed, reading tags remotely, and edits
/// being written back to the server.</summary>
public class RemoteFilesTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "TuneSyncTests", Guid.NewGuid().ToString("N"));

    public RemoteFilesTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        RemoteFiles.ClientFor = null;
        RemoteFiles.LocalCopyOf = null;
        RemoteFiles.Uploaded = null;
        try { Directory.Delete(_temp, true); } catch { /* best effort */ }
    }

    /// <summary>A "server" holding files in memory, counting how many bytes were sent.</summary>
    private sealed class FakeClient : IRemoteClient
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public long BytesSent;
        public int Opens;

        public Task TestAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string folder, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>(Files.Select(f =>
                new RemoteEntry(f.Key.Split('/')[^1], f.Key, false, f.Value.Length, new DateTime(2026, 1, 1))).ToList());
        public long GetSize(string path) => Files[path].Length;

        public Stream OpenRead(string path, long offset)
        {
            Opens++;
            var data = Files[path];
            return new CountingStream(new MemoryStream(data, (int)offset, data.Length - (int)offset), n => BytesSent += n);
        }

        public void Upload(string path, Stream content)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            Files[path] = ms.ToArray();
        }

        public void Dispose() { }
    }

    private sealed class CountingStream(Stream inner, Action<int> counted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            counted(n);
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Asset(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", name));

    [Fact]
    public void RemotePath_RoundTrips()
    {
        var path = RemotePath.Build("nas1", "/Music/KPop/곡.flac");
        Assert.Equal("remote://nas1/Music/KPop/곡.flac", path);
        Assert.True(RemotePath.TryParse(path, out var id, out var remote));
        Assert.Equal("nas1", id);
        Assert.Equal("/Music/KPop/곡.flac", remote);
        Assert.False(RemotePath.IsRemote(@"\\server\share\a.mp3"));
        Assert.Equal(".flac", Path.GetExtension(path));
    }

    [Fact]
    public void RemoteReadStream_ReadsAnyRangeCorrectly_AndReusesBlocks()
    {
        var data = new byte[1_000_000];
        new Random(7).NextBytes(data);
        var client = new FakeClient();
        client.Files["/a.bin"] = data;

        using var stream = new RemoteReadStream(client, "/a.bin", data.Length);
        var buffer = new byte[5000];
        foreach (var at in new long[] { 0, 131_000, 999_000, 400_123, 0 })
        {
            stream.Position = at;
            var n = stream.Read(buffer, 0, buffer.Length);
            Assert.Equal(Math.Min(buffer.Length, data.Length - at), n);
            Assert.True(data.AsSpan((int)at, n).SequenceEqual(buffer.AsSpan(0, n)), $"bytes at {at}");
        }
        // Reading a few places in a 1 MB file must not have pulled the whole file.
        Assert.True(client.BytesSent < data.Length, $"sent {client.BytesSent} bytes");
    }

    [Fact]
    public void TagsOfARemoteFile_AreReadWithoutDownloadingAllOfIt()
    {
        var client = new FakeClient();
        var bytes = Asset("sample.flac");
        client.Files["/Music/sample.flac"] = bytes;
        RemoteFiles.ClientFor = id => id == "src" ? client : null;
        var path = RemotePath.Build("src", "/Music/sample.flac");

        var local = Path.Combine(_temp, "sample.flac");
        File.WriteAllBytes(local, bytes);
        var expected = TagService.Read(local);

        var (tags, _, _, duration) = TagService.ReadForLibrary(path);
        Assert.Equal(expected.Title, tags.Title);
        Assert.Equal(expected.Artist, tags.Artist);
        Assert.Equal(path, tags.Path);
        Assert.True(duration > TimeSpan.Zero);
    }

    [Fact]
    public void EditingARemoteFile_UploadsTheChangeBackToTheServer()
    {
        var client = new FakeClient();
        client.Files["/Music/sample.mp3"] = Asset("sample.mp3");
        RemoteFiles.ClientFor = _ => client;
        var cacheCopy = Path.Combine(_temp, "cached.mp3");
        RemoteFiles.LocalCopyOf = p =>
        {
            File.WriteAllBytes(cacheCopy, client.Files["/Music/sample.mp3"]); // what the streaming cache would hold
            return cacheCopy;
        };
        string? uploadedNotice = null;
        RemoteFiles.Uploaded = (p, _) => uploadedNotice = p;
        var path = RemotePath.Build("src", "/Music/sample.mp3");

        TagService.WriteLyrics(path, "[00:01.00]원격 가사");
        var tags = TagService.Read(path);
        tags = new TrackTags { Path = path, Title = "원격 제목", Artist = tags.Artist, Album = tags.Album };
        TagService.Write(tags);

        // The server's copy now carries both edits.
        var serverCopy = Path.Combine(_temp, "server.mp3");
        File.WriteAllBytes(serverCopy, client.Files["/Music/sample.mp3"]);
        Assert.Equal("원격 제목", TagService.Read(serverCopy).Title);
        Assert.Equal("[00:01.00]원격 가사", TagService.ReadLyrics(serverCopy));
        Assert.Equal(path, uploadedNotice);
    }

    [Fact]
    public async Task RemoteScan_ReadsNewSongs_AndTakesKnownOnesFromTheIndex()
    {
        var client = new FakeClient();
        client.Files["/Music/a.mp3"] = Asset("sample.mp3");
        client.Files["/Music/b.flac"] = Asset("sample.flac");
        RemoteFiles.ClientFor = _ => client;
        var index = new RemoteLibraryIndex(Path.Combine(_temp, "index.json"));

        var first = new List<LibraryFile>();
        await foreach (var f in MediaLibraryScanner.ScanRemoteAsync(client, "src", "/Music", true, index))
            first.Add(f);
        Assert.Equal(2, first.Count);
        index.Save();

        client.Opens = 0;
        var reloaded = new RemoteLibraryIndex(Path.Combine(_temp, "index.json"));
        var second = new List<LibraryFile>();
        await foreach (var f in MediaLibraryScanner.ScanRemoteAsync(client, "src", "/Music", true, reloaded))
            second.Add(f);
        Assert.Equal(2, second.Count);
        Assert.Equal(0, client.Opens); // nothing re-read: all from the index
        Assert.Equal(first.Select(f => f.Tags.Title).Order(), second.Select(f => f.Tags.Title).Order());
    }

    // --- WebDAV against a scripted HTTP server ---

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Send(request, ct));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Respond(request);
        }
    }

    [Fact]
    public async Task WebDav_ListsFoldersAndFiles_RelativeToTheBaseAddress()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/dav/music/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/dav/music/K%20Pop/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>https://nas.local/dav/music/%EA%B3%A1.mp3</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength><d:getlastmodified>Wed, 01 Jan 2026 10:00:00 GMT</d:getlastmodified></d:prop></d:propstat></d:response>
            </d:multistatus>
            """;
        var handler = new ScriptedHandler { Respond = _ => new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(xml) } };
        using var client = new WebDavRemoteClient(new RemoteSource { Kind = RemoteKind.WebDav, Address = "https://nas.local/dav/music" }, "", handler);

        var entries = await client.ListAsync("/", CancellationToken.None);

        Assert.Equal("PROPFIND", handler.Requests[0].Method.Method);
        Assert.Equal("1", handler.Requests[0].Headers.GetValues("Depth").Single());
        Assert.Equal(2, entries.Count); // the folder itself is left out
        var folder = entries.Single(e => e.IsDirectory);
        Assert.Equal("K Pop", folder.Name);
        Assert.Equal("/K Pop", folder.Path);
        var song = entries.Single(e => !e.IsDirectory);
        Assert.Equal("곡.mp3", song.Name);
        Assert.Equal("/곡.mp3", song.Path);
        Assert.Equal(1234, song.Size);
    }

    [Fact]
    public void WebDav_ReadsFromAnOffset_AndUploadsViaTemporaryNameThenMove()
    {
        var handler = new ScriptedHandler
        {
            Respond = r => r.Method.Method switch
            {
                "GET" => new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3]) },
                "PUT" => new HttpResponseMessage(HttpStatusCode.Created),
                "MOVE" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.OK),
            },
        };
        using var client = new WebDavRemoteClient(new RemoteSource { Kind = RemoteKind.WebDav, Address = "https://nas.local/dav" }, "pw", handler);

        using (var s = client.OpenRead("/Music/a b.mp3", 500)) { var buf = new byte[3]; Assert.Equal(3, s.Read(buf, 0, 3)); }
        Assert.Equal(500, handler.Requests[0].Headers.Range!.Ranges.Single().From);
        Assert.Equal("https://nas.local/dav/Music/a%20b.mp3", handler.Requests[0].RequestUri!.AbsoluteUri);

        client.Upload("/Music/a b.mp3", new MemoryStream(Encoding.UTF8.GetBytes("new")));
        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.EndsWith(".tunesync-upload", put.RequestUri!.AbsoluteUri);
        var move = handler.Requests.Single(r => r.Method.Method == "MOVE");
        Assert.Equal("https://nas.local/dav/Music/a%20b.mp3", move.Headers.GetValues("Destination").Single());
        Assert.Equal("T", move.Headers.GetValues("Overwrite").Single());
    }
}
