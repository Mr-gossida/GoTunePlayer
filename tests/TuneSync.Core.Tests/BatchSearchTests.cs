using System.Diagnostics;
using System.Net;
using System.Text;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.Core.Tests;

/// <summary>Searching many songs at once: the public APIs' limits, the shared iTunes search, and
/// the multi-source auto-fill keeping every result with where it's from.</summary>
public class BatchSearchTests
{
    /// <summary>Answers each URL with what <paramref name="answer"/> says, counting the requests.</summary>
    private sealed class Api(Func<string, string> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Asked) Asked.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(url), Encoding.UTF8, "application/json") });
        }
    }

    // ---- the public APIs' limits ----

    [Fact]
    public async Task A_call_past_the_limit_waits_for_the_window()
    {
        var gate = new ApiGate(2, TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();
        await gate.WaitTurnAsync(CancellationToken.None);
        await gate.WaitTurnAsync(CancellationToken.None);
        Assert.True(clock.ElapsedMilliseconds < 200); // two fit at once

        await gate.WaitTurnAsync(CancellationToken.None);
        Assert.True(clock.ElapsedMilliseconds >= 390, $"the third went after {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task The_same_search_twice_is_asked_once()
    {
        var api = new Api(_ => """{"data":[]}""");
        var gate = new ApiGate(10, TimeSpan.FromSeconds(1));
        using var http = new HttpClient(api);

        await gate.GetStringAsync(http, "https://api.deezer.com/search?q=a", CancellationToken.None);
        await gate.GetStringAsync(http, "https://api.deezer.com/search?q=a", CancellationToken.None);
        await gate.GetStringAsync(http, "https://api.deezer.com/search?q=b", CancellationToken.None);

        Assert.Equal(2, api.Asked.Count);
    }

    // ---- iTunes: the US store, the Japanese one only when needed ----

    private const string Found = """{"results":[{"trackName":"밤편지","artistName":"IU","collectionName":"Palette","trackTimeMillis":253000}]}""";
    private const string Nothing = """{"results":[]}""";

    [Fact]
    public async Task ITunes_asks_the_japanese_store_only_when_the_us_one_lacks_the_title()
    {
        var api = new Api(url => url.Contains("country=US") && url.Contains("%EB%B0%A4%ED%8E%B8%EC%A7%80") ? Found : Nothing);
        using var http = new HttpClient(api);
        var source = new ITunesTagSource(http, new ApiGate(100, TimeSpan.FromSeconds(1)));

        var found = await source.SearchAsync(new TagQuery("아이유", "밤편지"), CancellationToken.None);
        Assert.Single(found);
        Assert.Single(api.Asked); // the US store had it

        await source.SearchAsync(new TagQuery("YOASOBI", "夜に駆ける"), CancellationToken.None);
        Assert.Equal(3, api.Asked.Count); // not in the US store: the Japanese one too
        Assert.Contains("country=JP", api.Asked[2]);
    }

    // ---- what a result changes ----

    private static TagCandidate Candidate(string source, double score, string album = "Palette", uint? year = 2017, string genre = "K-Pop",
        uint? track = 3, string artist = "아이유", double seconds = 253) => new()
    {
        Source = source,
        Title = "밤편지",
        Artist = artist,
        Album = album,
        AlbumArtist = artist,
        Year = year,
        Genre = genre,
        Track = track,
        Duration = TimeSpan.FromSeconds(seconds),
        Score = score,
    };

    [Fact]
    public void A_result_fills_empty_fields_and_replaces_only_when_asked_the_genre_never()
    {
        var tags = new TrackTags { Path = "x.mp3", Title = "밤편지", Artist = "아이유", Album = "palette", Genre = "발라드", Track = 5 };
        var c = Candidate("iTunes", 0.9);

        var fill = TagChanges.Of(c, f => TagFields.Get(tags, f), replace: false);
        Assert.Equal([TagField.AlbumArtist, TagField.Year], fill.Select(x => x.Field));

        var replace = TagChanges.Of(c, f => TagFields.Get(tags, f), replace: true);
        // the album differs in capitals only; the genre is the user's
        Assert.Equal([TagField.AlbumArtist, TagField.Year, TagField.Track], replace.Select(x => x.Field));
        Assert.Equal("3", replace[2].Value);

        // spacing alone isn't another title
        var spaced = new TrackTags { Path = "x.mp3", Title = "ゆびきり -日本語version-", Artist = "ユンナ" };
        var store = new TagCandidate { Source = "iTunes", Title = "ゆびきり-日本語version-", Artist = "ユンナ" };
        Assert.Empty(TagChanges.Of(store, f => TagFields.Get(spaced, f), replace: true));
    }

    // ---- several songs at once, from several sources ----

    private sealed class Source(string name, params TagCandidate[] answer) : ITagSource
    {
        public string Name => name;
        public int Asked { get; private set; }

        public Task<IReadOnlyList<TagCandidate>> SearchAsync(TagQuery query, CancellationToken ct)
        {
            Asked++;
            // fresh copies: the ranking scores them
            return Task.FromResult<IReadOnlyList<TagCandidate>>([.. answer.Select(a => new TagCandidate
            {
                Source = a.Source, Title = a.Title, Artist = a.Artist, Album = a.Album, AlbumArtist = a.AlbumArtist,
                Year = a.Year, Genre = a.Genre, Track = a.Track, Duration = a.Duration,
            })]);
        }
    }

    private static LibraryFile Song(string album = "") => new()
    {
        Path = "x.mp3",
        Tags = new TrackTags { Path = "x.mp3", Title = "밤편지", Artist = "아이유", Album = album },
        Duration = TimeSpan.FromSeconds(253),
    };

    [Fact]
    public async Task The_best_result_is_proposed_and_every_one_kept_with_its_source()
    {
        var mb = new Source("MusicBrainz", Candidate("MusicBrainz", 0, genre: ""));
        // another release, 17 s longer: it fits less well
        var itunes = new Source("iTunes", Candidate("iTunes", 0, album: "밤편지 - Single", track: 1, seconds: 270));
        var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService(), tagSearch: new TagSearchService([mb, itunes]));

        var p = Assert.Single(await service.BuildProposalsAsync([Song()], options: new AutoFillOptions(Tags: true, Cover: false, Lyrics: false)));

        Assert.Equal(2, p.TagCandidates.Count);
        Assert.Equal("MusicBrainz", p.ChosenTags!.Source);
        Assert.Equal(("Palette", 2017u, 3u), (p.ProposedAlbum, p.ProposedYear!.Value, p.ProposedTrack!.Value));
        Assert.Null(p.ProposedGenre); // MusicBrainz has none
        Assert.False(p.NeedsChoice);

        // another result picked: its values in place of the first's
        p.UseTags(p.TagCandidates.Single(c => c.Source == "iTunes"));
        Assert.Equal(("밤편지 - Single", "K-Pop", 1u), (p.ProposedAlbum, p.ProposedGenre, p.ProposedTrack!.Value));
    }

    [Fact]
    public async Task A_result_that_doesnt_clearly_fit_waits_for_the_user()
    {
        // the title matches, the artist is written otherwise: shown, not put in by itself
        var itunes = new Source("iTunes", Candidate("iTunes", 0, artist: "IU"));
        var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService(), tagSearch: new TagSearchService([itunes]));

        var p = Assert.Single(await service.BuildProposalsAsync([Song()], options: new AutoFillOptions(Tags: true, Cover: false, Lyrics: false)));

        Assert.Single(p.TagCandidates);
        Assert.Null(p.ChosenTags);
        Assert.False(p.HasAnyProposal);
        Assert.True(p.NeedsChoice);
        Assert.Equal("확실한 결과 없음", p.Status);
    }

    [Fact]
    public async Task A_switched_off_source_isnt_asked_for_any_song()
    {
        var mb = new Source("MusicBrainz", Candidate("MusicBrainz", 0));
        var deezer = new Source("Deezer", Candidate("Deezer", 0));
        var service = new AutoFillService(new FakeMusicBrainzService(), new FakeLyricsService(), tagSearch: new TagSearchService([mb, deezer]));

        await service.BuildProposalsAsync([Song(), Song()], options: new AutoFillOptions(Tags: true, Cover: false, Lyrics: false)
        {
            DisabledTagSources = ["Deezer"],
        });

        Assert.Equal((2, 0), (mb.Asked, deezer.Asked));
    }

    // ---- lyrics: beside the song and online ----

    private sealed class Lyrics(LyricsSearchResult answer) : ILyricsService
    {
        public int Asked { get; private set; }

        public Task<LyricsResult?> FindAsync(LyricsQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LyricsSearchResult> SearchAsync(LyricsQuery query, CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult(answer);
        }
    }

    private static LyricsCandidate Online(string text, bool synced = true) => new()
    {
        Source = "LRCLIB", Title = "밤편지", Artist = "아이유", Lyrics = text, IsSynced = synced,
    };

    [Fact]
    public async Task Lyrics_that_dont_clearly_fit_are_kept_but_not_put_in()
    {
        var lyrics = new Lyrics(new LyricsSearchResult([Online("[00:01.00]하나"), Online("[00:01.00]둘")], Certain: false));
        var service = new AutoFillService(new FakeMusicBrainzService(), lyrics, tagSearch: new TagSearchService([]));

        var p = Assert.Single(await service.BuildProposalsAsync([Song()], options: new AutoFillOptions(Tags: false, Cover: false, Lyrics: true)));

        Assert.Equal(2, p.LyricsCandidates.Count);
        Assert.Null(p.ProposedLyrics);
        Assert.True(p.NeedsChoice);

        p.UseLyrics(p.LyricsCandidates[1]);
        Assert.Equal("[00:01.00]둘", p.ProposedLyrics);
        Assert.False(p.NeedsChoice);
    }

    [Fact]
    public async Task Synced_lyrics_beside_the_song_come_first_and_a_switched_off_lrclib_isnt_asked()
    {
        var folder = Directory.CreateTempSubdirectory("gotune-batch");
        try
        {
            var path = Path.Combine(folder.FullName, "밤편지.mp3");
            File.WriteAllBytes(path, [0]);
            File.WriteAllText(Path.Combine(folder.FullName, "밤편지.lrc"), "[00:01.00]이 밤 그날의 반딧불을");
            var song = new LibraryFile { Path = path, Tags = new TrackTags { Path = path, Title = "밤편지", Artist = "아이유" } };
            var lyrics = new Lyrics(new LyricsSearchResult([Online("[00:01.00]온라인")]));
            var service = new AutoFillService(new FakeMusicBrainzService(), lyrics, tagSearch: new TagSearchService([]));

            var both = Assert.Single(await service.BuildProposalsAsync([song], options: new AutoFillOptions(Tags: false, Cover: false, Lyrics: true)));
            Assert.Equal("폴더 파일", both.ChosenLyrics!.Source);
            Assert.Equal(2, both.LyricsCandidates.Count);

            var local = Assert.Single(await service.BuildProposalsAsync([song], options: new AutoFillOptions(Tags: false, Cover: false, Lyrics: true)
            {
                DisabledLyricsSources = ["LRCLIB"],
            }));
            Assert.Single(local.LyricsCandidates);
            Assert.Equal(1, lyrics.Asked); // only the first search asked online
        }
        finally
        {
            folder.Delete(true);
        }
    }
}
