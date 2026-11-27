using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

public interface ILyricsService
{
    /// <summary>The lyrics that best fit the song (title, artist, and - when given - album and
    /// length), or null when nothing matches well enough.</summary>
    Task<LyricsResult?> FindAsync(LyricsQuery query, CancellationToken ct = default);

    /// <summary>Every set of lyrics that fits the song, best first (uploads of the same song
    /// differ: synced or plain, another language, another timing), for the user to choose from.
    /// A service without a list of its own offers its one best answer.</summary>
    async Task<LyricsSearchResult> SearchAsync(LyricsQuery query, CancellationToken ct = default) =>
        await FindAsync(query, ct) is { BestLyrics: { Length: > 0 } lyrics } found
            ? new LyricsSearchResult([new LyricsCandidate
            {
                Source = "LRCLIB",
                Title = found.TrackName,
                Artist = found.ArtistName,
                Album = found.AlbumName,
                Lyrics = lyrics,
                IsSynced = LrcParser.IsSynced(lyrics),
            }])
            : LyricsSearchResult.Empty;
}
