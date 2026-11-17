using System.Text.Json.Nodes;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.CoverSearch;

namespace TuneSync.Core.Tests;

public class CoverRankingTests
{
    private static CoverCandidate C(string title, string artist, string album, CoverReleaseKind kind = CoverReleaseKind.Album,
        int size = 1000, string source = "iTunes", TimeSpan? duration = null, string? key = null) => new()
    {
        Source = source,
        Title = title,
        Artist = artist,
        Album = album,
        Kind = kind,
        MaxSize = size,
        Duration = duration,
        ThumbnailUrl = $"https://example.test/{source}/{album}/{title}/{kind}/{size}",
        FullUrls = [],
        Key = key,
    };

    [Fact]
    public void Rank_StudioAlbumBeatsLiveAndCompilation()
    {
        var query = new CoverQuery("Oasis", "Wonderwall");
        var ranked = CoverRanking.Rank(query,
        [
            C("Wonderwall (Live at Wembley)", "Oasis", "Familiar to Millions", CoverReleaseKind.Live),
            C("Wonderwall", "Oasis", "Now That's What I Call 90s", CoverReleaseKind.Compilation),
            C("Wonderwall", "Oasis", "(What's the Story) Morning Glory?"),
        ]);

        Assert.Equal("(What's the Story) Morning Glory?", ranked[0].Album);
        Assert.True(ranked[0].Score > 0.9);
        Assert.DoesNotContain(ranked.Take(2), c => c.Kind == CoverReleaseKind.Live);
    }

    [Fact]
    public void Rank_LiveVersionIsFineWhenTheSongIsLive()
    {
        var query = new CoverQuery("Oasis", "Wonderwall (Live)");
        var ranked = CoverRanking.Rank(query,
        [
            C("Wonderwall (Live)", "Oasis", "Familiar to Millions (Live)", CoverReleaseKind.Live),
            C("Wonderwall", "Oasis", "Now 90s", CoverReleaseKind.Compilation),
        ]);
        Assert.Equal("Familiar to Millions (Live)", ranked[0].Album);
    }

    [Fact]
    public void Rank_TheFilesAlbumTagPicksItsRelease()
    {
        var query = new CoverQuery("NMIXX", "Love Me Like This", Album: "expérgo");
        var ranked = CoverRanking.Rank(query,
        [
            C("Love Me Like This", "NMIXX", "Love Me Like This - Single", CoverReleaseKind.Single),
            C("Love Me Like This", "NMIXX", "expérgo - EP", CoverReleaseKind.EP),
        ]);
        Assert.Equal("expérgo - EP", ranked[0].Album);
    }

    [Fact]
    public void Rank_DropsOtherSongsAndOtherArtists()
    {
        var query = new CoverQuery("Oasis", "Wonderwall");
        var ranked = CoverRanking.Rank(query,
        [
            C("Yoga Music", "Relaxing Sounds", "Yoga"),
            C("Wonderwall", "Ryan Adams", "Love Is Hell"),
            C("Wonderwall", "Oasis", "(What's the Story) Morning Glory?"),
        ]);
        Assert.Equal("Oasis", Assert.Single(ranked, c => c.Score >= 0.7).Artist);
        Assert.DoesNotContain(ranked, c => c.Title == "Yoga Music");
        // A cover of the same title by someone else may be shown, but well below the original.
        Assert.All(ranked.Where(c => c.Artist != "Oasis"), c => Assert.True(c.Score < ranked[0].Score - 0.2));
    }

    [Fact]
    public void Rank_SameAlbumFromSeveralSourcesIsShownOnce_TheBestEntryKept()
    {
        var query = new CoverQuery("Oasis", "Wonderwall");
        var ranked = CoverRanking.Rank(query,
        [
            C("Wonderwall", "Oasis", "(What's the Story) Morning Glory?", size: 500, source: "Deezer"),
            C("Wonderwall", "Oasis", "(What's The Story) Morning Glory?", size: 1200, source: "iTunes"),
        ]);
        var only = Assert.Single(ranked);
        Assert.Equal("iTunes", only.Source);
    }

    [Fact]
    public void Rank_SameReleaseGroupKeyIsShownOnce()
    {
        var query = new CoverQuery("Panic", "달팽이");
        var ranked = CoverRanking.Rank(query,
        [
            C("달팽이", "패닉", "Panic", source: "MusicBrainz", key: "mb:1"),
            C("달팽이", "Panic", "Panic (Remastered)", source: "MusicBrainz", key: "mb:1"),
        ]);
        Assert.Single(ranked);
    }

    [Fact]
    public void Rank_BiggerImageWinsBetweenEqualMatches()
    {
        var query = new CoverQuery("P!nk", "TRUSTFALL");
        var ranked = CoverRanking.Rank(query,
        [
            C("TRUSTFALL", "P!nk", "TRUSTFALL", size: 500, source: "Deezer", key: "a"),
            C("TRUSTFALL", "P!nk", "TRUSTFALL (Deluxe)", size: 1200, source: "iTunes", key: "b"),
        ]);
        Assert.Equal(1200, ranked[0].MaxSize);
    }

    [Fact]
    public void Rank_MatchingLengthTellsVersionsApart()
    {
        var query = new CoverQuery("Oasis", "Wonderwall", Duration: TimeSpan.FromSeconds(258));
        var ranked = CoverRanking.Rank(query,
        [
            C("Wonderwall", "Oasis", "Radio Edit Hits", duration: TimeSpan.FromSeconds(200), key: "x"),
            C("Wonderwall", "Oasis", "Morning Glory", duration: TimeSpan.FromSeconds(259), key: "y"),
        ]);
        Assert.Equal("Morning Glory", ranked[0].Album);
    }

    [Fact]
    public void Rank_KeepsAtMostTheRequestedCount()
    {
        var query = new CoverQuery("A", "Song");
        var many = Enumerable.Range(0, 30).Select(i => C("Song", "A", $"Album {i}", key: i.ToString()));
        Assert.Equal(12, CoverRanking.Rank(query, many).Count);
    }

    [Theory]
    [InlineData("Love Me Like This!", "love me like this", 1.0)]
    [InlineData("ＴＲＵＳＴＦＡＬＬ", "TRUSTFALL", 1.0)]
    [InlineData("Wonderwall (Remastered)", "Wonderwall", 0.97)]
    [InlineData("Summer (Feat. BE'O)", "Summer", 0.97)]
    public void TitleSimilarity_IgnoresCaseWidthPunctuationAndExtras(string a, string b, double atLeast) =>
        Assert.True(CoverRanking.TitleSimilarity(a, b) >= atLeast - 1e-9, $"{a} vs {b}: {CoverRanking.TitleSimilarity(a, b)}");

    [Fact]
    public void TitleSimilarity_DifferentSongsScoreLow()
    {
        Assert.True(CoverRanking.TitleSimilarity("Wonderwall", "Don't Look Back in Anger") < 0.3);
        // The same "(Remastered)" on both doesn't make them alike.
        Assert.True(CoverRanking.TitleSimilarity("Wonderwall (Remastered)", "Live Forever (Remastered)") < 0.3);
    }

    [Theory]
    [InlineData("Paul Blanco, Heize", "Paul Blanco & Heize")]
    [InlineData("Paul Blanco, Heize", "Paul Blanco")]
    public void ArtistSimilarity_MatchesCreditsListedDifferently(string a, string b) =>
        Assert.True(CoverRanking.ArtistSimilarity(a, b) >= 0.9);
}

public class CoverSourceParsingTests
{
    [Fact]
    public void ITunes_RewritesArtworkSizes_AndReadsSingles()
    {
        var json = JsonNode.Parse("""
            {"results":[
              {"trackName":"Love Me Like This","artistName":"NMIXX","collectionName":"Love Me Like This - Single",
               "collectionId":42,"trackCount":1,"releaseDate":"2023-03-20T12:00:00Z","trackTimeMillis":190000,
               "artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/a/b.jpg/100x100bb.jpg"},
              {"trackName":"No art","artistName":"X"}
            ]}
            """);
        var c = Assert.Single(ITunesCoverSource.Parse(json));
        Assert.Equal(CoverReleaseKind.Single, c.Kind);
        Assert.Equal(2023, c.Year);
        Assert.Equal(TimeSpan.FromSeconds(190), c.Duration);
        Assert.EndsWith("/300x300bb.jpg", c.ThumbnailUrl);
        Assert.EndsWith("/1200x1200bb.jpg", c.FullUrls[0]);
        Assert.Equal("it:42", c.Key);
    }

    [Fact]
    public void Deezer_UsesTheLargestCover()
    {
        var json = JsonNode.Parse("""
            {"data":[{"title":"Wonderwall","duration":258,"artist":{"name":"Oasis"},
              "album":{"id":7,"title":"Morning Glory","cover_medium":"https://x/250.jpg","cover_big":"https://x/500.jpg","cover_xl":"https://x/1000.jpg"}}]}
            """);
        var c = Assert.Single(DeezerCoverSource.Parse(json));
        Assert.Equal(["https://x/1000.jpg", "https://x/500.jpg"], c.FullUrls);
        Assert.Equal(1000, c.MaxSize);
        Assert.Equal("https://x/250.jpg", c.ThumbnailUrl);
    }

    [Fact]
    public void MusicBrainz_OneCandidatePerReleaseGroup_WithKinds()
    {
        var json = JsonNode.Parse("""
            [{"score":100,"title":"Wonderwall","length":258000,"artist-credit":[{"name":"Oasis"}],
              "releases":[
                {"id":"r1","title":"Morning Glory","status":"Official","date":"1995-10-02",
                 "release-group":{"id":"g1","primary-type":"Album"}},
                {"id":"r2","title":"Morning Glory (JP)","status":"Official",
                 "release-group":{"id":"g1","primary-type":"Album"}},
                {"id":"r3","title":"Familiar to Millions","status":"Official",
                 "release-group":{"id":"g2","primary-type":"Album","secondary-types":["Live"]}},
                {"id":"r4","title":"Unplugged","status":"Bootleg","release-group":{"id":"g3","primary-type":"Album"}}
              ]},
             {"score":30,"title":"Something else","artist-credit":[{"name":"Y"}],
              "releases":[{"id":"r9","title":"Z","release-group":{"id":"g9"}}]}]
            """)!.AsArray();
        var list = MusicBrainzCoverSource.Parse(json);
        Assert.Equal(["g1", "g2", "g3"], list.Select(c => c.Key![3..]));
        Assert.Equal([CoverReleaseKind.Album, CoverReleaseKind.Live, CoverReleaseKind.Unofficial], list.Select(c => c.Kind));
        Assert.Equal(1995, list[0].Year);
        Assert.Equal("https://coverartarchive.org/release-group/g1/front-250", list[0].ThumbnailUrl);
    }
}

public class CoverSearchServiceTests
{
    private sealed class FakeSource(string name, Func<IReadOnlyList<CoverCandidate>> result) : ICoverSource
    {
        public string Name => name;
        public Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverQuery query, CancellationToken ct) => Task.FromResult(result());
    }

    private static CoverCandidate Hit(string album) => new()
    {
        Source = "Fake", Title = "Song", Artist = "Band", Album = album, Kind = CoverReleaseKind.Album,
        ThumbnailUrl = "t/" + album, FullUrls = [], MaxSize = 1000,
    };

    [Fact]
    public async Task SearchAsync_ReportsFailedSources_AndRanksTheRest()
    {
        using var http = new HttpClient();
        var service = new CoverSearchService(
        [
            new FakeSource("A", () => [Hit("Second Album"), Hit("Song")]),
            new FakeSource("B", () => throw new HttpRequestException("down")),
        ], http);
        var reports = new List<CoverSearchProgress>();
        var result = await service.SearchAsync(new CoverQuery("Band", "Song"), new SyncProgress<CoverSearchProgress>(reports.Add));

        Assert.Equal(["B"], result.FailedSources);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(2, r.Total));
    }

    [Fact]
    public async Task SearchAsync_Throws_WhenEverySourceFails()
    {
        using var http = new HttpClient();
        var service = new CoverSearchService([new FakeSource("A", () => throw new HttpRequestException("오프라인"))], http);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync(new CoverQuery("Band", "Song")));
        Assert.Equal("오프라인", ex.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, true)]
    [InlineData(new byte[] { (byte)'<', (byte)'h', (byte)'t', (byte)'m', (byte)'l', 0, 0, 0, 0 }, false)]
    public void IsImage_AcceptsJpegAndPngOnly(byte[] bytes, bool expected) =>
        Assert.Equal(expected, CoverSearchService.IsImage(bytes));

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class AutoFillCoverSearchTests
{
    private sealed class FakeCoverSearch(params (double Score, byte[]? Bytes)[] hits) : ICoverSearchService
    {
        public List<double> Downloaded { get; } = [];

        public Task<CoverSearchResult> SearchAsync(CoverQuery query, IProgress<CoverSearchProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(new CoverSearchResult(hits.Select((h, i) => new CoverCandidate
            {
                Source = "Fake", Title = query.Title, Artist = query.Artist, ThumbnailUrl = i.ToString(), FullUrls = [i.ToString()], Score = h.Score,
            }).ToList(), []));

        public Task<byte[]?> GetThumbnailAsync(CoverCandidate candidate, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

        public Task<byte[]?> GetFullAsync(CoverCandidate candidate, CancellationToken ct = default)
        {
            Downloaded.Add(candidate.Score);
            return Task.FromResult(hits[int.Parse(candidate.FullUrls[0])].Bytes);
        }
    }

    private static LibraryFile File() => new()
    {
        Path = "x.mp3",
        Tags = new TrackTags { Path = "x.mp3", Artist = "IU", Title = "Good Day" },
    };

    [Fact]
    public async Task CoverOnly_UsesTheBestCandidateThatDownloads()
    {
        var covers = new FakeCoverSearch((0.95, null), (0.9, [1, 2]), (0.85, [3]));
        var mb = new FakeMusicBrainzService();
        var service = new AutoFillService(mb, new FakeLyricsService(), covers);

        var p = Assert.Single(await service.BuildProposalsAsync([File()], options: new AutoFillOptions(Tags: false, Cover: true, Lyrics: false)));

        Assert.Equal(new byte[] { 1, 2 }, p.ProposedCoverBytes);
        Assert.Empty(mb.Calls); // the cover search asks MusicBrainz itself
    }

    [Fact]
    public async Task CoverOnly_LeavesWeakMatchesAlone()
    {
        var covers = new FakeCoverSearch((0.6, [1]));
        var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService(), covers);

        var p = Assert.Single(await service.BuildProposalsAsync([File()], options: new AutoFillOptions(Tags: false, Cover: true, Lyrics: false)));

        Assert.Null(p.ProposedCoverBytes);
        Assert.Empty(covers.Downloaded);
        Assert.Equal("찾지 못함", p.Status);
    }
}
