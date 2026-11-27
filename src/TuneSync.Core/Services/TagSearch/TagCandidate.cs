namespace TuneSync.Core.Services.TagSearch;

/// <summary>What a tag search looks for: the song's artist and title (from its tags, or its file
/// name), and its album and length when known - they tell versions of the song apart.</summary>
public sealed record TagQuery(string Artist, string Title, string? Album = null, TimeSpan? Duration = null);

/// <summary>One song as a source has it: the tags it would give the file. Empty / null where the
/// source doesn't say.</summary>
public sealed class TagCandidate
{
    /// <summary>The source's name as the user sees it ("MusicBrainz", "iTunes", "Deezer").</summary>
    public required string Source { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string Album { get; init; } = "";
    public string AlbumArtist { get; init; } = "";
    public uint? Year { get; init; }
    public string Genre { get; init; } = "";
    public uint? Track { get; init; }
    public uint? TrackCount { get; init; }
    public uint? Disc { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>How well it fits the song, 0..1 (<see cref="TagRanking"/>).</summary>
    public double Score { get; set; }
}

/// <summary>How far a search is: <paramref name="Done"/> of <paramref name="Total"/> sources
/// have answered, the last being <paramref name="Source"/>.</summary>
public sealed record SourceProgress(int Done, int Total, string Source);

/// <summary>Ranked candidates, best first; the sources asked, and those that couldn't be reached.</summary>
public sealed record TagSearchResult(IReadOnlyList<TagCandidate> Candidates, IReadOnlyList<string> Sources, IReadOnlyList<string> FailedSources);
