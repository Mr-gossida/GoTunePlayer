namespace TuneSync.Core.Models;

/// <summary>A single MusicBrainz recording/release match, scored so the caller can decide how
/// much to trust it.</summary>
public sealed class MusicBrainzMatch
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? ReleaseMbid { get; init; }
    public uint? Track { get; init; }
    public uint? Disc { get; init; }
    public int Score { get; init; }
}
