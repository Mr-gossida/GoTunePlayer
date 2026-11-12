using TuneSync.Core.Models;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Services;

/// <summary>Reads and writes the text tags and lyrics every other TuneSync feature keys off.
/// Backed by TagLib#, so it understands mp3/flac/m4a (and, incidentally, ogg/wma/wav) transparently.
/// Paths may be local or remote (<see cref="RemotePath"/>): files are opened through
/// <see cref="RemoteFiles"/>, which reads network files in place and writes edits back to them.</summary>
public static class TagService
{
    public static readonly string[] SupportedExtensions = [".mp3", ".flac", ".m4a", ".ogg", ".wma", ".wav"];

    public static bool IsSupported(string path) =>
        Array.IndexOf(SupportedExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    public static TrackTags Read(string path)
    {
        using var file = RemoteFiles.Open(path);
        return ReadTags(file, path);
    }

    private static TrackTags ReadTags(TagLib.File file, string path)
    {
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

    /// <summary>Everything the library list needs from a file, read in one open (which matters a
    /// lot for network files: one connection and a few hundred KB instead of four passes).</summary>
    public static (TrackTags Tags, byte[]? Cover, string? Lyrics, TimeSpan Duration) ReadForLibrary(string path)
    {
        using var file = RemoteFiles.Open(path);
        var cover = file.Tag.Pictures.FirstOrDefault()?.Data?.Data;
        var lyrics = file.Tag.Lyrics;
        return (ReadTags(file, path), cover, string.IsNullOrEmpty(lyrics) ? null : lyrics, file.Properties?.Duration ?? TimeSpan.Zero);
    }

    public static void Write(TrackTags tags) => RemoteFiles.Edit(tags.Path, file => ApplyTo(file, tags));

    /// <summary>Copies the text tags onto an open file (saved by the caller), so several edits
    /// can share one save - for a network file that's one upload instead of several.</summary>
    public static void ApplyTo(TagLib.File file, TrackTags tags)
    {
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
    }

    public static TimeSpan ReadDuration(string path)
    {
        using var file = RemoteFiles.Open(path);
        return file.Properties?.Duration ?? TimeSpan.Zero;
    }

    public static AudioQuality? ReadAudioQuality(string path)
    {
        using var file = RemoteFiles.Open(path);
        if (file.Properties is not { } p) return null;

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var codec = string.Join(" ", p.Codecs.Where(c => c is not null).Select(c => c.Description ?? ""));
        var isAlac = codec.Contains("ALAC", StringComparison.OrdinalIgnoreCase)
                     || codec.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase);
        var format = ext switch
        {
            "m4a" or "mp4" => isAlac ? "ALAC" : "AAC",
            "ogg" => "OGG",
            "wma" => codec.Contains("Lossless", StringComparison.OrdinalIgnoreCase) ? "WMA Lossless" : "WMA",
            _ => ext.ToUpperInvariant(),
        };
        var lossless = ext is "flac" or "wav" or "aiff" or "aif" || isAlac || format == "WMA Lossless";
        return new AudioQuality(format, p.AudioBitrate, p.AudioSampleRate, p.BitsPerSample, p.AudioChannels, lossless);
    }

    public static string? ReadLyrics(string path)
    {
        using var file = RemoteFiles.Open(path);
        var lyrics = file.Tag.Lyrics;
        return string.IsNullOrEmpty(lyrics) ? null : lyrics;
    }

    /// <summary>TagLib's Lyrics property maps to the right native field per format on its own:
    /// ID3 USLT for mp3, the LYRICS Vorbis comment for flac, the (c)lyr atom for m4a.</summary>
    public static void WriteLyrics(string path, string lyrics) =>
        RemoteFiles.Edit(path, file => file.Tag.Lyrics = lyrics);
}
