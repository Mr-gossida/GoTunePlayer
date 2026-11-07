namespace TuneSync.Core.Models;

/// <summary>A single candidate match from a MusicBrainz recording search, carrying the
/// release MBID needed to look up cover art on the Cover Art Archive.</summary>
public sealed class MusicBrainzMatch
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public string? ReleaseMbid { get; init; }
    public string? AlbumArtist { get; init; }
    public uint? Track { get; init; }
    public uint? Disc { get; init; }

    /// <summary>MusicBrainz's own 0-100 match confidence for this recording search hit.</summary>
    public int Score { get; init; }
}
