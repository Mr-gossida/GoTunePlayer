using System.Text.RegularExpressions;
using TuneSync.Core.Services.CoverSearch;

namespace TuneSync.Core.Services.TagSearch;

/// <summary>Scores tag candidates against the song and orders them, best first. Pure functions
/// (no network), so the ranking can be tested on its own. The name comparisons are the cover
/// search's (<see cref="CoverRanking"/>): the same song is recognized the same way in both.</summary>
public static partial class TagRanking
{
    // Title and artist decide whether it's the song at all; album and length which recording;
    // how much of the tags a candidate fills breaks ties between equally fitting ones.
    private const double TitleWeight = 0.45;
    private const double ArtistWeight = 0.30;
    private const double AlbumWeight = 0.10;
    private const double DurationWeight = 0.10;
    private const double FilledWeight = 0.05;

    /// <summary>Below this a candidate is most likely another song; it isn't shown.</summary>
    public const double MinScore = 0.55;

    /// <summary>Scores every candidate, drops the ones that are clearly another song, merges what a
    /// source lists twice, and returns at most <paramref name="max"/>, best first.</summary>
    public static List<TagCandidate> Rank(TagQuery query, IEnumerable<TagCandidate> candidates, int max = 12)
    {
        var kept = new List<TagCandidate>();
        foreach (var c in candidates)
        {
            var title = CoverRanking.TitleSimilarity(query.Title, c.Title);
            if (title < 0.5) continue;
            // A similar title by an unrelated artist is another song; only an exact title may have
            // a differently written artist (아이유 / IU, 패닉 / Panic).
            if (title < 0.95 && CoverRanking.ArtistSimilarity(query.Artist, c.Artist) < 0.4) continue;
            c.Score = Score(query, c);
            if (c.Score >= MinScore) kept.Add(c);
        }
        return kept
            .OrderByDescending(c => c.Score)
            .ThenByDescending(Filled)
            .DistinctBy(c => (c.Source, CoverRanking.Normalize(c.Title), CoverRanking.Normalize(c.Artist), CoverRanking.Normalize(c.Album), c.Year, c.Track))
            .Take(max)
            .ToList();
    }

    public static double Score(TagQuery query, TagCandidate c)
    {
        var title = CoverRanking.TitleSimilarity(query.Title, c.Title);
        var artist = CoverRanking.ArtistSimilarity(query.Artist, c.Artist);

        // Without an album (or a length) to compare, its weight goes to title and artist.
        double album = 0, albumWeight = 0;
        if (!string.IsNullOrWhiteSpace(query.Album) && !string.IsNullOrWhiteSpace(c.Album))
        {
            album = CoverRanking.TitleSimilarity(query.Album, c.Album);
            albumWeight = AlbumWeight;
        }
        double duration = 0, durationWeight = 0;
        if (query.Duration is { TotalSeconds: > 0 } && c.Duration is { TotalSeconds: > 0 })
        {
            duration = DurationScore(query.Duration.Value, c.Duration.Value);
            durationWeight = DurationWeight;
        }
        var scale = (TitleWeight + ArtistWeight + AlbumWeight + DurationWeight) / (TitleWeight + ArtistWeight + albumWeight + durationWeight);
        var score = (title * TitleWeight + artist * ArtistWeight + album * albumWeight + duration * durationWeight) * scale
                    + Filled(c) / 6.0 * FilledWeight;

        // A live / remix / acoustic version the song isn't.
        if (VersionWords().IsMatch(c.Title + " " + c.Album) && !VersionWords().IsMatch(query.Title + " " + query.Album)) score *= 0.85;
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>1 within 3 s; less the further apart (another recording, past 10 s).</summary>
    public static double DurationScore(TimeSpan a, TimeSpan b)
    {
        var diff = Math.Abs((a - b).TotalSeconds);
        return diff <= 3 ? 1 : diff <= 10 ? 0.6 : diff <= 30 ? 0.2 : 0;
    }

    /// <summary>How many of the six detail tags a candidate has (album, album artist, year, genre, track, disc).</summary>
    private static int Filled(TagCandidate c) =>
        (c.Album.Length > 0 ? 1 : 0) + (c.AlbumArtist.Length > 0 ? 1 : 0) + (c.Year is not null ? 1 : 0)
        + (c.Genre.Length > 0 ? 1 : 0) + (c.Track is not null ? 1 : 0) + (c.Disc is not null ? 1 : 0);

    [GeneratedRegex(@"\b(live|acoustic|instrumental|inst\.?|karaoke|unplugged|demo|remix|sped up|slowed)\b|라이브|어쿠스틱|ライブ", RegexOptions.IgnoreCase)]
    private static partial Regex VersionWords();
}
