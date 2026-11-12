using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Enumerates a folder for supported audio files and reads each one's tags/cover/lyrics
/// presence (plus a small pre-resized thumbnail, so the UI thread never has to decode a full-size
/// embedded cover). Files are read with bounded parallelism and streamed back as each one
/// finishes, so the UI can populate incrementally instead of blocking on the whole folder.</summary>
public static class MediaLibraryScanner
{
    public static async IAsyncEnumerable<LibraryFile> ScanAsync(
        string rootFolder,
        bool includeSubfolders,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Walking a big folder tree (especially on a network share) takes seconds; it must not run
        // synchronously on the caller's thread before the first await, or it freezes the UI.
        var paths = await Task.Run(() => EnumerateAudioFiles(rootFolder, includeSubfolders), ct);
        await foreach (var item in ScanFilesAsync(paths, ct))
            yield return item;
    }

    /// <summary>Reads the given audio files with bounded parallelism, streaming each back as it
    /// finishes. Missing/unsupported/unreadable files are skipped.</summary>
    public static async IAsyncEnumerable<LibraryFile> ScanFilesAsync(
        IReadOnlyCollection<string> paths,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (paths.Count == 0) yield break;

        var channel = Channel.CreateUnbounded<LibraryFile>();
        var degreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    paths,
                    new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism, CancellationToken = ct },
                    async (path, token) =>
                    {
                        // Missing/corrupt/unreadable file - skip it rather than aborting the scan.
                        var item = ScanFile(path);

                        if (item is not null)
                            await channel.Writer.WriteAsync(item, token);
                    });
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
            yield return item;

        await producer; // surfaces cancellation/any unexpected exception to the caller
    }

    /// <summary>Supported audio files under the folder. An unreadable sub-folder (permissions,
    /// a share dropping out) is skipped instead of aborting the whole scan.</summary>
    private static List<string> EnumerateAudioFiles(string rootFolder, bool includeSubfolders)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        try
        {
            return [.. Directory.EnumerateFiles(rootFolder, "*", options).Where(TagService.IsSupported)];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Reads a single audio file registered directly (not discovered by scanning a
    /// folder). Returns null if the file is missing, unsupported, or unreadable.</summary>
    public static LibraryFile? ScanFile(string path)
    {
        if (!TagService.IsSupported(path)) return null;
        if (!RemotePath.IsRemote(path) && !File.Exists(path)) return null;
        try
        {
            return ReadLibraryFile(path);
        }
        catch
        {
            return null;
        }
    }

    private static LibraryFile ReadLibraryFile(string path)
    {
        // One open for everything (a network file is then read once, not four times).
        var (tags, cover, lyrics, duration) = TagService.ReadForLibrary(path);

        return new LibraryFile
        {
            Path = path,
            Tags = tags,
            HasCover = cover is { Length: > 0 },
            HasLyrics = !string.IsNullOrWhiteSpace(lyrics),
            IsLyricsSynced = LrcParser.IsSynced(lyrics),
            // Only the small thumbnail is kept: holding every full-size cover in memory cost
            // hundreds of MB on a big library (the player reads the full cover when it plays).
            ThumbnailBytes = CoverArtService.GetThumbnail(cover)?.JpegBytes,
            Duration = duration,
        };
    }

    // --- network sources (WebDAV / FTP / SFTP) ---

    /// <summary>Walks a remote folder (and its subfolders) and reads each song's tags over the
    /// network - only the few hundred KB that hold them. Songs already in <paramref name="index"/>
    /// with the same size and date are taken from it without touching the server again.</summary>
    public static async IAsyncEnumerable<LibraryFile> ScanRemoteAsync(
        Remote.IRemoteClient client,
        string sourceId,
        string folder,
        bool includeSubfolders,
        RemoteLibraryIndex index,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = new List<RemoteEntry>();
        var pending = new Queue<string>([folder]);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            IReadOnlyList<RemoteEntry> entries;
            try
            {
                entries = await client.ListAsync(current, ct);
            }
            catch (Exception) when (current != folder)
            {
                continue; // an unreadable subfolder shouldn't sink the whole scan
            }
            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (includeSubfolders && !entry.Name.StartsWith('.') && !entry.Name.StartsWith('@')) pending.Enqueue(entry.Path);
                }
                else if (TagService.IsSupported(entry.Name))
                {
                    files.Add(entry);
                }
            }
        }

        var toRead = new List<string>();
        foreach (var entry in files)
        {
            var path = RemotePath.Build(sourceId, entry.Path);
            Remote.RemoteFiles.RememberSize(path, entry.Size);
            if (index.TryGet(path, entry.Size, entry.Modified) is { } known) yield return known;
            else toRead.Add(path);
        }

        var stamps = files.ToDictionary(f => RemotePath.Build(sourceId, f.Path), f => (f.Size, f.Modified));
        await foreach (var item in ScanFilesAsync(toRead, ct))
        {
            if (stamps.TryGetValue(item.Path, out var stamp)) index.Put(item, stamp.Size, stamp.Modified);
            yield return item;
        }
    }

    /// <summary>A single song registered directly from a remote source.</summary>
    public static LibraryFile? ScanRemoteFile(string path) => ScanFile(path);
}

/// <summary>What was read from each network song last time (tags, thumbnail, length), saved to
/// disk and reused while the file's size and date are unchanged - so a remote library opens in
/// seconds instead of re-reading every file over the network at each start.</summary>
public sealed class RemoteLibraryIndex
{
    private readonly string _file;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries;
    private bool _dirty;

    public sealed class Entry
    {
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public TrackTags? Tags { get; set; }
        public bool HasCover { get; set; }
        public bool HasLyrics { get; set; }
        public bool IsLyricsSynced { get; set; }
        public byte[]? Thumbnail { get; set; }
        public double DurationSeconds { get; set; }
    }

    public RemoteLibraryIndex(string file)
    {
        _file = file;
        try
        {
            var loaded = File.Exists(file)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file))
                : null;
            _entries = new(loaded ?? [], StringComparer.Ordinal);
        }
        catch
        {
            _entries = new(StringComparer.Ordinal); // unreadable index: just rebuild it
        }
    }

    public LibraryFile? TryGet(string path, long size, DateTime modified)
    {
        if (!_entries.TryGetValue(path, out var e) || e.Size != size || e.Modified != modified || e.Tags is not { } t) return null;
        return new LibraryFile
        {
            Path = path,
            Tags = new TrackTags
            {
                Path = path,
                Title = t.Title,
                Artist = t.Artist,
                AlbumArtist = t.AlbumArtist,
                Album = t.Album,
                Composer = t.Composer,
                Genre = t.Genre,
                Track = t.Track,
                Disc = t.Disc,
                Year = t.Year,
            },
            HasCover = e.HasCover,
            HasLyrics = e.HasLyrics,
            IsLyricsSynced = e.IsLyricsSynced,
            ThumbnailBytes = e.Thumbnail,
            Duration = TimeSpan.FromSeconds(e.DurationSeconds),
        };
    }

    public void Put(LibraryFile file, long size, DateTime modified)
    {
        _entries[file.Path] = new Entry
        {
            Size = size,
            Modified = modified,
            Tags = file.Tags,
            HasCover = file.HasCover,
            HasLyrics = file.HasLyrics,
            IsLyricsSynced = file.IsLyricsSynced,
            Thumbnail = file.ThumbnailBytes,
            DurationSeconds = file.Duration.TotalSeconds,
        };
        _dirty = true;
    }

    /// <summary>After an edit was uploaded the stored entry is stale: drop it so it's re-read.</summary>
    public void Forget(string path)
    {
        if (_entries.TryRemove(path, out _)) _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, System.Text.Json.JsonSerializer.Serialize(_entries));
            _dirty = false;
        }
        catch
        {
            // a missed save only costs a slower next start
        }
    }
}
