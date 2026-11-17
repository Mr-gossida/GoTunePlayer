using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services.CoverSearch;

/// <summary>Scores cover candidates against the song and orders them, best first. Pure functions
/// (no network), so the ranking can be tested on its own.</summary>
public static partial class CoverRanking
{
    // How much each signal counts. Title and artist decide whether it's the right song at all;
    // the rest picks the nicest cover among the right ones.
    private const double TitleWeight = 0.42;
    private const double ArtistWeight = 0.30;
    private const double AlbumWeight = 0.10;
    private const double DurationWeight = 0.05;
    private const double KindWeight = 0.07;
    private const double SizeWeight = 0.06;

    /// <summary>Below this a candidate is most likely a different song; it isn't shown.</summary>
    public const double MinScore = 0.55;

    /// <summary>Scores every candidate, drops the ones that are clearly another song, merges
    /// duplicates (the same album from several sources keeps its best entry) and returns at most
    /// <paramref name="max"/>, best first.</summary>
    public static List<CoverCandidate> Rank(CoverQuery query, IEnumerable<CoverCandidate> candidates, int max = 12)
    {
        var kept = new List<CoverCandidate>();
        foreach (var c in candidates)
        {
            var title = TitleSimilarity(query.Title, c.Title);
            if (title < 0.5) continue;
            // A similar title by an unrelated artist ("Love Like This" for "Love Me Like This") is
            // another song. Only an exact title may have a differently written artist: 패닉 / Panic,
            // or a soundtrack credited to "Various Artists" or the label.
            if (title < 0.95 && ArtistSimilarity(query.Artist, c.Artist) < 0.4) continue;
            c.Score = Score(query, c);
            if (c.Score >= MinScore) kept.Add(c);
        }

        return kept
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.MaxSize)
            .DistinctBy(c => c.Key ?? c.ThumbnailUrl)
            // The same album name is the same picture, even when one store spells the artist
            // differently (the Japanese store lists Oasis as オアシス).
            .DistinctBy(c => Normalize(c.Album))
            .Take(max)
            .ToList();
    }

    public static double Score(CoverQuery query, CoverCandidate c)
    {
        var title = TitleSimilarity(query.Title, c.Title);
        var artist = ArtistSimilarity(query.Artist, c.Artist);

        // A file without an album tag gives no album signal; its weight goes to title and artist.
        double album = 0, albumWeight = 0;
        if (!string.IsNullOrWhiteSpace(query.Album) && !string.IsNullOrWhiteSpace(c.Album))
        {
            album = TitleSimilarity(query.Album, c.Album);
            albumWeight = AlbumWeight;
        }

        var duration = DurationScore(query.Duration, c.Duration);
        var kind = KindScore(query, c);
        var size = SizeScore(c.MaxSize);

        var matchWeight = TitleWeight + ArtistWeight + AlbumWeight;
        var scale = matchWeight / (TitleWeight + ArtistWeight + albumWeight);
        var score = (title * TitleWeight + artist * ArtistWeight + album * albumWeight) * scale
                    + duration * DurationWeight + kind * KindWeight + size * SizeWeight;
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>1 when the names are the same apart from case, width, punctuation and
    /// "(Remastered)"-style additions; lower the more they differ.</summary>
    public static double TitleSimilarity(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var full = Similarity(Normalize(a), Normalize(b));
        if (full >= 1) return 1;
        // "Wonderwall (Remastered)" vs "Wonderwall": compare without the bracketed extras too,
        // a little below a full match so the exact edition still wins.
        var coreA = Normalize(StripExtras(a));
        var coreB = Normalize(StripExtras(b));
        if (coreA.Length == 0 || coreB.Length == 0) return full;
        var core = Similarity(coreA, coreB);
        // The shared "(Remastered)" mustn't make "Live Forever (Remastered)" look like
        // "Wonderwall (Remastered)": the full names count only as far as the core names agree.
        return Math.Max(core * 0.97, Math.Min(full, core));
    }

    /// <summary>Like <see cref="TitleSimilarity"/>, but "Paul Blanco, Heize" also matches
    /// "Paul Blanco & Heize" or just "Paul Blanco".</summary>
    public static double ArtistSimilarity(string? a, string? b)
    {
        var whole = TitleSimilarity(a, b);
        if (whole >= 1 || string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return whole;
        var pa = SplitArtists(a);
        var pb = SplitArtists(b);
        if (pa.Count == 0 || pb.Count == 0) return whole;
        var shared = pa.Count(x => pb.Contains(x));
        if (shared == 0) return whole;
        // Every name on the shorter list appears on the longer one: almost a full match.
        var part = shared == Math.Min(pa.Count, pb.Count) ? 0.92 : 0.8;
        return Math.Max(whole, part);
    }

    private static double DurationScore(TimeSpan? a, TimeSpan? b)
    {
        if (a is not { TotalSeconds: > 0 } x || b is not { TotalSeconds: > 0 } y) return 0.5;
        var diff = Math.Abs((x - y).TotalSeconds);
        return diff <= 3 ? 1 : diff <= 10 ? 0.6 : diff <= 30 ? 0.2 : 0;
    }

    private static double KindScore(CoverQuery query, CoverCandidate c)
    {
        // A live/acoustic/instrumental version has its own cover; only fine when the song is one.
        var wantsVersion = VersionWords().IsMatch(query.Title) || VersionWords().IsMatch(query.Album ?? "");
        if (!wantsVersion && (VersionWords().IsMatch(c.Title) || VersionWords().IsMatch(c.Album)))
            return 0;
        return c.Kind switch
        {
            CoverReleaseKind.Album => 1,
            CoverReleaseKind.EP => 0.9,
            CoverReleaseKind.Single => 0.8,
            CoverReleaseKind.Unknown => 0.7,
            CoverReleaseKind.Compilation => 0.3,
            CoverReleaseKind.Live => wantsVersion ? 0.8 : 0.1,
            _ => 0,
        };
    }

    private static double SizeScore(int maxSize) => maxSize switch
    {
        >= 1000 => 1,
        >= 600 => 0.75,
        >= 500 => 0.6,
        0 => 0.5,
        _ => 0.2,
    };

    /// <summary>Lowercase, full-width folded (NFKC), accents kept, punctuation and spaces removed -
    /// so "Love Me Like This!" and "love me like this" compare equal.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var text = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch) || cat is UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Drops "(feat. …)", "[Remastered 2009]", " - Single" and similar additions.</summary>
    public static string StripExtras(string s)
    {
        var t = Brackets().Replace(s, " ");
        t = DashSuffix().Replace(t, "");
        t = Feat().Replace(t, "");
        return t.Trim();
    }

    private static HashSet<string> SplitArtists(string s) =>
        ArtistSeparators().Split(s).Select(Normalize).Where(x => x.Length > 0).ToHashSet();

    /// <summary>0..1: 1 when equal; one name containing the other counts high (it's usually an
    /// addition like a subtitle); otherwise the Dice coefficient of character pairs.</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length >= 3 && longer.Contains(shorter, StringComparison.Ordinal))
            return 0.8 + 0.15 * shorter.Length / longer.Length;
        if (a.Length < 2 || b.Length < 2) return 0;

        var pairs = new Dictionary<int, int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var key = a[i] << 16 | a[i + 1];
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }
        var common = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var key = b[i] << 16 | b[i + 1];
            if (pairs.TryGetValue(key, out var n) && n > 0)
            {
                pairs[key] = n - 1;
                common++;
            }
        }
        return 2.0 * common / (a.Length - 1 + b.Length - 1);
    }

    [GeneratedRegex(@"\s*[\(\[\{（【「『<][^\)\]\}）】」』>]*[\)\]\}）】」』>]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+-\s+(single|ep|remaster(ed)?.*|.*version|.*edition|from .*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DashSuffix();

    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Feat();

    [GeneratedRegex(@"\s*(?:,|&|/|;|\+|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bwith\b|、)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparators();

    [GeneratedRegex(@"\b(live|acoustic|instrumental|inst\.?|karaoke|unplugged|demo|remix)\b|라이브|어쿠스틱|ライブ", RegexOptions.IgnoreCase)]
    private static partial Regex VersionWords();
}
