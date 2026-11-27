using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TuneSync.Core.Services.CoverSearch;

namespace TuneSync.Core.Services;

/// <summary>Keeps to what a keyless public API asks of its users: the iTunes Search API about 20
/// calls a minute, Deezer 50 in 5 seconds. Searching many songs at once (통합 검색) would run past
/// both within seconds - then they refuse (403 / 429) and the rest of the songs get nothing from
/// them - so a call beyond the limit waits its turn instead. The tag and cover searches send the
/// same search for a song; its answer is kept a few minutes, so the second one isn't sent again.</summary>
public sealed class ApiGate(int limit, TimeSpan window)
{
    /// <summary>Apple: "approximately 20 calls per minute".</summary>
    public static ApiGate ITunes { get; } = new(20, TimeSpan.FromMinutes(1));

    /// <summary>Deezer: 50 requests per 5 seconds; a little under it.</summary>
    public static ApiGate Deezer { get; } = new(45, TimeSpan.FromSeconds(5));

    private const int MaxAnswers = 300;
    private static readonly TimeSpan KeepAnswers = TimeSpan.FromMinutes(10);

    // Stopwatch timestamps: steady (unlike the wall clock) and finer than the 15 ms system tick.
    private readonly Queue<long> _sent = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly ConcurrentDictionary<string, (long At, string Body)> _answers = new();

    /// <summary>The body of a GET, from the last few minutes' answers when it was asked already,
    /// else asked when the limit allows.</summary>
    public async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (_answers.TryGetValue(url, out var kept) && Stopwatch.GetElapsedTime(kept.At) < KeepAnswers) return kept.Body;
        await WaitTurnAsync(ct);
        var body = await http.GetStringAsync(url, ct);
        if (_answers.Count >= MaxAnswers) Prune();
        _answers[url] = (Stopwatch.GetTimestamp(), body);
        return body;
    }

    /// <summary>Returns once one more call fits in the window (callers wait in turn).</summary>
    public async Task WaitTurnAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct);
        try
        {
            while (true)
            {
                while (_sent.Count > 0 && Stopwatch.GetElapsedTime(_sent.Peek()) >= window) _sent.Dequeue();
                if (_sent.Count < limit)
                {
                    _sent.Enqueue(Stopwatch.GetTimestamp());
                    return;
                }
                await Task.Delay(window - Stopwatch.GetElapsedTime(_sent.Peek()) + TimeSpan.FromMilliseconds(1), ct);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    private void Prune()
    {
        foreach (var (url, answer) in _answers)
            if (Stopwatch.GetElapsedTime(answer.At) >= KeepAnswers) _answers.TryRemove(url, out _);
        if (_answers.Count >= MaxAnswers) _answers.Clear();
    }
}

/// <summary>The iTunes Search API's song search, shared by the tag and cover searches (so a song
/// searched for both is asked once). The US store first - this API doesn't serve the Korean one,
/// and the US one has most songs, Korean ones under their international names; the Japanese store
/// only when the US one has nothing of that title (J-pop, anime and game music). One call a song
/// keeps a batch of songs inside the API's limit.</summary>
public static class ITunesSearch
{
    private static readonly string[] Countries = ["US", "JP"];

    /// <summary>The stores' answers ({"results": [...]}), the US one first.</summary>
    public static async Task<List<JsonNode>> SongsAsync(HttpClient http, ApiGate gate, string artist, string title, CancellationToken ct)
    {
        var term = Uri.EscapeDataString($"{artist} {title}");
        var answers = new List<JsonNode>();
        foreach (var country in Countries)
        {
            var json = await gate.GetStringAsync(http, $"https://itunes.apple.com/search?term={term}&entity=song&limit=15&country={country}", ct);
            if (JsonNode.Parse(json) is not { } root) continue;
            answers.Add(root);
            if (HasTitle(root, title)) break;
        }
        return answers;
    }

    private static bool HasTitle(JsonNode root, string title) =>
        (root["results"]?.AsArray() ?? []).Any(r => r?["trackName"]?.GetValue<string>() is { } name
                                                    && CoverRanking.TitleSimilarity(title, name) >= 0.5);
}
