namespace TuneSync.Core.Services;

/// <summary>Serializes callers so no two complete within less than <paramref name="minInterval"/>
/// of each other - used to respect MusicBrainz's ~1 req/sec anonymous rate limit.</summary>
public sealed class RateLimiter(TimeSpan minInterval)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastUtc = DateTime.MinValue;

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastUtc;
            if (elapsed < minInterval)
                await Task.Delay(minInterval - elapsed, ct);
            _lastUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
