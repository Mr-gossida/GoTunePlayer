using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Looks up lyrics on LRCLIB only (open-licensed, no paywalled/ToS-restricted
/// providers). Every candidate - from the exact lookup and from search - is scored on title,
/// artist, length, album and whether it has synced lyrics; versions of a different length (live,
/// remix, a foreign-language release) are turned down rather than guessed.</summary>
public sealed class LrcLibLyricsService : ILyricsService, IDisposable
{
    /// <summary>Longer or shorter than this, a candidate is another recording of the song.</summary>
    private const double MaxLengthDifference = 8;

    private static readonly string[] VersionWords =
        ["live", "remix", "inst", "instrumental", "acoustic", "ver", "version", "japanese", "english", "chinese", "mr", "karaoke", "sped", "slowed"];

    private readonly HttpClient _http;

    public LrcLibLyricsService() : this(new HttpClientHandler())
    {
    }

    /// <summary>For tests: a handler standing in for the LRCLIB server.</summary>
    public LrcLibLyricsService(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { BaseAddress = new Uri("https://lrclib.net/api/") };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TuneSync/0.1 (+https://github.com/)");
    }

    public async Task<LyricsResult?> FindAsync(LyricsQuery query, CancellationToken ct = default)
    {
        var candidates = new List<LrcLibTrack>();

        // 1. The exact lookup, with album and length when known (LRCLIB then picks the recording
        //    within ~2 s of that length). A synced, well-fitting hit ends the search right here.
        if (await TryGetAsync(query, query.Title, ct) is { } exact)
        {
            candidates.Add(exact);
            if (Score(query, exact) is { } s && s >= GoodEnough && HasSynced(exact)) return ToResult(exact);
        }

        // 2. Without a "(feat. …)" / "(with …)" clause - a common reason the exact lookup misses.
        var bare = StripFeature(query.Title);
        if (bare != query.Title && await TryGetAsync(query, bare, ct) is { } bareHit) candidates.Add(bareHit);

        // 3. Search: by field first, then free text; every result is a candidate.
        candidates.AddRange(await SearchAsync($"search?track_name={Esc(bare)}&artist_name={Esc(query.Artist)}", ct));
        if (!candidates.Any(c => Score(query, c) is not null))
            candidates.AddRange(await SearchAsync($"search?q={Esc(query.Artist + " " + bare)}", ct));

        var best = candidates
            .Select(c => (Track: c, Score: Score(query, c)))
            .Where(c => c.Score is not null)
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();
        return best.Track is null ? null : ToResult(best.Track);
    }

    /// <summary>A candidate scoring at least this (title and length both right) can be taken
    /// without searching further.</summary>
    private const double GoodEnough = 8;

    /// <summary>How well a candidate fits, or null when it shouldn't be used at all.</summary>
    internal static double? Score(LyricsQuery query, LrcLibTrack candidate)
    {
        if (!HasSynced(candidate) && string.IsNullOrWhiteSpace(candidate.PlainLyrics)) return null; // nothing to use
        double score = 0;

        // Title: must match - exactly, or once "(feat. …)"-style extras are set aside.
        var title = Normalize(query.Title);
        var theirTitle = Normalize(candidate.TrackName);
        if (theirTitle == title) score += 4;
        else if (Normalize(StripBrackets(candidate.TrackName)) == Normalize(StripBrackets(query.Title))) score += 3;
        else return null;

        // A live/remix/… version we weren't asked for.
        if (VersionWords.Any(w => Words(candidate.TrackName).Contains(w) && !Words(query.Title).Contains(w))) score -= 3;

        // Length: the strongest sign it's the same recording.
        var lengthMatches = false;
        if (query.Seconds is { } seconds && candidate.Duration > 0)
        {
            var diff = Math.Abs(candidate.Duration - seconds);
            if (diff > MaxLengthDifference) return null;
            lengthMatches = diff <= 3;
            score += diff <= 2 ? 4 : diff <= 3 ? 3 : 1;
        }

        // Artist: exact, or one name inside the other ("IU" / "IU (아이유)"). A differently
        // written artist ("(G)I-DLE" / "(여자)아이들") is still fine when the length matches.
        var artist = Normalize(query.Artist);
        var theirArtist = Normalize(candidate.ArtistName);
        if (artist.Length > 0 && theirArtist == artist) score += 3;
        else if (artist.Length > 0 && theirArtist.Length > 0 && (theirArtist.Contains(artist) || artist.Contains(theirArtist))) score += 2;
        else if (!lengthMatches) return null;

        if (!string.IsNullOrWhiteSpace(query.Album) && Normalize(candidate.AlbumName ?? "") == Normalize(query.Album)) score += 1;
        if (HasSynced(candidate)) score += 2;
        return score;
    }

    private async Task<LrcLibTrack?> TryGetAsync(LyricsQuery query, string title, CancellationToken ct)
    {
        var url = new StringBuilder($"get?artist_name={Esc(query.Artist)}&track_name={Esc(title)}");
        if (!string.IsNullOrWhiteSpace(query.Album)) url.Append("&album_name=").Append(Esc(query.Album));
        if (query.Seconds is { } seconds) url.Append("&duration=").Append(Math.Round(seconds).ToString(CultureInfo.InvariantCulture));
        try
        {
            var resp = await _http.GetAsync(url.ToString(), ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<LrcLibTrack>(cancellationToken: ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<List<LrcLibTrack>> SearchAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];
            return await resp.Content.ReadFromJsonAsync<List<LrcLibTrack>>(cancellationToken: ct) ?? [];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private static bool HasSynced(LrcLibTrack track) => !string.IsNullOrWhiteSpace(track.SyncedLyrics);

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string StripFeature(string title)
    {
        var idx = title.IndexOf("(feat.", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = title.IndexOf("(with ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = title.IndexOf("(prod.", StringComparison.OrdinalIgnoreCase);
        return idx <= 0 ? title : title[..idx].Trim();
    }

    /// <summary>Drops every "(…)" and "[…]" part: "Good Day (Japanese Ver.)" → "Good Day".</summary>
    private static string StripBrackets(string title)
    {
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var c in title)
        {
            if (c is '(' or '[' or '（') depth++;
            else if (c is ')' or ']' or '）') depth = Math.Max(0, depth - 1);
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    private static HashSet<string> Words(string s) =>
        [.. s.ToLowerInvariant().Split([' ', '(', ')', '[', ']', '.', '-', '/', '_'], StringSplitOptions.RemoveEmptyEntries)];

    private static string Normalize(string s) =>
        new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static LyricsResult ToResult(LrcLibTrack dto) => new()
    {
        TrackName = dto.TrackName,
        ArtistName = dto.ArtistName,
        AlbumName = dto.AlbumName,
        Instrumental = dto.Instrumental,
        PlainLyrics = dto.PlainLyrics,
        SyncedLyrics = dto.SyncedLyrics,
    };

    public void Dispose() => _http.Dispose();

    internal sealed class LrcLibTrack
    {
        [JsonPropertyName("trackName")] public string TrackName { get; set; } = "";
        [JsonPropertyName("artistName")] public string ArtistName { get; set; } = "";
        [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }
}
