using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TuneSync.Core.Models;
using TuneSync.Core.Services.Remote;

namespace TuneSync.App.Network;

/// <summary>Network songs are downloaded into a local cache while they play: playback starts from
/// the first bytes (through <see cref="LocalStreamServer"/>), the finished file stays for instant
/// replays, and the next song in the queue is fetched ahead. The cache keeps itself under the size
/// set in Settings, removing the songs played longest ago first. Each copy knows which version of
/// the server's file it is, so a song changed on the server since (from another PC, or another
/// app) is fetched again instead of being edited here and uploaded over the newer file.</summary>
public sealed class StreamingCache
{
    /// <summary>No download goes past this (or past the cache's own size, if larger): a server that
    /// keeps sending must not fill the disk. Roomy enough for DSD, where one long track can take
    /// several GB (DSD256/512).</summary>
    private const long MinDownloadCap = 16L << 30;

    /// <summary>Space a download leaves free on the drive, so a song never fills the disk up.</summary>
    internal const long FreeSpaceReserve = 1L << 30;

    private readonly Func<long> _limitBytes;
    private readonly ConcurrentDictionary<string, CacheItem> _items = new(StringComparer.Ordinal);

    /// <summary>Which version of the server's file each copy is. Kept beside the cache folder, not in
    /// it, so the size limit and 캐시 비우기 never count or remove it.</summary>
    private readonly CopyVersions _versions = new(TuneSync.Core.Services.AppDataFolder.Combine("cache-versions.json"));

    public StreamingCache(Func<long> limitBytes)
    {
        _limitBytes = limitBytes;
        Directory.CreateDirectory(Folder);
        // Leftovers of downloads cut off by closing the app.
        foreach (var partial in Directory.EnumerateFiles(Folder, "*.part"))
            try { File.Delete(partial); } catch { /* still in use - fine */ }
        // Copies removed meanwhile (by hand, through 캐시 폴더 열기) need no version any more.
        _versions.Retain(CopyNames());
    }

    public static string Folder { get; } = TuneSync.Core.Services.AppDataFolder.Combine("cache");

    /// <summary>The cache entry for a network song, starting its download if it isn't cached or
    /// already coming in.</summary>
    public CacheItem Open(string remotePath) => Open(remotePath, waiting: false);

    /// <param name="waiting">Counted as waiting for the finished file - from before its download
    /// starts, so a song played meanwhile doesn't stop it.</param>
    private CacheItem Open(string remotePath, bool waiting)
    {
        var item = _items.GetOrAdd(remotePath, NewItem);
        // A finished copy that is gone from disk (removed by the size limit, or by hand) or that the
        // last folder listing shows to be of another version than the server's is fetched again -
        // without asking the server, so playing a song costs no extra round trip. (A missing one
        // used to count as complete: the song then didn't play until a restart.)
        if (item.IsComplete && !item.IsDownloading && IsStale(item)) item = Renew(remotePath, item);
        return Start(item, null, waiting);
    }

    /// <param name="version">The server's version, when it was just looked up: the one a download
    /// started here holds.</param>
    private CacheItem Start(CacheItem item, RemoteVersion? version, bool waiting)
    {
        if (waiting) Interlocked.Increment(ref item.Waiters);
        item.Touch();
        // A download that is being stopped (another song was picked a moment ago) is told to
        // carry on instead: handing the player a song whose download is about to die cut the
        // stream short ("재생할 수 없습니다" / stuck at 0:00 when skipping back quickly).
        if (item.IsComplete || item.KeepGoing()) return item;
        item.StartDownload(version, Math.Max(_limitBytes(), MinDownloadCap), () => Trim());
        return item;
    }

    /// <summary>An entry for a song not opened yet: its copy on disk, if there is one.</summary>
    private CacheItem NewItem(string remotePath) => new(remotePath, FileFor(remotePath), _versions, adopt: true);

    /// <summary>Puts a new entry in place of a song's copy that can't be used as it is; its
    /// download replaces the file. (If someone else replaced it a moment ago, theirs is used.)</summary>
    private CacheItem Renew(string remotePath, CacheItem stale)
    {
        var fresh = new CacheItem(remotePath, stale.FilePath, _versions, adopt: false);
        return _items.TryUpdate(remotePath, fresh, stale) ? fresh : _items.GetOrAdd(remotePath, fresh);
    }

    /// <summary>A finished copy that can't be played as it is: gone from disk, or - as far as the
    /// last folder listing tells, without asking the server - of another version than the server's.</summary>
    private static bool IsStale(CacheItem item)
    {
        var file = new FileInfo(item.FilePath);
        return !file.Exists || RemoteVersion.IsOutdated(item.Version, file.Length, RemoteFiles.LastSeen(item.RemotePath));
    }

    /// <summary>Drops a song's cached copy (it no longer matches the server); the next play
    /// downloads it again.</summary>
    public void Forget(string remotePath)
    {
        if (_items.TryGetValue(remotePath, out var item))
        {
            // Already being fetched anew: that copy will be the server's. (Dropping it would let a
            // second download of the song start over the same partial file.)
            if (item.IsDownloading) return;
            _items.TryRemove(new KeyValuePair<string, CacheItem>(remotePath, item));
        }
        // Also when no entry holds the copy: left on disk, it would be taken up again as it is.
        var file = FileFor(remotePath);
        _versions.Set(System.IO.Path.GetFileName(file), null);
        try { File.Delete(file); } catch { /* in use - replaced by the next download */ }
    }

    /// <summary>The cache entry for a song if there is one, without starting a download.</summary>
    public CacheItem? Peek(string remotePath) => _items.TryGetValue(remotePath, out var item) ? item : null;

    /// <summary>Fetch ahead (the next song in the queue) so it starts instantly.</summary>
    public void Prefetch(string remotePath)
    {
        try { Open(remotePath); } catch { /* best effort */ }
    }

    /// <summary>The finished local copy (downloading and waiting for it if needed), for a tag edit -
    /// blocking, off the UI thread. The edit is uploaded over the server's file, so the copy must be
    /// the version the server holds now: one made on an older copy would undo whatever was changed
    /// there since (from another PC, or another app). Unless the copy was fetched after this call
    /// began, one look at the file's size and date on the server decides, and the song is fetched
    /// again unless the copy certainly is that version.</summary>
    public string CompletedPath(string remotePath)
    {
        var asked = CacheItem.NextSerial();
        var item = Open(remotePath, waiting: true);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                item.Completion.GetAwaiter().GetResult();
            }
            finally
            {
                Interlocked.Decrement(ref item.Waiters);
            }
            if (IsCurrent(item, asked, out var server)) return item.FilePath;
            // A file that never comes whole (or keeps changing) isn't edited at all.
            if (attempt == 3) throw new IOException("서버의 파일을 온전히 받아오지 못했습니다. 잠시 뒤 다시 시도해 주세요.");
            item = Start(Renew(remotePath, item), server, waiting: true);
        }
    }

    /// <summary>Whether a finished copy is certainly the version the server holds now.</summary>
    /// <param name="server">The server's version, when it was looked up (the copy fetched next holds it).</param>
    private static bool IsCurrent(CacheItem item, long asked, out RemoteVersion? server)
    {
        server = null;
        // Null when not known - from before versions were kept, or the file didn't come whole.
        var held = item.HeldVersion;
        // Fetched whole after the edit asked for it: nothing older can be in it.
        if (held is not null && item.FetchedSince(asked)) return true;
        try
        {
            var client = RemoteFiles.Client(item.RemotePath, out var path);
            if (client.Stat(path) is { } entry)
            {
                server = RemoteVersion.Of(entry);
                RemoteFiles.RememberSize(item.RemotePath, entry.Size, entry.Modified);
            }
        }
        catch
        {
            // Can't tell: fetched again (and if the server can't be reached, that download says so).
        }
        // Without a date on both, a change that kept the size (a tag edited elsewhere) can't be seen.
        return held is { } copy && server is { } now && copy.IsSameAs(now);
    }

    /// <param name="keep">False for work that's only useful while the song plays (the seek bar
    /// waves, the spectrum): playing another song may then stop the download.</param>
    public Task<string> CompletedPathAsync(string remotePath, bool keep = true)
    {
        var item = Open(remotePath, waiting: keep);
        return item.Completion.ContinueWith(t =>
        {
            if (keep) Interlocked.Decrement(ref item.Waiters);
            t.GetAwaiter().GetResult();
            return item.FilePath;
        }, TaskScheduler.Default);
    }

    /// <summary>A song starts playing: every other download stops, so its connection and
    /// bandwidth go to this one. (Skipping through network songs used to leave each one's full
    /// download running; FTP allows only a few connections, and the new song then waited for a
    /// free one.) Downloads someone is waiting on - a tag edit - carry on.</summary>
    public void Focus(string remotePath)
    {
        foreach (var (path, item) in _items)
        {
            if (string.Equals(path, remotePath, StringComparison.Ordinal)) continue;
            if (item.IsDownloading && Volatile.Read(ref item.Waiters) == 0) item.Cancel();
        }
    }

    public long UsedBytes => Directory.Exists(Folder)
        ? new DirectoryInfo(Folder).EnumerateFiles().Sum(f => { try { return f.Length; } catch { return 0L; } })
        : 0;

    /// <summary>Removes cached songs, oldest-played first, until under the limit (never the ones
    /// downloading or played in the last minute).</summary>
    public void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Folder).EnumerateFiles()
                .Where(f => f.Extension != ".part")
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            var limit = _limitBytes();
            var removed = new List<string>();
            foreach (var file in files)
            {
                if (total <= limit) break;
                if (DateTime.UtcNow - file.LastAccessTimeUtc < TimeSpan.FromMinutes(1)) continue;
                var owner = _items.Values.FirstOrDefault(i => string.Equals(i.FilePath, file.FullName, StringComparison.OrdinalIgnoreCase));
                if (owner is { IsDownloading: true }) continue;
                // Read before deleting: a deleted FileInfo can't tell its size. (Asking afterwards
                // threw, which skipped the rest - this count then never went down, so every copy
                // went, and the song's entry stayed "complete" without its file: it didn't play
                // again until a restart.)
                var size = file.Length;
                try
                {
                    file.Delete();
                }
                catch
                {
                    continue; // in use (playing) - leave it
                }
                total -= size;
                removed.Add(file.Name);
                // Only that entry: one put in its place meanwhile is fetching the song anew.
                if (owner is not null) _items.TryRemove(new KeyValuePair<string, CacheItem>(owner.RemotePath, owner));
            }
            _versions.Forget(removed);
        }
        catch
        {
            // cache folder unavailable - nothing to trim
        }
    }

    public void Clear()
    {
        foreach (var item in _items.Values.Where(i => !i.IsDownloading).ToList())
            _items.TryRemove(item.RemotePath, out _);
        foreach (var file in Directory.EnumerateFiles(Folder))
            try { File.Delete(file); } catch { /* playing right now */ }
        _versions.Retain(CopyNames());
    }

    /// <summary>The file names of the copies in the cache folder.</summary>
    private static IEnumerable<string> CopyNames() =>
        Directory.EnumerateFiles(Folder).Select(file => System.IO.Path.GetFileName(file));

    /// <summary>One cache file per song: a hash of its network path, keeping the extension.</summary>
    private static string FileFor(string remotePath)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(remotePath)))[..24];
        return System.IO.Path.Combine(Folder, hash + System.IO.Path.GetExtension(remotePath).ToLowerInvariant());
    }
}

/// <summary>A network song in the cache: either complete on disk or still arriving.</summary>
public sealed class CacheItem
{
    /// <summary>Numbers downloads and edits in the order they begin (see <see cref="FetchedSince"/>).</summary>
    private static long _serials;

    private readonly object _gate = new();
    private readonly CopyVersions _versions;
    private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _downloaded;
    private long _fetchedAt;
    private RemoteVersion? _version;
    private Exception? _failure;

    /// <summary>Callers waiting for the finished file (tag edits, analysis): such a download
    /// isn't stopped by <see cref="StreamingCache.Focus"/>.</summary>
    public int Waiters;

    private volatile bool _cancel;
    private bool _resume;
    private volatile Stream? _source;

    /// <summary>How long a download may go without a byte before its connection is given up.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(1);

    /// <summary>Called when the song is wanted again. True when its download is running - and if
    /// it was being stopped, it now continues from where it got to rather than ending.</summary>
    public bool KeepGoing()
    {
        lock (_gate)
        {
            if (!IsDownloading) return false;
            if (_cancel) _resume = true;
            return true;
        }
    }

    /// <summary>Stops the download (the partial file is dropped; it starts over if needed again).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (!IsDownloading) return;
            _cancel = true;
        }
        // Closing the connection's stream also ends a read blocked on the network.
        try { _source?.Dispose(); } catch { /* already closed */ }
    }

    /// <param name="versions">Where the version each copy holds is recorded.</param>
    /// <param name="adopt">A copy already on disk is taken as it is (complete, of the version it was
    /// recorded as); otherwise this entry's download replaces it.</param>
    internal CacheItem(string remotePath, string filePath, CopyVersions versions, bool adopt)
    {
        RemotePath = remotePath;
        FilePath = filePath;
        _versions = versions;
        if (adopt && File.Exists(filePath))
        {
            Length = _downloaded = new FileInfo(filePath).Length;
            _version = versions.Get(Name);
            _completion.SetResult();
        }
    }

    public string RemotePath { get; }
    public string FilePath { get; }
    public string PartPath => FilePath + ".part";

    /// <summary>The copy's file name, which its version is recorded under.</summary>
    private string Name => System.IO.Path.GetFileName(FilePath);

    /// <summary>Which version of the server's file the copy is, as recorded when it was fetched or
    /// uploaded; null when not known (fetched before versions were kept, or it didn't come whole).</summary>
    internal RemoteVersion? Version
    {
        get { lock (_gate) return _version; }
        private set { lock (_gate) _version = value; }
    }

    /// <summary>The recorded version, while the file on disk still has its size (null otherwise:
    /// changed here since, or not known).</summary>
    internal RemoteVersion? HeldVersion =>
        File.Exists(FilePath) ? RemoteVersion.Held(Version, new FileInfo(FilePath).Length) : null;

    /// <summary>Whether the copy comes from a download that began after <paramref name="serial"/>
    /// was taken (with <see cref="NextSerial"/>).</summary>
    internal bool FetchedSince(long serial) => Interlocked.Read(ref _fetchedAt) > serial;

    internal static long NextSerial() => Interlocked.Increment(ref _serials);

    /// <summary>Total size (known once the download starts, or from the finished file).</summary>
    public long Length { get; private set; }

    public long Downloaded => Interlocked.Read(ref _downloaded);
    public bool IsComplete => _completion.Task.IsCompletedSuccessfully;
    public bool IsDownloading { get; private set; }
    public Task Completion => _completion.Task;

    /// <summary>The file to read the bytes that have arrived so far from.</summary>
    public string ReadablePath => IsComplete ? FilePath : PartPath;

    public void Touch()
    {
        try { if (File.Exists(FilePath)) File.SetLastAccessTimeUtc(FilePath, DateTime.UtcNow); } catch { /* best effort */ }
    }

    /// <param name="version">The server's version, when it was just looked up; otherwise the last
    /// folder listing tells which version the download holds, as far as it can.</param>
    /// <param name="cap">The most it writes, whatever the server says or sends.</param>
    internal void StartDownload(RemoteVersion? version, long cap, Action onFinished)
    {
        lock (_gate)
        {
            if (IsDownloading || IsComplete) return;
            IsDownloading = true;
            _cancel = false;
            _resume = false;
            _failure = null;
            if (_completion.Task.IsCompleted) _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _downloaded, 0);
            Interlocked.Exchange(ref _fetchedAt, NextSerial());
        }

        Task.Run(() =>
        {
            // A connection that goes quiet without closing (Wi-Fi gone, the NAS asleep) left the read
            // waiting forever: the song never finished, and playing it again reused the dead
            // download. A minute without a byte closes the stream, which ends the read with an
            // error; the next play starts afresh.
            var stalled = false;
            var lastCount = -1L;
            var lastMove = Environment.TickCount64;
            using var watchdog = new System.Threading.Timer(_ =>
            {
                var count = Downloaded;
                if (count != lastCount)
                {
                    (lastCount, lastMove) = (count, Environment.TickCount64);
                    return;
                }
                if (Environment.TickCount64 - lastMove < StallLimit.TotalMilliseconds || _source is not { } quiet) return;
                stalled = true;
                try { quiet.Dispose(); } catch { /* already closed */ }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            try
            {
                // Taken before anything is read, so it is never newer than what arrives.
                var expected = version ?? RemoteFiles.LastSeen(RemotePath);
                var client = RemoteFiles.Client(RemotePath, out var path);
                var announced = client.GetSize(path);
                // A broken or hostile server mustn't fill the disk: nothing past the size it gave,
                // and nothing larger than any song needs, whatever size it gives.
                if (announced > cap) throw new IOException("파일이 너무 커서 내려받지 않았습니다.");
                if (announced > 0 && FreeSpace(FilePath) is { } free && free < announced + StreamingCache.FreeSpaceReserve)
                    throw new IOException("디스크 공간이 부족해 내려받지 못했습니다.");
                var most = announced > 0 ? announced : cap;
                Length = announced;
                using (var target = new FileStream(PartPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[256 * 1024];
                    while (true)
                    {
                        try
                        {
                            // From where it got to: after a stop that was called off, the bytes
                            // already here (and a player reading them) stay as they are.
                            using var source = client.OpenRead(path, Downloaded);
                            _source = source;
                            int read;
                            while (!_cancel && (read = source.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (Downloaded + read > most)
                                    throw new IOException(announced > 0
                                        ? "서버가 알린 크기보다 많은 데이터가 와서 내려받기를 멈췄습니다."
                                        : "파일이 너무 커서 내려받기를 멈췄습니다.");
                                target.Write(buffer, 0, read);
                                target.Flush();
                                Interlocked.Add(ref _downloaded, read);
                                lock (_gate) Monitor.PulseAll(_gate);
                            }
                        }
                        catch when (_cancel)
                        {
                            // the connection was closed to stop the download
                        }
                        finally
                        {
                            _source = null;
                        }
                        lock (_gate)
                        {
                            if (!_cancel) break; // all of it is here
                            if (!_resume) throw new OperationCanceledException("다른 곡을 재생해 내려받기를 멈췄습니다.");
                            _cancel = false;
                            _resume = false;
                        }
                    }
                }
                // The copy it replaces was another version: not known until this one is recorded.
                _versions.Set(Name, null);
                File.Move(PartPath, FilePath, overwrite: true);
                // Counts as just played. (It kept the time of its .part, so a download that took a
                // while was the first thing the size limit removed, right as it finished.)
                try { File.SetLastAccessTimeUtc(FilePath, DateTime.UtcNow); } catch { /* best effort */ }
                Length = new FileInfo(FilePath).Length;
                Version = RemoteVersion.OfDownload(expected, announced, Length);
                _versions.Set(Name, Version);
                Interlocked.Exchange(ref _downloaded, Length);
                _completion.TrySetResult();
            }
            catch (Exception ex)
            {
                if (stalled) ex = new IOException("서버가 1분 동안 응답하지 않아 내려받기를 멈췄습니다. 다시 재생하면 처음부터 받습니다.", ex);
                _failure = ex;
                try { File.Delete(PartPath); } catch { /* best effort */ }
                _completion.TrySetException(ex);
            }
            finally
            {
                _source = null;
                IsDownloading = false;
                lock (_gate) Monitor.PulseAll(_gate);
                onFinished();
            }
        });
    }

    /// <summary>Blocks until the byte at <paramref name="position"/> has arrived (or the download
    /// ends). False when it never will (failed, or past the end).</summary>
    public bool WaitFor(long position, CancellationToken ct)
    {
        lock (_gate)
        {
            while (Downloaded <= position && !IsComplete)
            {
                if (_failure is not null || ct.IsCancellationRequested) return false;
                if (!IsDownloading && !IsComplete) return false;
                Monitor.Wait(_gate, 200);
            }
            return Downloaded > position;
        }
    }

    /// <summary>The song was edited in the cache and uploaded: the file on disk is the new truth -
    /// its size and, with it, how much of it is here. (Leaving the "arrived" count at the old size
    /// made the stream server stop short of a file that grew - added lyrics - and the song then
    /// never started playing.) It is also the version the server holds now: the server is asked
    /// for its size and date once, so the next edit can use this copy instead of fetching the song
    /// again. Blocking; called off the UI thread, right after the upload.</summary>
    public void Refresh()
    {
        // A download under way replaces the file anyway, and its size is its own.
        if (!IsComplete || IsDownloading || !File.Exists(FilePath)) return;
        lock (_gate)
        {
            Length = new FileInfo(FilePath).Length;
            Interlocked.Exchange(ref _downloaded, Length);
            Monitor.PulseAll(_gate);
        }

        RemoteVersion? uploaded = null;
        try
        {
            var client = RemoteFiles.Client(RemotePath, out var path);
            if (client.Stat(path) is { } entry && entry.Size == Length)
            {
                uploaded = RemoteVersion.Of(entry);
                RemoteFiles.RememberSize(RemotePath, entry.Size, entry.Modified);
            }
        }
        catch
        {
            // Not known: the next edit fetches the song again first.
        }
        Version = uploaded;
        _versions.Set(Name, uploaded);
    }

    /// <summary>Free space on the drive a file goes to (null when it can't be told).</summary>
    private static long? FreeSpace(string file)
    {
        try { return new DriveInfo(System.IO.Path.GetPathRoot(file)!).AvailableFreeSpace; }
        catch { return null; }
    }
}
