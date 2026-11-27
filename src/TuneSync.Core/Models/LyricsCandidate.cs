namespace TuneSync.Core.Models;

/// <summary>One set of lyrics found for a song, and where it came from.</summary>
public sealed class LyricsCandidate
{
    /// <summary>The source's name as the user sees it ("LRCLIB", "폴더 파일").</summary>
    public required string Source { get; init; }
    public required string Title { get; init; }
    public string Artist { get; init; } = "";
    public string? Album { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>The lyrics as they'd go into the file: synced (LRC) when there are, else plain.</summary>
    public required string Lyrics { get; init; }

    public bool IsSynced { get; init; }

    /// <summary>The script the lyrics are in, in words: "한국어", "일본어", "중국어", "영문".</summary>
    public string Language { get; init; } = "";

    /// <summary>The song's length matches (within 3 s); null when either length is unknown.</summary>
    public bool? LengthMatches { get; init; }

    /// <summary>The album matches the song's album tag.</summary>
    public bool AlbumMatches { get; init; }

    /// <summary>For a file found next to the song: its name.</summary>
    public string? FileName { get; init; }
}

/// <summary>Lyrics found for a song, best first. <see cref="Certain"/> is false when several fit
/// equally and it's not clear which is the song's (the first shouldn't go in by itself then).</summary>
public sealed record LyricsSearchResult(IReadOnlyList<LyricsCandidate> Candidates, bool Certain = true)
{
    public static LyricsSearchResult Empty { get; } = new([]);

    /// <summary>The lyrics to put in by themselves from <paramref name="found"/> (the files beside
    /// the song and the online ones, best first): synced lyrics kept beside the song (the user's
    /// own), else the best online ones - unless it's unclear which song they're of
    /// (<paramref name="onlineCertain"/> false) - else plain lyrics beside it. Null: none is
    /// clearly the song's, the user chooses.</summary>
    public static LyricsCandidate? Pick(IReadOnlyList<LyricsCandidate> found, bool onlineCertain) =>
        found.FirstOrDefault(c => c.FileName is not null && c.IsSynced)
        ?? (onlineCertain ? found.FirstOrDefault(c => c.FileName is null) : null)
        ?? found.FirstOrDefault(c => c.FileName is not null);
}
