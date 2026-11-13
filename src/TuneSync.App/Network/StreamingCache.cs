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
/// set in Settings, removing the songs played longest ago first.</summary>
public sealed class StreamingCache
{
    private readonly Func<long> _limitBytes;
    private readonly ConcurrentDictionary<string, CacheItem> _items = new(StringComparer.Ordinal);

    public StreamingCache(Func<long> limitBytes)
    {
        _limitBytes = limitBytes;
        Directory.CreateDirectory(Folder);
        // Leftovers of downloads cut off by closing the app.
        foreach (var partial in Directory.EnumerateFiles(Folder, "*.part"))
            try { File.Delete(partial); } catch { /* still in use - fine */ }
    }

    public static string Folder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneSync", "cache");

    /// <summary>The cache entry for a network song, starting its download if it isn't cached or
    /// already coming in.</summary>
    public CacheItem Open(string remotePath)
    {
        var item = _items.GetOrAdd(remotePath, path => new CacheItem(path, FileFor(path)));
        item.Touch();
        // A download that is being stopped (another song was picked a moment ago) is told to
        // carry on instead: handing the player a song whose download is about to die cut the
        // stream short ("재생할 수 없습니다" / stuck at 0:00 when skipping back quickly).
        if (item.IsComplete || item.KeepGoing()) return item;
        item.StartDownload(() => Trim());
        return item;
    }

    /// <summary>Drops a song's cached copy (it no longer matches the server); the next play
    /// downloads it again.</summary>
    public void Forget(string remotePath)
    {
        if (_items.TryRemove(remotePath, out var item))
        {
            try { File.Delete(item.FilePath); } catch { /* in use - replaced by the next download */ }
        }
    }

    /// <summary>The cache entry for a song if there is one, without starting a download.</summary>
    public CacheItem? Peek(string remotePath) => _items.TryGetValue(remotePath, out var item) ? item : null;

    /// <summary>Fetch ahead (the next song in the queue) so it starts instantly.</summary>
    public void Prefetch(string remotePath)
    {
        try { Open(remotePath); } catch { /* best effort */ }
    }

    /// <summary>The finished local copy (downloading and waiting for it if needed). Blocking -
    /// used for tag edits and waveform analysis, both off the UI thread.</summary>
    public string CompletedPath(string remotePath)
    {
        var item = Open(remotePath);
        Interlocked.Increment(ref item.Waiters);
        try
        {
            item.Completion.GetAwaiter().GetResult();
            return item.FilePath;
        }
        finally
        {
            Interlocked.Decrement(ref item.Waiters);
        }
    }

    /// <param name="keep">False for work that's only useful while the song plays (the seek bar
    /// waves, the spectrum): playing another song may then stop the download.</param>
    public Task<string> CompletedPathAsync(string remotePath, bool keep = true)
    {
        var item = Open(remotePath);
        if (keep) Interlocked.Increment(ref item.Waiters);
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
            foreach (var file in files)
            {
                if (total <= limit) break;
                if (DateTime.UtcNow - file.LastAccessTimeUtc < TimeSpan.FromMinutes(1)) continue;
                var owner = _items.Values.FirstOrDefault(i => string.Equals(i.FilePath, file.FullName, StringComparison.OrdinalIgnoreCase));
                if (owner is { IsDownloading: true }) continue;
                try
                {
                    file.Delete();
                    total -= file.Length;
                    if (owner is not null) _items.TryRemove(owner.RemotePath, out _);
                }
                catch
                {
                    // in use (playing) - leave it
                }
            }
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
    }

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
    private readonly object _gate = new();
    private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _downloaded;
    private Exception? _failure;

    /// <summary>Callers waiting for the finished file (tag edits, analysis): such a download
    /// isn't stopped by <see cref="StreamingCache.Focus"/>.</summary>
    public int Waiters;

    private volatile bool _cancel;
    private bool _resume;
    private Stream? _source;

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

    public CacheItem(string remotePath, string filePath)
    {
        RemotePath = remotePath;
        FilePath = filePath;
        if (File.Exists(filePath))
        {
            Length = _downloaded = new FileInfo(filePath).Length;
            _completion.SetResult();
        }
    }

    public string RemotePath { get; }
    public string FilePath { get; }
    public string PartPath => FilePath + ".part";

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

    public void StartDownload(Action onFinished)
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
        }

        Task.Run(() =>
        {
            try
            {
                var client = RemoteFiles.Client(RemotePath, out var path);
                Length = client.GetSize(path);
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
                File.Move(PartPath, FilePath, overwrite: true);
                Length = new FileInfo(FilePath).Length;
                Interlocked.Exchange(ref _downloaded, Length);
                _completion.TrySetResult();
            }
            catch (Exception ex)
            {
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
    /// never started playing.)</summary>
    public void Refresh()
    {
        if (!File.Exists(FilePath)) return;
        lock (_gate)
        {
            Length = new FileInfo(FilePath).Length;
            if (IsComplete) Interlocked.Exchange(ref _downloaded, Length);
            Monitor.PulseAll(_gate);
        }
    }
}
