using System.Net;
using System.Text;
using System.Text.Json;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class LrcLibLyricsServiceTests
{
    /// <summary>Stands in for lrclib.net: "get" answers with <see cref="Get"/> (404 when null),
    /// "search" with the <see cref="SearchBy"/> list whose key is in the request (e.g.
    /// "?track_name=막내" for the search by title alone), else with <see cref="Search"/>.
    /// Records the requests made.</summary>
    private sealed class FakeLrcLib : HttpMessageHandler
    {
        public object? Get { get; set; }
        public List<object> Search { get; } = [];
        public Dictionary<string, List<object>> SearchBy { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            var decoded = Uri.UnescapeDataString(path);
            Requests.Add(decoded);
            object? body = path.Contains("/api/get") ? Get
                : SearchBy.FirstOrDefault(s => decoded.EndsWith(s.Key, StringComparison.Ordinal)).Value ?? Search;
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

    // ---- language ----

    // (lines like the ones in the WINNER song: Japanese is kanji with kana, Korean may mix in English)
    private const string Japanese = "君はいるかな? 近くに来たんだ 愛してる hey";
    private const string Korean = "Hey, really really 널 보면 내 심장이 뛰어";
    private const string Romanized = "neol bomyeon nae simjangi ttwieo";

    private static readonly LyricsQuery ReallyReally = new("WINNER", "REALLY REALLY", "FATE NUMBER FOR", TimeSpan.FromSeconds(203));

    [Fact]
    public async Task A_Korean_song_with_English_tags_gets_Korean_lyrics_not_the_Japanese_release()
    {
        // what LRCLIB really answers: the exact lookup hits the Japanese upload (same title, album
        // and length), the search has the Korean ones too
        var server = new FakeLrcLib { Get = Track("WINNER", "Really Really", 204, "FATE NUMBER FOR", lyrics: Japanese) };
        server.Search.Add(Track("WINNER", "REALLY REALLY", 204, "FATE NUMBER FOR -JAPAN VERSION-", lyrics: Korean));
        server.Search.Add(Track("WINNER", "Really Really", 204, "FATE NUMBER FOR", lyrics: Japanese));
        server.Search.Add(Track("WINNER", "REALLY REALLY", 204, "OUR TWENTY FOR", lyrics: Korean));
        server.Search.Add(Track("WINNER", "REALLY REALLY", 203, "Best of K-Pop Music 2018", lyrics: Korean));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(ReallyReally);

        Assert.Equal($"[00:01.00]{Korean}", result?.BestLyrics);
        Assert.True(server.Requests.Count >= 2); // the Japanese exact hit didn't end the search
    }

    [Fact]
    public async Task Hangul_beats_romanized_uploads_of_the_same_recording()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("WINNER", "REALLY REALLY", 204, lyrics: Romanized));
        server.Search.Add(Track("WINNER", "REALLY REALLY", 204, lyrics: Romanized));
        server.Search.Add(Track("WINNER", "REALLY REALLY", 204, lyrics: Korean));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(ReallyReally);

        Assert.Equal($"[00:01.00]{Korean}", result?.BestLyrics);
    }

    [Fact]
    public async Task Finds_Korean_lyrics_under_the_Korean_title_a_romanized_upload_gives()
    {
        // what LRCLIB really answers for "Some" by "소유, 정기고": every search by our title finds
        // only a romanized upload titled "썸 (Some)"; the Korean lyrics are listed under "썸"
        var server = new FakeLrcLib();
        server.Search.Add(Track("소유 & 정기고 feat. 릴보이 of 긱스", "썸 (Some)", 211, "Some", lyrics: Romanized));
        server.SearchBy["?track_name=썸"] =
        [
            Track("NATURE", "썸 (You'll Be Mine)", 217, lyrics: "남의 곡"),
            Track("소유, 정기고", "썸 (Feat. 릴보이 Of 긱스 )", 210, "썸", lyrics: Korean),
        ];
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("소유, 정기고", "Some (Feat. 릴보이 Of 긱스 )", "Some", TimeSpan.FromSeconds(211)));

        Assert.Equal($"[00:01.00]{Korean}", result?.BestLyrics);
    }

    [Fact]
    public async Task Finds_Korean_lyrics_under_the_English_title_a_bilingual_romanized_upload_gives()
    {
        // what LRCLIB really answers for "사랑은 미친짓" by "15&": every search by our title finds
        // only romanized uploads, one titled with both names; the Korean lyrics are listed under
        // the English one
        var server = new FakeLrcLib();
        server.Search.Add(Track("15&", "사랑은 미친짓 (feat. 칸토 of 트로이)", 217, "Love is Madness", synced: false, lyrics: Romanized));
        server.Search.Add(Track("15", "사랑은 미친짓 (feat. 칸토 of 트로이) Love is Madness", 217, synced: false, lyrics: Romanized));
        server.SearchBy["?q=15& Love is Madness"] =
        [
            Track("15&", "Love is Madness (feat. Kanto)", 217, "Love is Madness (feat. Kanto)", lyrics: Korean),
        ];
        using var service = new LrcLibLyricsService(server);
        var query = new LyricsQuery("15&", "사랑은 미친짓 (feat. 칸토 of 트로이)", "사랑은 미친짓", TimeSpan.FromSeconds(217));

        Assert.Equal($"[00:01.00]{Korean}", (await service.FindAsync(query))?.BestLyrics);
        var listed = await service.SearchAsync(query);
        Assert.Equal("한국어", listed.Candidates[0].Language);
    }

    [Fact]
    public async Task An_English_song_still_gets_its_English_lyrics()
    {
        var server = new FakeLrcLib { Get = Track("Coldplay", "Yellow", 266, "Parachutes", lyrics: "Look at the stars") };
        server.Search.Add(Track("Coldplay", "Yellow", 266, "Parachutes", lyrics: "Look at the stars"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("Coldplay", "Yellow", "Parachutes", TimeSpan.FromSeconds(266)));

        Assert.Equal("[00:01.00]Look at the stars", result?.BestLyrics);
    }

    [Fact]
    public async Task A_song_tagged_in_Hangul_never_gets_Japanese_lyrics()
    {
        var server = new FakeLrcLib { Get = Track("아이유", "좋은 날", 234, lyrics: Japanese) };
        server.Search.Add(Track("아이유", "좋은 날", 234, lyrics: Japanese));
        using var service = new LrcLibLyricsService(server);

        Assert.Null(await service.FindAsync(new LyricsQuery("아이유", "좋은 날", null, TimeSpan.FromSeconds(234))));
    }

    [Fact]
    public async Task A_Japanese_version_gets_the_Japanese_lyrics()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("IU", "Good Day (Japanese Ver.)", 236, lyrics: Korean));
        server.Search.Add(Track("IU", "Good Day (Japanese Ver.)", 236, lyrics: Japanese));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("IU", "Good Day (Japanese Ver.)", null, TimeSpan.FromSeconds(236)));

        Assert.Equal($"[00:01.00]{Japanese}", result?.BestLyrics);
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

    // ---- songs listed under other names ----

    private static readonly LyricsQuery Maknae = new("백예린", "막내", "물고기", TimeSpan.FromSeconds(234.4));

    [Fact]
    public async Task Finds_a_song_listed_under_its_English_title_by_artist_album_and_length()
    {
        // what LRCLIB really has: no title search finds "막내", the album search lists the single
        var server = new FakeLrcLib();
        server.SearchBy["?q=백예린 물고기"] =
        [
            Track("백예린", "Pisces", 227, "물고기", lyrics: "난 땅에서도 숨을 쉴 수 있는"),
            Track("백예린", "The loved one", 234, "물고기", lyrics: "우리 집 막내"),
            Track("백예린", "It was me", 201, "물고기", lyrics: "그게 나였어"),
        ];
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(Maknae);

        Assert.Equal("[00:01.00]우리 집 막내", result?.BestLyrics);
        Assert.Contains(server.Requests, r => r.EndsWith("?track_name=막내")); // tried the title alone first
    }

    [Fact]
    public async Task Another_title_in_the_same_script_is_another_song_even_on_the_same_album()
    {
        var server = new FakeLrcLib();
        server.SearchBy["?q=김동률 Monologue"] = [Track("김동률", "그건 말야", 214, "Monologue", lyrics: "남의 곡")];
        using var service = new LrcLibLyricsService(server);

        Assert.Null(await service.FindAsync(new LyricsQuery("김동률", "아이처럼", "Monologue", TimeSpan.FromSeconds(214))));
    }

    [Fact]
    public async Task By_album_needs_the_length_and_just_one_song_of_the_album_fitting_it()
    {
        var server = new FakeLrcLib();
        server.SearchBy["?q=백예린 물고기"] = [Track("백예린", "The loved one", 238, "물고기", lyrics: "막내")];
        using var service = new LrcLibLyricsService(server);
        Assert.Null(await service.FindAsync(Maknae)); // 4 s off

        server.SearchBy["?q=백예린 물고기"] =
        [
            Track("백예린", "The loved one", 234, "물고기", lyrics: "막내"),
            Track("백예린", "Pisces", 235, "물고기", lyrics: "막내 물고기"),
        ];
        Assert.Null(await service.FindAsync(Maknae)); // which one is it?

        server.SearchBy["?q=백예린 물고기"] = [Track("백예린", "The loved one", 234, "Love, Yerin", lyrics: "막내")];
        Assert.Null(await service.FindAsync(Maknae)); // another album
    }

    [Fact]
    public async Task By_album_needs_the_lyrics_to_sing_our_title()
    {
        // what LRCLIB really has: no "왜? 날", but "Antifreeze" of the same album, just as long
        var server = new FakeLrcLib();
        server.SearchBy["?q=백예린 선물"] = [Track("백예린", "Antifreeze", 246, "선물", lyrics: "우린 오래전부터")];
        using var service = new LrcLibLyricsService(server);
        var whyMe = new LyricsQuery("백예린", "왜? 날", "선물", TimeSpan.FromSeconds(246.5));
        Assert.Null(await service.FindAsync(whyMe));

        // the title sung with other spacing and punctuation still counts
        server.SearchBy["?q=백예린 선물"] = [Track("백예린", "Why me", 246, "선물", lyrics: "그런데 왜 날")];
        Assert.NotNull(await service.FindAsync(whyMe));
    }

    [Fact]
    public async Task Finds_a_song_whose_title_holds_ours_in_brackets()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("Crush", "Ego (미워 (Ego))", 175, "wonderego", lyrics: "미워"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("Crush", "미워", null, TimeSpan.FromSeconds(175)));

        Assert.Equal("[00:01.00]미워", result?.BestLyrics);
        Assert.Null(await service.FindAsync(new LyricsQuery("Crush", "미워", null, TimeSpan.FromSeconds(190)))); // another recording
        Assert.Null(await service.FindAsync(new LyricsQuery("Crush", "미워"))); // no length to tell
    }

    [Fact]
    public async Task Finds_a_song_whose_title_is_written_in_both_languages()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("Yerin Baek", "우주를 건너 Across the universe", 247, "FRANK", lyrics: "우주를 건너"));
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("백예린 (Yerin Baek)", "우주를 건너", "FRANK", TimeSpan.FromSeconds(247)));

        Assert.Equal("[00:01.00]우주를 건너", result?.BestLyrics);
    }

    [Fact]
    public async Task Searches_by_title_alone_when_the_artist_is_written_otherwise()
    {
        var server = new FakeLrcLib();
        server.SearchBy["?track_name=이 별로부터"] = [Track("아이유", "이 별로부터", 225, lyrics: "평범한 날", synced: false)];
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("아이유(IU)", "이 별로부터", "이 별로부터", TimeSpan.FromSeconds(225)));

        Assert.Equal("평범한 날", result?.BestLyrics);
    }

    [Fact]
    public async Task By_title_alone_never_takes_another_artists_song_of_the_same_length()
    {
        var server = new FakeLrcLib();
        server.SearchBy["?track_name=좋은 사람"] =
        [
            Track("T-ARA", "좋은 사람", 212, "Absolute First Album", lyrics: "티아라"),
            Track("Park Hyo Shin", "좋은 사람", 212, lyrics: "박효신"),
        ];
        using var service = new LrcLibLyricsService(server);

        Assert.Null(await service.FindAsync(new LyricsQuery("폴킴", "좋은 사람", "길", TimeSpan.FromSeconds(212))));
    }

    [Fact]
    public async Task By_title_alone_takes_the_artists_romanized_name_but_no_other()
    {
        var server = new FakeLrcLib();
        server.SearchBy["?track_name=나의 X에게"] = [Track("KyoungSeo", "Dear my X (나의 X에게)", 188, lyrics: "경서")];
        using var service = new LrcLibLyricsService(server);

        var result = await service.FindAsync(new LyricsQuery("경서", "나의 X에게", null, TimeSpan.FromSeconds(188.4)));

        Assert.Equal("[00:01.00]경서", result?.BestLyrics);

        server.SearchBy["?track_name=나의 X에게"] = [Track("Kim Seo", "나의 X에게", 188, lyrics: "남의 곡")];
        Assert.Null(await service.FindAsync(new LyricsQuery("경서", "나의 X에게", null, TimeSpan.FromSeconds(188.4))));
    }

    [Theory]
    [InlineData("폴킴", "Paul Kim")]
    [InlineData("민니", "MINNIE")]
    [InlineData("휘인", "Whee In")]
    [InlineData("김민석 (멜로망스)", "Kim Min-seok (MeloMance)")]
    public async Task A_romanized_artist_name_counts_as_the_same_artist(string ours, string theirs)
    {
        var server = new FakeLrcLib();
        server.SearchBy["?track_name=노래"] = [Track(theirs, "노래", 200, lyrics: "맞음")];
        using var service = new LrcLibLyricsService(server);

        Assert.NotNull(await service.FindAsync(new LyricsQuery(ours, "노래", null, TimeSpan.FromSeconds(200))));
    }

    [Fact]
    public async Task A_credit_in_brackets_is_not_a_title()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("ZICO", "feat. Jtong", 211, lyrics: "남의 곡"));
        server.Search.Add(Track("ZICO", "Live", 211, lyrics: "라이브"));
        using var service = new LrcLibLyricsService(server);

        Assert.Null(await service.FindAsync(new LyricsQuery("ZICO", "날 (feat. Jtong) (Live)", null, TimeSpan.FromSeconds(211))));
    }

    // ---- every fitting upload, to choose from (가사 찾기's list) ----

    [Fact]
    public async Task Lists_every_fitting_upload_best_first_without_stopping_at_the_exact_hit()
    {
        var server = new FakeLrcLib { Get = Track("IU", "Good Day", 234, "REAL", lyrics: "정답") };
        server.Search.Add(Track("IU", "Good Day", 234, "REAL", synced: false, lyrics: "일반 가사"));
        server.Search.Add(Track("IU", "Good Day", 235, "REAL", lyrics: "다른 싱크"));
        server.Search.Add(Track("IU", "Good Day", 358, "Live", lyrics: "라이브")); // another recording: not offered
        using var service = new LrcLibLyricsService(server);

        var result = await service.SearchAsync(GoodDay);

        Assert.True(result.Certain);
        Assert.Equal(["[00:01.00]정답", "[00:01.00]다른 싱크", "일반 가사"], result.Candidates.Select(c => c.Lyrics));
        Assert.All(result.Candidates, c => Assert.Equal("LRCLIB", c.Source));
        Assert.Equal([true, true, false], result.Candidates.Select(c => c.IsSynced));
        Assert.True(result.Candidates[0].LengthMatches);
        Assert.True(result.Candidates[0].AlbumMatches);
        Assert.Equal("한국어", result.Candidates[0].Language);
        Assert.True(server.Requests.Count > 1); // searched on after the exact lookup
    }

    [Fact]
    public async Task The_same_lyrics_uploaded_twice_are_listed_once()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("IU", "Good Day", 234, "REAL", lyrics: "같은 가사"));
        server.Search.Add(Track("IU", "Good Day", 234.5, "REAL", lyrics: "같은 가사"));
        using var service = new LrcLibLyricsService(server);

        Assert.Single((await service.SearchAsync(GoodDay)).Candidates);
    }

    [Fact]
    public async Task The_list_starts_with_what_FindAsync_takes()
    {
        var server = new FakeLrcLib();
        server.Search.Add(Track("IU", "Good Day", 358, "Live", lyrics: "라이브"));
        server.Search.Add(Track("IU", "Good Day", 236.07, "Good Day (Japanese Version)", lyrics: "일본어"));
        server.Search.Add(Track("IU", "Good Day", 234, "Real", lyrics: "원곡"));
        using var service = new LrcLibLyricsService(server);

        var found = await service.FindAsync(GoodDay);
        var listed = await service.SearchAsync(GoodDay);

        Assert.Equal(found?.BestLyrics, listed.Candidates[0].Lyrics);
    }
}
