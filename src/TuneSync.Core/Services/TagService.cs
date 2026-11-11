using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Reads and writes the text tags and lyrics every other TuneSync feature keys off.
/// Backed by TagLib#, so it understands mp3/flac/m4a (and, incidentally, ogg/wma/wav) transparently.</summary>
public static class TagService
{
    public static readonly string[] SupportedExtensions = [".mp3", ".flac", ".m4a", ".ogg", ".wma", ".wav"];

    public static bool IsSupported(string path) =>
        Array.IndexOf(SupportedExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    public static TrackTags Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var t = file.Tag;
        return new TrackTags
        {
            Path = path,
            Title = t.Title ?? "",
            Artist = t.FirstPerformer ?? "",
            AlbumArtist = t.FirstAlbumArtist ?? "",
            Album = t.Album ?? "",
            Composer = t.FirstComposer ?? "",
            Genre = t.FirstGenre ?? "",
            Track = t.Track,
            Disc = t.Disc,
            Year = t.Year,
        };
    }

    public static void Write(TrackTags tags)
    {
        using var file = TagLib.File.Create(tags.Path);
        var t = file.Tag;
        t.Title = tags.Title;
        t.Performers = string.IsNullOrEmpty(tags.Artist) ? [] : [tags.Artist];
        t.AlbumArtists = string.IsNullOrEmpty(tags.AlbumArtist) ? [] : [tags.AlbumArtist];
        t.Album = tags.Album;
        t.Composers = string.IsNullOrEmpty(tags.Composer) ? [] : [tags.Composer];
        t.Genres = string.IsNullOrEmpty(tags.Genre) ? [] : [tags.Genre];
        t.Track = tags.Track;
        t.Disc = tags.Disc;
        t.Year = tags.Year;
        file.Save();
    }

    public static TimeSpan ReadDuration(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Properties?.Duration ?? TimeSpan.Zero;
    }

    public static string? ReadLyrics(string path)
    {
        using var file = TagLib.File.Create(path);
        var lyrics = file.Tag.Lyrics;
        return string.IsNullOrEmpty(lyrics) ? null : lyrics;
    }

    /// <summary>TagLib's Lyrics property maps to the right native field per format on its own:
    /// ID3 USLT for mp3, the LYRICS Vorbis comment for flac, the (c)lyr atom for m4a.</summary>
    public static void WriteLyrics(string path, string lyrics)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Lyrics = lyrics;
        file.Save();
    }
}
