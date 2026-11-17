namespace TuneSync.Core.Models;

/// <summary>What a cover search looks for: the song as the library knows it. Album and duration
/// are optional; when present they help pick the right release among many.</summary>
public sealed record CoverQuery(string Artist, string Title, string? Album = null, TimeSpan? Duration = null);

/// <summary>What kind of release a cover belongs to. Live recordings, compilations and bootlegs
/// usually carry a different picture than the song's own album, so they rank lower.</summary>
public enum CoverReleaseKind
{
    Unknown,
    Album,
    EP,
    Single,
    Compilation,
    Live,
    Unofficial,
}

/// <summary>One cover found online, with where it came from and how well it matches the song.</summary>
public sealed class CoverCandidate
{
    /// <summary>"MusicBrainz", "iTunes" or "Deezer" - shown on the tile.</summary>
    public required string Source { get; init; }

    /// <summary>The song title and artist as the source lists them.</summary>
    public required string Title { get; init; }

    public required string Artist { get; init; }

    /// <summary>The album/release the cover belongs to.</summary>
    public string Album { get; init; } = "";

    public int? Year { get; init; }

    public CoverReleaseKind Kind { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>A small (about 250-300 px) image for the gallery.</summary>
    public required string ThumbnailUrl { get; init; }

    /// <summary>Full-size image addresses, largest first; the first one that answers is used.</summary>
    public required IReadOnlyList<string> FullUrls { get; init; }

    /// <summary>The largest edge the source can serve (e.g. 1200 for Cover Art Archive), before
    /// the image is actually downloaded; 0 when unknown.</summary>
    public int MaxSize { get; init; }

    /// <summary>Identifies the same picture across one source's results (e.g. a release group).</summary>
    public string? Key { get; init; }

    /// <summary>0..1, set by <see cref="Services.CoverSearch.CoverRanking"/>.</summary>
    public double Score { get; set; }

    public int ScorePercent => (int)Math.Round(Score * 100);
}
