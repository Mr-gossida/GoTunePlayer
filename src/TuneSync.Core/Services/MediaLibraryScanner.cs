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
        if (!TagService.IsSupported(path) || !File.Exists(path)) return null;
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
        var tags = TagService.Read(path);
        var cover = CoverArtService.GetCover(path);
        var lyrics = TagService.ReadLyrics(path);
        var duration = TagService.ReadDuration(path);

        return new LibraryFile
        {
            Path = path,
            Tags = tags,
            HasCover = cover is { Length: > 0 },
            HasLyrics = !string.IsNullOrWhiteSpace(lyrics),
            IsLyricsSynced = LrcParser.IsSynced(lyrics),
            CoverBytes = cover,
            ThumbnailBytes = CoverArtService.GetThumbnail(cover)?.JpegBytes,
            Duration = duration,
        };
    }
}
