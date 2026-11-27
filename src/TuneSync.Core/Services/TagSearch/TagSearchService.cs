namespace TuneSync.Core.Services.TagSearch;

/// <summary>Finds a song's tags on MusicBrainz, iTunes and Deezer at once (the ones the user has
/// on) and ranks the answers (<see cref="TagRanking"/>), so the best fits can be offered side by
/// side, each with where it came from. Only the artist, title and album leave the computer, with
/// the same plain "GoTunePlayer/1.0" user agent as every other lookup.</summary>
public sealed class TagSearchService : IDisposable
{
    private readonly IReadOnlyList<ITagSource> _sources;
    private readonly HttpClient? _http;

    public TagSearchService(MusicBrainzService musicBrainz)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(MusicBrainzService.UserAgentString);
        _sources = [new MusicBrainzTagSource(musicBrainz), new ITunesTagSource(_http), new DeezerTagSource(_http)];
    }

    /// <summary>For tests: given sources.</summary>
    public TagSearchService(IReadOnlyList<ITagSource> sources) => _sources = sources;

    /// <summary>Every source's name, in the order they're offered.</summary>
    public IReadOnlyList<string> SourceNames => [.. _sources.Select(s => s.Name)];

    /// <summary>Asks the sources not in <paramref name="disabled"/> at once and ranks what comes
    /// back. Throws only when every source asked failed (the message says why); otherwise failed
    /// sources are listed in the result.</summary>
    public async Task<TagSearchResult> SearchAsync(TagQuery query, IReadOnlyCollection<string> disabled,
        IProgress<SourceProgress>? progress = null, CancellationToken ct = default)
    {
        var sources = _sources.Where(s => !disabled.Contains(s.Name)).ToList();
        if (sources.Count == 0) sources = [.. _sources]; // all switched off: there's nothing else to ask
        var done = 0;
        var failed = new List<(string Source, Exception Error)>();
        // each source's answers in its own slot: put together in the order the sources are offered
        // (not the order they answered), so equally good results always rank the same way
        var answers = new IReadOnlyList<TagCandidate>[sources.Count];

        await Task.WhenAll(sources.Select(async (source, i) =>
        {
            try
            {
                answers[i] = await source.SearchAsync(query, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lock (failed) failed.Add((source.Name, ex));
            }
            progress?.Report(new SourceProgress(Interlocked.Increment(ref done), sources.Count, source.Name));
        }));
        ct.ThrowIfCancellationRequested();
        var found = answers.Where(a => a is not null).SelectMany(a => a).ToList();

        if (failed.Count == sources.Count)
        {
            var first = failed[0].Error;
            throw first is HttpRequestException { InnerException: null } ? first
                : new HttpRequestException("태그 검색 서버에 연결하지 못했습니다. 인터넷 연결을 확인하세요.", first);
        }
        // in the order the sources are offered, so the list reads the same every time
        var failedNames = sources.Select(s => s.Name).Where(n => failed.Any(f => f.Source == n)).ToList();
        return new TagSearchResult(TagRanking.Rank(query, found), [.. sources.Select(s => s.Name)], failedNames);
    }

    public void Dispose() => _http?.Dispose();
}
