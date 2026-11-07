namespace TuneSync.Core.Models;

public sealed class LyricsResult
{
    public string TrackName { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string? AlbumName { get; set; }
    public bool Instrumental { get; set; }
    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }

    /// <summary>What actually goes into the tag: synced text if we have it, else plain, else null.</summary>
    public string? BestLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics) ? SyncedLyrics
        : !string.IsNullOrWhiteSpace(PlainLyrics) ? PlainLyrics
        : null;
}
