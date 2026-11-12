using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

/// <summary>In-memory stand-ins for the network services AutoFillService depends on, so its tests
/// never hit the real MusicBrainz/LRCLIB APIs.</summary>
internal sealed class FakeMusicBrainzService : IMusicBrainzService
{
    public MusicBrainzMatch? NextMatch { get; set; }
    public byte[]? NextCover { get; set; }
    public List<(string Artist, string Title)> Calls { get; } = [];

    public Task<MusicBrainzMatch?> FindAsync(string artist, string title, CancellationToken ct = default)
    {
        Calls.Add((artist, title));
        return Task.FromResult(NextMatch);
    }

    public Task<byte[]?> GetCoverAsync(string releaseMbid, CancellationToken ct = default) =>
        Task.FromResult(NextCover);
}

internal sealed class FakeLyricsService : ILyricsService
{
    public LyricsResult? NextResult { get; set; }

    public LyricsQuery? LastQuery { get; private set; }

    public Task<LyricsResult?> FindAsync(LyricsQuery query, CancellationToken ct = default)
    {
        LastQuery = query;
        return Task.FromResult(NextResult);
    }
}
