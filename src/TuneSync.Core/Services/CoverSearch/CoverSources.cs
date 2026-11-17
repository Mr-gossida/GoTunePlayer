using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services.CoverSearch;

/// <summary>One place covers can come from. Each source turns the song into candidates; the
/// ranking (<see cref="CoverRanking"/>) decides which are worth showing.</summary>
public interface ICoverSource
{
    string Name { get; }

    Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverQuery query, CancellationToken ct);
}

/// <summary>MusicBrainz releases with art from the Cover Art Archive. Uses the release group's
/// art (the archive picks the release that has a front cover), so one album doesn't show up once
/// per pressing. Goes through <see cref="MusicBrainzService"/> for its one-request-a-second limit.</summary>
public sealed class MusicBrainzCoverSource(MusicBrainzService musicBrainz) : ICoverSource
{
    public string Name => "MusicBrainz";

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverQuery query, CancellationToken ct)
    {
        var recordings = await musicBrainz.SearchRecordingsAsync(query.Artist, query.Title, 15, ct);
        return Parse(recordings);
    }

    public static List<CoverCandidate> Parse(JsonArray? recordings)
    {
        var list = new List<CoverCandidate>();
        var seen = new HashSet<string>();
        foreach (var rec in recordings ?? [])
        {
            if (rec is null || (rec["score"]?.GetValue<int>() ?? 0) < 50) continue;
            var title = rec["title"]?.GetValue<string>();
            var artist = FirstCredit(rec);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
            TimeSpan? length = rec["length"] is JsonValue v && v.TryGetValue<long>(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;

            foreach (var rel in rec["releases"]?.AsArray() ?? [])
            {
                var group = rel?["release-group"];
                var groupId = group?["id"]?.GetValue<string>();
                if (rel is null || string.IsNullOrEmpty(groupId) || !seen.Add(groupId)) continue;
                var releaseId = rel["id"]?.GetValue<string>();

                var full = new List<string>
                {
                    $"https://coverartarchive.org/release-group/{groupId}/front-1200",
                    $"https://coverartarchive.org/release-group/{groupId}/front-500",
                };
                if (!string.IsNullOrEmpty(releaseId)) full.Add($"https://coverartarchive.org/release/{releaseId}/front-1200");

                list.Add(new CoverCandidate
                {
                    Source = "MusicBrainz",
                    Title = title,
                    Artist = FirstCredit(rel) ?? artist,
                    Album = rel["title"]?.GetValue<string>() ?? group?["title"]?.GetValue<string>() ?? "",
                    Year = YearOf(rel["date"]?.GetValue<string>()),
                    Kind = KindOf(rel),
                    Duration = length,
                    ThumbnailUrl = $"https://coverartarchive.org/release-group/{groupId}/front-250",
                    FullUrls = full,
                    MaxSize = 1200,
                    Key = "mb:" + groupId,
                });
            }
        }
        return list;
    }

    private static CoverReleaseKind KindOf(JsonNode release)
    {
        var status = release["status"]?.GetValue<string>();
        if (status is "Bootleg" or "Pseudo-Release") return CoverReleaseKind.Unofficial;
        var group = release["release-group"];
        var secondary = group?["secondary-types"]?.AsArray().Select(t => t?.GetValue<string>()).ToList() ?? [];
        if (secondary.Contains("Live")) return CoverReleaseKind.Live;
        // (A soundtrack is fine: for OST and game music it *is* the song's album.)
        if (secondary.Contains("Compilation")) return CoverReleaseKind.Compilation;
        return group?["primary-type"]?.GetValue<string>() switch
        {
            "Album" => CoverReleaseKind.Album,
            "EP" => CoverReleaseKind.EP,
            "Single" => CoverReleaseKind.Single,
            _ => CoverReleaseKind.Unknown,
        };
    }

    private static string? FirstCredit(JsonNode node)
    {
        var credit = node["artist-credit"]?.AsArray();
        return credit?.Count > 0 ? credit[0]?["name"]?.GetValue<string>() : null;
    }

    internal static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
}

/// <summary>The iTunes Search API (no key). Searched in the US and Japanese stores: the Korean
/// store isn't served by this API, and the Japanese one covers J-pop, anime and game music well.
/// Artwork URLs take any size in the file name, up to what Apple has on file.</summary>
public sealed partial class ITunesCoverSource(HttpClient http) : ICoverSource
{
    private static readonly string[] Countries = ["US", "JP"];

    public string Name => "iTunes";

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverQuery query, CancellationToken ct)
    {
        var term = Uri.EscapeDataString($"{query.Artist} {query.Title}");
        var results = new List<CoverCandidate>();
        foreach (var country in Countries)
        {
            var json = await http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=15&country={country}", ct);
            results.AddRange(Parse(JsonNode.Parse(json)));
        }
        return results;
    }

    public static List<CoverCandidate> Parse(JsonNode? root)
    {
        var list = new List<CoverCandidate>();
        foreach (var r in root?["results"]?.AsArray() ?? [])
        {
            var art = r?["artworkUrl100"]?.GetValue<string>();
            var title = r?["trackName"]?.GetValue<string>();
            var artist = r?["artistName"]?.GetValue<string>();
            if (r is null || art is null || title is null || artist is null || !ArtSize().IsMatch(art)) continue;

            var album = r["collectionName"]?.GetValue<string>() ?? "";
            var kind = album.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase) ? CoverReleaseKind.Single
                : album.EndsWith(" - EP", StringComparison.OrdinalIgnoreCase) ? CoverReleaseKind.EP
                : string.Equals(r["collectionArtistName"]?.GetValue<string>(), "Various Artists", StringComparison.OrdinalIgnoreCase) ? CoverReleaseKind.Compilation
                : (r["trackCount"]?.GetValue<int>() ?? 0) >= 7 ? CoverReleaseKind.Album
                : CoverReleaseKind.Unknown;
            TimeSpan? length = r["trackTimeMillis"] is JsonValue v && v.TryGetValue<long>(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
            var collectionId = r["collectionId"]?.ToString();

            list.Add(new CoverCandidate
            {
                Source = "iTunes",
                Title = title,
                Artist = artist,
                Album = album,
                Year = MusicBrainzCoverSource.YearOf(r["releaseDate"]?.GetValue<string>()),
                Kind = kind,
                Duration = length,
                ThumbnailUrl = ArtSize().Replace(art, "/300x300bb."),
                FullUrls = [ArtSize().Replace(art, "/1200x1200bb."), ArtSize().Replace(art, "/600x600bb.")],
                MaxSize = 1200,
                Key = collectionId is null ? null : "it:" + collectionId,
            });
        }
        return list;
    }

    [GeneratedRegex(@"/\d+x\d+bb\.")]
    private static partial Regex ArtSize();
}

/// <summary>The Deezer search API (no key). Its field filters (artist:"…") answer nothing from
/// some regions, so it's a plain text search; the ranking sorts out the strays.</summary>
public sealed class DeezerCoverSource(HttpClient http) : ICoverSource
{
    public string Name => "Deezer";

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverQuery query, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"{query.Artist} {query.Title}");
        var json = await http.GetStringAsync($"https://api.deezer.com/search?q={q}&limit=15", ct);
        return Parse(JsonNode.Parse(json));
    }

    public static List<CoverCandidate> Parse(JsonNode? root)
    {
        var list = new List<CoverCandidate>();
        foreach (var r in root?["data"]?.AsArray() ?? [])
        {
            var album = r?["album"];
            var thumb = album?["cover_medium"]?.GetValue<string>();
            var xl = album?["cover_xl"]?.GetValue<string>();
            var big = album?["cover_big"]?.GetValue<string>();
            var title = r?["title"]?.GetValue<string>();
            var artist = r?["artist"]?["name"]?.GetValue<string>();
            if (r is null || thumb is null || title is null || artist is null || (xl ?? big) is null) continue;

            TimeSpan? length = r["duration"] is JsonValue v && v.TryGetValue<int>(out var s) ? TimeSpan.FromSeconds(s) : null;
            list.Add(new CoverCandidate
            {
                Source = "Deezer",
                Title = title,
                Artist = artist,
                Album = album?["title"]?.GetValue<string>() ?? "",
                Kind = CoverReleaseKind.Unknown,
                Duration = length,
                ThumbnailUrl = thumb,
                FullUrls = new[] { xl, big }.OfType<string>().ToList(),
                MaxSize = xl is null ? 500 : 1000,
                Key = album?["id"] is { } id ? "dz:" + id : null,
            });
        }
        return list;
    }
}
