using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

public interface IMusicBrainzService
{
    Task<MusicBrainzMatch?> FindAsync(string artist, string title, CancellationToken ct = default);
    Task<byte[]?> GetCoverAsync(string releaseMbid, CancellationToken ct = default);
}
