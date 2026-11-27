using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TuneSync.Core.Services.CoverSearch;

namespace TuneSync.Core.Services.TagSearch;

/// <summary>One place tags can come from. Each source turns the song into candidates; the
/// ranking (<see cref="TagRanking"/>) decides which are worth showing. All of them are open, free
/// APIs that need no key and allow this use; only the artist, title (and album) are sent.</summary>
public interface ITagSource
{
    string Name { get; }

    Task<IReadOnlyList<TagCandidate>> SearchAsync(TagQuery query, CancellationToken ct);
}

/// <summary>MusicBrainz, the open music encyclopedia: recordings with the releases they're on
/// (album, album artist, year, track and disc number). Goes through <see cref="MusicBrainzService"/>
/// for its one-request-a-second limit.</summary>
public sealed class MusicBrainzTagSource(MusicBrainzService musicBrainz) : ITagSource
{
    public const string SourceName = "MusicBrainz";

    public string Name => SourceName;

    public async Task<IReadOnlyList<TagCandidate>> SearchAsync(TagQuery query, CancellationToken ct) =>
        Parse(await musicBrainz.SearchRecordingsAsync(query.Artist, query.Title, 10, ct));

    /// <summary>Each plausible recording on up to two of its releases: the one it most likely
    /// belongs to (an official studio album before a single, a compilation or a live record;
    /// the earliest of those) and the next of another kind.</summary>
    public static List<TagCandidate> Parse(JsonArray? recordings)
    {
        var list = new List<TagCandidate>();
        foreach (var rec in recordings ?? [])
        {
            if (rec is null || (rec["score"]?.GetValue<int>() ?? 0) < 50) continue;
            var title = rec["title"]?.GetValue<string>();
            var artist = Credit(rec);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
            TimeSpan? length = rec["length"] is JsonValue v && v.TryGetValue<long>(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;

            var releases = (rec["releases"]?.AsArray() ?? []).OfType<JsonNode>()
                .OrderBy(Preference)
                .ThenBy(r => r["date"]?.GetValue<string>() is { Length: >= 4 } d ? d : "9999")
                .ToList();
            var kinds = new HashSet<int>();
            foreach (var rel in releases)
            {
                if (!kinds.Add(Preference(rel))) continue; // one release of each kind
                if (kinds.Count > 2) break;
                var media = rel["media"]?.AsArray()?.OfType<JsonNode>().FirstOrDefault();
                var track = media?["track"]?.AsArray()?.OfType<JsonNode>().FirstOrDefault();
                list.Add(new TagCandidate
                {
                    Source = SourceName,
                    Title = title,
                    Artist = artist,
                    Album = rel["title"]?.GetValue<string>() ?? "",
                    AlbumArtist = Credit(rel) ?? artist,
                    Year = YearOf(rel["date"]?.GetValue<string>()),
                    Track = uint.TryParse(track?["number"]?.GetValue<string>(), out var number) && number > 0 ? number : null,
                    TrackCount = media?["track-count"]?.GetValue<uint>() is > 0 and var count ? count : null,
                    Disc = media?["position"]?.GetValue<uint>() is > 0 and var disc ? disc : null,
                    Duration = length,
                });
            }
        }
        return list;
    }

    /// <summary>0 for an official studio album, then EPs and singles, then the rest
    /// (compilations, live records, bootlegs).</summary>
    private static int Preference(JsonNode release)
    {
        var official = release["status"]?.GetValue<string>() == "Official";
        var group = release["release-group"];
        var type = group?["primary-type"]?.GetValue<string>();
        var plain = group?["secondary-types"]?.AsArray() is not { Count: > 0 };
        return !official ? 4 : !plain ? 3 : type switch { "Album" => 0, "EP" => 1, "Single" => 2, _ => 3 };
    }

    /// <summary>The whole credit ("Paul Blanco & Heize"), not just its first name.</summary>
    private static string? Credit(JsonNode node)
    {
        var credits = node["artist-credit"]?.AsArray();
        if (credits is not { Count: > 0 }) return null;
        var sb = new StringBuilder();
        foreach (var c in credits)
            sb.Append(c?["name"]?.GetValue<string>()).Append(c?["joinphrase"]?.GetValue<string>());
        return sb.ToString().Trim() is { Length: > 0 } s ? s : null;
    }

    internal static uint? YearOf(string? date) =>
        date is { Length: >= 4 } && uint.TryParse(date[..4], out var year) && year is >= 1900 and <= 2100 ? year : null;
}

/// <summary>The iTunes Search API (no key): album, album artist, year, genre, track and disc
/// numbers. Searched in the US and Japanese stores, as the cover search does (this API doesn't
/// serve the Korean one); Korean songs come back under their international names there.</summary>
public sealed partial class ITunesTagSource(HttpClient http) : ITagSource
{
    public const string SourceName = "iTunes";
    private static readonly string[] Countries = ["US", "JP"];

    public string Name => SourceName;

    public async Task<IReadOnlyList<TagCandidate>> SearchAsync(TagQuery query, CancellationToken ct)
    {
        var term = Uri.EscapeDataString($"{query.Artist} {query.Title}");
        var results = new List<TagCandidate>();
        foreach (var country in Countries)
        {
            var json = await http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=15&country={country}", ct);
            results.AddRange(Parse(JsonNode.Parse(json)));
        }
        return results;
    }

    public static List<TagCandidate> Parse(JsonNode? root)
    {
        var list = new List<TagCandidate>();
        foreach (var r in root?["results"]?.AsArray() ?? [])
        {
            var title = r?["trackName"]?.GetValue<string>();
            var artist = r?["artistName"]?.GetValue<string>();
            if (r is null || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
            TimeSpan? length = r["trackTimeMillis"] is JsonValue v && v.TryGetValue<long>(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
            list.Add(new TagCandidate
            {
                Source = SourceName,
                Title = title,
                Artist = artist,
                // iTunes names a single "밤편지 - Single": the album tag is the name alone
                Album = StoreSuffix().Replace(r["collectionName"]?.GetValue<string>() ?? "", ""),
                AlbumArtist = r["collectionArtistName"]?.GetValue<string>() ?? artist,
                Year = MusicBrainzTagSource.YearOf(r["releaseDate"]?.GetValue<string>()),
                Genre = r["primaryGenreName"]?.GetValue<string>() ?? "",
                Track = Number(r["trackNumber"]),
                TrackCount = Number(r["trackCount"]),
                Disc = Number(r["discNumber"]),
                Duration = length,
            });
        }
        return list;
    }

    internal static uint? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) && n > 0 ? (uint)n : null;

    [GeneratedRegex(@"\s+-\s+(Single|EP)$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreSuffix();
}

/// <summary>The Deezer API (no key). Its search answers title, artist, album and length; the track
/// and album pages add the track and disc number, year, genre and album artist - asked for the
/// few results that look like the song only.</summary>
public sealed class DeezerTagSource(HttpClient http) : ITagSource
{
    public const string SourceName = "Deezer";

    /// <summary>Results whose details (two more requests each) are fetched.</summary>
    private const int Detailed = 4;

    public string Name => SourceName;

    public async Task<IReadOnlyList<TagCandidate>> SearchAsync(TagQuery query, CancellationToken ct)
    {
        // Its field filters (artist:"…") answer nothing from some regions: a plain text search.
        var q = Uri.EscapeDataString($"{query.Artist} {query.Title}");
        var found = JsonNode.Parse(await http.GetStringAsync($"https://api.deezer.com/search?q={q}&limit=15", ct))?["data"]?.AsArray() ?? [];
        var likely = found.OfType<JsonNode>()
            .Select(r => (Node: r, Title: r["title"]?.GetValue<string>() ?? "", Artist: r["artist"]?["name"]?.GetValue<string>() ?? ""))
            .Where(r => r.Title.Length > 0 && r.Artist.Length > 0 && CoverRanking.TitleSimilarity(query.Title, r.Title) >= 0.5)
            .OrderByDescending(r => CoverRanking.TitleSimilarity(query.Title, r.Title) + CoverRanking.ArtistSimilarity(query.Artist, r.Artist))
            .Take(Detailed)
            .ToList();
        var details = await Task.WhenAll(likely.Select(r => DetailsAsync(r.Node, ct)));
        return [.. likely.Select((r, i) => Build(r.Node, details[i].Track, details[i].Album))];
    }

    private async Task<(JsonNode? Track, JsonNode? Album)> DetailsAsync(JsonNode result, CancellationToken ct)
    {
        async Task<JsonNode?> Get(string url)
        {
            try { return JsonNode.Parse(await http.GetStringAsync(url, ct)); }
            catch (Exception) when (!ct.IsCancellationRequested) { return null; } // the search's own fields still stand
        }
        var trackId = result["id"]?.ToString();
        var albumId = result["album"]?["id"]?.ToString();
        var track = trackId is null ? Task.FromResult<JsonNode?>(null) : Get($"https://api.deezer.com/track/{trackId}");
        var album = albumId is null ? Task.FromResult<JsonNode?>(null) : Get($"https://api.deezer.com/album/{albumId}");
        return (await track, await album);
    }

    /// <summary>A candidate from a search result and, when they could be had, its track and album pages.</summary>
    public static TagCandidate Build(JsonNode result, JsonNode? track, JsonNode? album)
    {
        var artist = result["artist"]?["name"]?.GetValue<string>() ?? "";
        TimeSpan? length = result["duration"] is JsonValue v && v.TryGetValue<int>(out var s) && s > 0 ? TimeSpan.FromSeconds(s) : null;
        var genre = album?["genres"]?["data"]?.AsArray()?.OfType<JsonNode>().FirstOrDefault()?["name"]?.GetValue<string>();
        return new TagCandidate
        {
            Source = SourceName,
            Title = result["title"]?.GetValue<string>() ?? "",
            Artist = artist,
            Album = result["album"]?["title"]?.GetValue<string>() ?? "",
            AlbumArtist = album?["artist"]?["name"]?.GetValue<string>() ?? artist,
            Year = MusicBrainzTagSource.YearOf(track?["release_date"]?.GetValue<string>() ?? album?["release_date"]?.GetValue<string>()),
            Genre = genre ?? "",
            Track = ITunesTagSource.Number(track?["track_position"]),
            TrackCount = ITunesTagSource.Number(album?["nb_tracks"]),
            Disc = ITunesTagSource.Number(track?["disk_number"]),
            Duration = length,
        };
    }
}
