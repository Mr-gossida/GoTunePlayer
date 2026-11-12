using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Looks up canonical tag data on MusicBrainz and cover art on the companion Cover Art
/// Archive (both free, open, public APIs - no key required). MusicBrainz's usage policy requires
/// a descriptive User-Agent and caps anonymous callers at roughly one request per second; both
/// are enforced here so the app never risks the caller's IP getting rate-limited or blocked.
/// See https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting.</summary>
public sealed class MusicBrainzService : IMusicBrainzService, IDisposable
{
    private const string UserAgentString = "TuneSync/0.1 (+https://github.com/)";
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1100);

    private readonly HttpClient _mbHttp;
    private readonly HttpClient _caaHttp;
    private readonly RateLimiter _rateLimiter = new(MinRequestInterval);

    public MusicBrainzService()
    {
        _mbHttp = new HttpClient { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") };
        _mbHttp.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentString);
        _mbHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _caaHttp = new HttpClient { BaseAddress = new Uri("https://coverartarchive.org/") };
        _caaHttp.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentString);
    }

    /// <summary>Searches MusicBrainz recordings for the given artist/title and returns the
    /// best-scoring hit that carries a usable release MBID, or null if nothing plausible matched
    /// (caller should treat that file as "매칭 실패" and leave it to manual editing).</summary>
    public async Task<MusicBrainzMatch?> FindAsync(string artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return null;

        var query = $"artist:\"{EscapeLucene(artist)}\" AND recording:\"{EscapeLucene(title)}\"";
        var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";

        // A failed request is an error, not "no match": the caller shows it (검색 오류: …) instead
        // of telling the user the song doesn't exist.
        JsonNode? root;
        try
        {
            using var resp = await RateLimitedGetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
                    ? "MusicBrainz가 요청이 많아 잠시 응답하지 않습니다. 조금 뒤 다시 시도하세요."
                    : $"MusicBrainz 응답 오류 ({(int)resp.StatusCode})");
            var json = await resp.Content.ReadAsStringAsync(ct);
            root = JsonNode.Parse(json);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not HttpRequestException { InnerException: null })
        {
            throw new HttpRequestException("MusicBrainz에 연결하지 못했습니다. 인터넷 연결을 확인하세요.", ex);
        }

        var recordings = root?["recordings"]?.AsArray();
        if (recordings is null || recordings.Count == 0) return null;

        // MusicBrainz treats a studio recording and a live/session recording of the same song as
        // different recording entities, each scored independently - the top-scored one isn't
        // necessarily the studio version, and its own release list won't include the studio album
        // at all. So: scan every plausible recording (score >= 50) and prefer whichever one has an
        // official studio-album release; only fall back to the first valid release of the first
        // valid recording if nothing in the whole candidate set qualifies.
        MusicBrainzMatch? fallback = null;
        foreach (var rec in recordings)
        {
            if (rec is null) continue;
            var score = rec["score"]?.GetValue<int>() ?? 0;
            if (score < 50) continue; // too loose a match to trust unattended

            var recTitle = rec["title"]?.GetValue<string>();
            var artistCredit = rec["artist-credit"]?.AsArray();
            var recArtist = artistCredit?.Count > 0 ? artistCredit[0]?["name"]?.GetValue<string>() : null;
            if (string.IsNullOrWhiteSpace(recTitle) || string.IsNullOrWhiteSpace(recArtist)) continue;

            var releases = rec["releases"]?.AsArray();
            if (releases is null || releases.Count == 0) continue;

            var preferred = releases.FirstOrDefault(IsPreferredRelease);
            var release = preferred ?? releases.FirstOrDefault(r => r is not null);
            if (release is null) continue;

            var match = BuildMatch(release, recTitle, recArtist, score);
            if (preferred is not null) return match;
            fallback ??= match;
        }

        return fallback;

        static MusicBrainzMatch BuildMatch(JsonNode release, string recTitle, string recArtist, int score)
        {
            var releaseMbid = release["id"]?.GetValue<string>();
            var album = release["title"]?.GetValue<string>();
            var releaseArtistCredit = release["artist-credit"]?.AsArray();
            var albumArtist = releaseArtistCredit?.Count > 0 ? releaseArtistCredit[0]?["name"]?.GetValue<string>() : recArtist;

            uint? track = null, disc = null;
            var media = release["media"]?.AsArray()?.FirstOrDefault(m => m is not null);
            if (media is not null)
            {
                disc = media["position"]?.GetValue<uint>();
                var tracks = media["track"]?.AsArray();
                var trackEntry = tracks?.FirstOrDefault(t => t is not null);
                if (trackEntry is not null && uint.TryParse(trackEntry["number"]?.GetValue<string>(), out var tn))
                    track = tn;
            }

            return new MusicBrainzMatch
            {
                Title = recTitle,
                Artist = recArtist,
                Album = album,
                AlbumArtist = albumArtist,
                ReleaseMbid = releaseMbid,
                Track = track,
                Disc = disc,
                Score = score,
            };
        }

        static bool IsPreferredRelease(JsonNode? release)
        {
            if (release is null) return false;
            if (release["status"]?.GetValue<string>() != "Official") return false;
            var releaseGroup = release["release-group"];
            if (releaseGroup?["primary-type"]?.GetValue<string>() != "Album") return false;
            var secondaryTypes = releaseGroup["secondary-types"]?.AsArray();
            return secondaryTypes is null || secondaryTypes.Count == 0;
        }
    }

    /// <summary>Downloads front cover art for a release from the Cover Art Archive. Returns null
    /// if that release has no art on file there (common for obscure releases - not an error, just
    /// nothing to apply).</summary>
    public async Task<byte[]?> GetCoverAsync(string releaseMbid, CancellationToken ct = default)
    {
        try
        {
            // 1200 px when the archive has it (most releases), else 500 px.
            foreach (var size in new[] { "front-1200", "front-500" })
            {
                using var resp = await _caaHttp.GetAsync($"release/{releaseMbid}/{size}", ct);
                if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            return null;
        }
        catch (Exception) when (ct.IsCancellationRequested == false)
        {
            return null;
        }
    }

    /// <summary>One request per second (MusicBrainz's limit); when it still answers "busy" (503 /
    /// 429), waits a little longer and tries again, twice.</summary>
    private async Task<HttpResponseMessage> RateLimitedGetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _rateLimiter.WaitAsync(ct);
            var resp = await _mbHttp.GetAsync(url, ct);
            if (resp.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests) || attempt == 2) return resp;
            resp.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
        }
    }

    private static string EscapeLucene(string value) =>
        // MusicBrainz search uses Lucene query syntax; quotes inside a quoted phrase must be escaped.
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose()
    {
        _mbHttp.Dispose();
        _caaHttp.Dispose();
    }
}
