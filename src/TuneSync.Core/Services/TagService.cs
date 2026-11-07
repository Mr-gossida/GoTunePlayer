using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Reads and writes the text tags every other TuneSync feature (rename, merge-dedupe,
/// lyrics) keys off. Backed by TagLib#, so it understands mp3/flac/m4a transparently.</summary>
public static class TagService
{
    public static readonly string[] SupportedExtensions = [".mp3", ".flac", ".m4a"];

    public static bool IsSupported(string path) =>
        Array.IndexOf(SupportedExtensions, System.IO.Path.GetExtension(path).ToLowerInvariant()) >= 0;

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
        };
    }

    public static void Write(TrackTags tags)
    {
        using var file = TagLib.File.Create(tags.Path);
        var t = file.Tag;
        t.Title = tags.Title;
        t.Performers = [tags.Artist];
        t.AlbumArtists = [tags.AlbumArtist];
        t.Album = tags.Album;
        t.Composers = string.IsNullOrEmpty(tags.Composer) ? [] : [tags.Composer];
        t.Genres = string.IsNullOrEmpty(tags.Genre) ? [] : [tags.Genre];
        t.Track = tags.Track;
        t.Disc = tags.Disc;
        file.Save();
    }

    public static string? ReadLyrics(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Lyrics;
    }

    /// <summary>TagLib's Lyrics property maps to the right native field per format on its own:
    /// ID3 USLT for mp3, the LYRICS Vorbis comment for flac, the ©lyr atom for m4a.</summary>
    public static void WriteLyrics(string path, string lyrics)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Lyrics = lyrics;
        file.Save();
    }
}
