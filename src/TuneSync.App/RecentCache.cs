using System.Diagnostics.CodeAnalysis;

namespace TuneSync.App;

/// <summary>The last few results worked out per song (waveform, spectrum), by path: going back
/// to a recent song shows them at once instead of analyzing the file again. The oldest entry
/// leaves when a new one goes over <paramref name="capacity"/>.</summary>
internal sealed class RecentCache<T>(int capacity)
{
    private readonly Dictionary<string, T> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    public bool TryGet(string path, [MaybeNullWhen(false)] out T value) => _items.TryGetValue(path, out value);

    public void Add(string path, T value)
    {
        _items[path] = value;
        _order.Enqueue(path);
        while (_order.Count > capacity) _items.Remove(_order.Dequeue());
    }
}
