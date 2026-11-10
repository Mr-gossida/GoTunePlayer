using System.Runtime.CompilerServices;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Enumerates a folder for supported audio files and reads each one's tags/cover/lyrics
/// presence. Yields results as they're read so the UI can populate incrementally instead of
/// blocking on the whole folder.</summary>
public static class MediaLibraryScanner
{
    public static async IAsyncEnumerable<LibraryFile> ScanAsync(
        string rootFolder,
        bool includeSubfolders,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(rootFolder, "*.*", option).Where(TagService.IsSupported);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            LibraryFile? item;
            try
            {
                item = await Task.Run(() => ReadLibraryFile(path), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested == false)
            {
                // Corrupt/unreadable file - skip it rather than aborting the whole scan.
                item = null;
            }

            if (item is not null) yield return item;
        }
    }

    private static LibraryFile ReadLibraryFile(string path)
    {
        var tags = TagService.Read(path);
        var cover = CoverArtService.GetCover(path);
        var lyrics = TagService.ReadLyrics(path);

        return new LibraryFile
        {
            Path = path,
            Tags = tags,
            HasCover = cover is { Length: > 0 },
            HasLyrics = !string.IsNullOrWhiteSpace(lyrics),
            CoverBytes = cover,
        };
    }
}
