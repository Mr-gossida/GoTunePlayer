using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Looks up lyrics on LRCLIB only (open-licensed), never Melon/Bugs/Genius — see the
/// project's lyrics-sourcing rule this app grew out of. A search match is only trusted when the
/// returned artist plausibly matches ours; otherwise it's reported as "not found" rather than guessed.</summary>
public sealed class LyricsService : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://lrclib.net/api/") };

    public LyricsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TuneSync/0.1 (+https://github.com/)");
    }

    public async Task<LyricsResult?> FindAsync(string artist, string title, CancellationToken ct = default)
    {
        var direct = await TryGetAsync(artist, title, ct);
        if (direct is not null) return direct;

        // Retry with a parenthetical feat./with clause stripped, per the project's search-tip rule.
        var strippedTitle = StripFeature(title);
        if (strippedTitle != title)
        {
            direct = await TryGetAsync(artist, strippedTitle, ct);
            if (direct is not null) return direct;
        }

        return await TrySearchAsync(artist, title, ct);
    }

    private async Task<LyricsResult?> TryGetAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            var url = $"get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var dto = await resp.Content.ReadFromJsonAsync<LrcLibTrack>(cancellationToken: ct);
            return dto is null ? null : ToResult(dto);
        }
        catch
        {
            return null;
        }
    }

    private async Task<LyricsResult?> TrySearchAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            var url = $"search?q={Uri.EscapeDataString(artist + " " + title)}";
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var results = await resp.Content.ReadFromJsonAsync<List<LrcLibTrack>>(cancellationToken: ct);
            if (results is null || results.Count == 0) return null;

            var normArtist = Normalize(artist);
            var match = results.FirstOrDefault(r => Normalize(r.ArtistName) == normArtist)
                ?? results.FirstOrDefault(r => Normalize(r.ArtistName).Contains(normArtist) || normArtist.Contains(Normalize(r.ArtistName)));
            return match is null ? null : ToResult(match);
        }
        catch
        {
            return null;
        }
    }

    private static string StripFeature(string title)
    {
        var idx = title.IndexOf("(feat.", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = title.IndexOf("(with ", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? title : title[..idx].Trim();
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

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

    private sealed class LrcLibTrack
    {
        [JsonPropertyName("trackName")] public string TrackName { get; set; } = "";
        [JsonPropertyName("artistName")] public string ArtistName { get; set; } = "";
        [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }
}
