namespace TuneSync.Core.Models;

public sealed class LyricsResult
{
    public required string TrackName { get; init; }
    public required string ArtistName { get; init; }
    public string? AlbumName { get; init; }
    public bool Instrumental { get; init; }
    public string? PlainLyrics { get; init; }
    public string? SyncedLyrics { get; init; }

    /// <summary>Prefer synced (LRC timestamps) lyrics when available; embedded tag lyrics have no
    /// use for timing anyway, so plain text is the safe fallback.</summary>
    public string? BestLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics) ? SyncedLyrics : PlainLyrics;
}
