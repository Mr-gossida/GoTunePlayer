namespace TuneSync.Core.Models;

/// <summary>The text tags every TuneSync feature (library view, auto-fill, rename, merge) keys
/// off. A plain data holder — <see cref="Services.TagService"/> is what maps it on/off disk.</summary>
public sealed class TrackTags
{
    public required string Path { get; init; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Composer { get; set; } = "";
    public string Genre { get; set; } = "";
    public uint Track { get; set; }
    public uint Disc { get; set; }
    public uint Year { get; set; }

    public TrackTags Clone() => new()
    {
        Path = Path,
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        Composer = Composer,
        Genre = Genre,
        Track = Track,
        Disc = Disc,
        Year = Year,
    };
}
