using System.Diagnostics.CodeAnalysis;

namespace TuneSync.App;

/// <summary>The last few results worked out per song (waveform, spectrum), by path: going back
/// to a recent song shows them at once instead of analyzing the file again. The oldest entry
/// leaves when a new one goes over <paramref name="capacity"/>.</summary>
internal sealed class RecentCache<T>(int capacity)
{
    private readonly Dictionary<string, T> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = []; // oldest first; small (tens of entries)

    public bool TryGet(string path, [MaybeNullWhen(false)] out T value) => _items.TryGetValue(path, out value);

    public void Add(string path, T value)
    {
        // A path added again moves to the newest end instead of being listed twice (its older
        // listing would otherwise evict the fresh entry early).
        _order.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _items[path] = value;
        _order.Add(path);
        while (_order.Count > capacity)
        {
            _items.Remove(_order[0]);
            _order.RemoveAt(0);
        }
    }
}

/// <summary>One kind of background analysis of the playing song (waveform, spectrum): a new song
/// cancels the previous one's, recent results come from a <see cref="RecentCache{T}"/>, and a
/// result is shown only if its song is still the one playing.</summary>
internal sealed class BackgroundAnalysis<T>(int capacity)
{
    private readonly RecentCache<T> _cache = new(capacity);
    private CancellationTokenSource? _cts;

    /// <param name="readable">The local file to analyze for <c>path</c> (a network song's downloaded copy).</param>
    /// <param name="show">Shows a result, or default while one is being worked out.</param>
    /// <param name="isCurrent">Whether <c>path</c> is still the song playing.</param>
    public async void Load(string path, Func<string, Task<string>> readable, Func<string, CancellationToken, T> analyze,
        Action<T?> show, Func<string, bool> isCurrent)
    {
        Cancel();
        if (_cache.TryGet(path, out var cached))
        {
            show(cached);
            return;
        }
        show(default);
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        T result;
        try
        {
            var file = await readable(path);
            result = await Task.Run(() => analyze(file, token), token);
        }
        catch (Exception)
        {
            return; // cancelled (another song) or unreadable: nothing to show
        }
        _cache.Add(path, result);
        if (!token.IsCancellationRequested && isCurrent(path)) show(result);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
