using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

public interface ILyricsService
{
    Task<LyricsResult?> FindAsync(string artist, string title, CancellationToken ct = default);
}
