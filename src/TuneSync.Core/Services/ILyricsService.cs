using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

public interface ILyricsService
{
    /// <summary>The lyrics that best fit the song (title, artist, and - when given - album and
    /// length), or null when nothing matches well enough.</summary>
    Task<LyricsResult?> FindAsync(LyricsQuery query, CancellationToken ct = default);
}
