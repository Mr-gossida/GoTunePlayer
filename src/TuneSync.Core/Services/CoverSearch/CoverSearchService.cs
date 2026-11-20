using TuneSync.Core.Models;

namespace TuneSync.Core.Services.CoverSearch;

/// <summary>How far a cover search is: <paramref name="Done"/> of <paramref name="Total"/>
/// sources have answered, the last being <paramref name="Source"/>.</summary>
public sealed record CoverSearchProgress(int Done, int Total, string Source);

/// <summary>Ranked candidates, best first, and the sources that couldn't be reached.</summary>
public sealed record CoverSearchResult(IReadOnlyList<CoverCandidate> Candidates, IReadOnlyList<string> FailedSources);

public interface ICoverSearchService
{
    /// <summary>Asks every source at once and ranks what comes back. Throws only when every
    /// source failed (the message says why); otherwise failed sources are listed in the result.</summary>
    Task<CoverSearchResult> SearchAsync(CoverQuery query, IProgress<CoverSearchProgress>? progress = null, CancellationToken ct = default);

    /// <summary>The small gallery image; null when the source has none after all.</summary>
    Task<byte[]?> GetThumbnailAsync(CoverCandidate candidate, CancellationToken ct = default);

    /// <summary>The full-size image (the first of <see cref="CoverCandidate.FullUrls"/> that
    /// answers); null when none does.</summary>
    Task<byte[]?> GetFullAsync(CoverCandidate candidate, CancellationToken ct = default);
}

/// <summary>Finds album covers on MusicBrainz/Cover Art Archive, iTunes and Deezer and ranks them
/// with <see cref="CoverRanking"/>. Only the artist and title (and album) leave the computer, with
/// the same plain "GoTunePlayer/1.0" user agent as every other lookup.</summary>
public sealed class CoverSearchService : ICoverSearchService, IDisposable
{
    private const long MaxImageBytes = 20 * 1024 * 1024;

    private readonly IReadOnlyList<ICoverSource> _sources;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    // A gallery asks for a dozen thumbnails at once; a few at a time is plenty and polite.
    private readonly SemaphoreSlim _downloads = new(4, 4);

    public CoverSearchService(MusicBrainzService musicBrainz)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(MusicBrainzService.UserAgentString);
        _ownsHttp = true;
        _sources = [new MusicBrainzCoverSource(musicBrainz), new ITunesCoverSource(_http), new DeezerCoverSource(_http)];
    }

    /// <summary>For tests: given sources, and the client images are downloaded with.</summary>
    public CoverSearchService(IReadOnlyList<ICoverSource> sources, HttpClient http)
    {
        _sources = sources;
        _http = http;
    }

    /// <summary>How long one image may take, its body included: the client's own timeout ends
    /// with the headers, and a connection that stalls halfway (after sleep, a dropped Wi-Fi)
    /// would otherwise hold one of the four download slots for good.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<CoverSearchResult> SearchAsync(CoverQuery query, IProgress<CoverSearchProgress>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = new List<(string Source, Exception Error)>();
        var found = new List<CoverCandidate>();

        await Task.WhenAll(_sources.Select(async source =>
        {
            try
            {
                var list = await source.SearchAsync(query, ct);
                lock (found) found.AddRange(list);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lock (found) failed.Add((source.Name, ex));
            }
            progress?.Report(new CoverSearchProgress(Interlocked.Increment(ref done), _sources.Count, source.Name));
        }));
        ct.ThrowIfCancellationRequested();

        if (failed.Count == _sources.Count && _sources.Count > 0)
        {
            var first = failed[0].Error;
            throw first is HttpRequestException { InnerException: null } ? first
                : new HttpRequestException("커버 검색 서버에 연결하지 못했습니다. 인터넷 연결을 확인하세요.", first);
        }

        return new CoverSearchResult(CoverRanking.Rank(query, found), failed.Select(f => f.Source).ToList());
    }

    public Task<byte[]?> GetThumbnailAsync(CoverCandidate candidate, CancellationToken ct = default) =>
        DownloadAsync(candidate.ThumbnailUrl, ct);

    public async Task<byte[]?> GetFullAsync(CoverCandidate candidate, CancellationToken ct = default)
    {
        foreach (var url in candidate.FullUrls)
        {
            var bytes = await DownloadAsync(url, ct);
            if (bytes is not null) return bytes;
        }
        return null;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (SecureUri(url) is not { } uri) return null;
        await _downloads.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxImageBytes) return null;
            var bytes = await ReadAtMostAsync(resp.Content, MaxImageBytes, timeout.Token);
            return bytes is not null && IsImage(bytes) ? bytes : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // a missing picture just drops that tile (so does one too slow or too big)
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Images only come over HTTPS: their addresses are taken from the sources' answers,
    /// and a picture sent in the clear could be swapped on the way. A plain http address is asked
    /// for over https (all three sources serve that); anything else is refused.</summary>
    private static Uri? SecureUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsDefaultPort
            ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri
            : null;
    }

    /// <summary>The body, or null once it runs past <paramref name="max"/> bytes: the length a
    /// server announces can't be relied on (a chunked answer has none).</summary>
    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, long max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > max) return null;
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    /// <summary>JPEG or PNG - what tags can embed (and not an HTML error page).</summary>
    public static bool IsImage(byte[] b) =>
        b.Length > 8 && ((b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47));

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _downloads.Dispose();
    }
}
