using System.Net;
using System.Text;
using System.Text.Json;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class LrcLibLyricsServiceTests
{
    /// <summary>Stands in for lrclib.net: "get" answers with <see cref="Get"/> (404 when null),
    /// "search" with <see cref="Search"/>. Records the requests made.</summary>
    private sealed class FakeLrcLib : HttpMessageHandler
    {
        public object? Get { get; set; }
        public List<object> Search { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(Uri.UnescapeDataString(path));
            object? body = path.Contains("/api/get") ? Get : Search;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                });
        }
    }

    private static object Track(string artist, string title, double duration, string? album = null, bool synced = true, string lyrics = "가사") => new
    {
        artistName = artist,
        trackName = title,
        albumName = album,
        duration,
        instrumental = false,
        plainLyrics = lyrics,
        syncedLyrics = synced ? $"[00:01.00]{lyrics}" : null,
    };

    private static readonly LyricsQuery GoodDay = new("IU", "Good Day", "REAL", TimeSpan.FromSeconds(234));

    [Fact]
    public async Task Sends_album_and_length_to_the_exact_lookup()
    {
        var server = new FakeLrcLib { Get = Track("IU", "Good Day", 234, "REAL", lyrics: "정답") };
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(GoodDay);

        Assert.Equal("[00:01.00]정답", result?.BestLyrics);
        Assert.Contains("album_name=REAL", server.Requests[0]);
        Assert.Contains("duration=234", server.Requests[0]);
        Assert.Single(server.Requests); // a synced, well-fitting hit needs no search
    }

    [Fact]
    public async Task Skips_a_live_version_of_another_length_for_the_right_one()
    {
        var server = new FakeLrcLib { Get = Track("IU", "Good Day", 358, "Live", lyrics: "라이브") };
        server.Search.Add(Track("IU", "Good Day", 358, "Live", lyrics: "라이브"));
        server.Search.Add(Track("IU", "Good Day", 236.07, "Good Day (Japanese Version)", lyrics: "일본어"));
        server.Search.Add(Track("IU", "Good Day", 234, "Real", lyrics: "원곡"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(GoodDay);

        Assert.Equal("[00:01.00]원곡", result?.BestLyrics);
    }

    [Fact]
    public async Task Prefers_synced_lyrics_among_equally_fitting_versions()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("IU", "Good Day", 234, synced: false, lyrics: "일반"));
        server.Search.Add(Track("IU", "Good Day", 235, lyrics: "싱크"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(GoodDay);

        Assert.Equal("[00:01.00]싱크", result?.BestLyrics);
    }

    [Fact]
    public async Task Finds_nothing_rather_than_another_recording()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("IU", "Good Day", 358, lyrics: "라이브"));
        using var service = new LrcLibLyricsService(server);

        Assert.Null(await service.FindAsync(GoodDay));
    }

    [Fact]
    public async Task Accepts_a_differently_written_artist_when_title_and_length_match()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("(G)I-DLE", "TOMBOY", 174, lyrics: "톰보이"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("(여자)아이들", "TOMBOY", null, TimeSpan.FromSeconds(174)));

        Assert.Equal("[00:01.00]톰보이", result?.BestLyrics);
    }

    [Fact]
    public async Task Without_a_length_still_needs_the_artist_to_match()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("Someone Else", "Good Day", 234, lyrics: "남의 곡"));
        server.Search.Add(Track("IU", "Good Day (Japanese Ver.)", 236, lyrics: "일본어"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("IU", "Good Day"));

        Assert.Equal("[00:01.00]일본어", result?.BestLyrics); // same song once "(…)" is set aside; never the other artist
    }

    [Fact]
    public async Task Prefers_the_original_over_a_marked_remix()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("IU", "Good Day (Remix)", 234, lyrics: "리믹스"));
        server.Search.Add(Track("IU", "Good Day", 233, lyrics: "원곡"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(GoodDay);

        Assert.Equal("[00:01.00]원곡", result?.BestLyrics);
    }
}
