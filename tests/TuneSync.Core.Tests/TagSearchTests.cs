using System.Text;
using System.Text.Json.Nodes;
using TuneSync.Core.Services;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.Core.Tests;

public class TagSearchTests
{
    private static readonly TagQuery Song = new("아이유", "밤편지", "Palette", TimeSpan.FromSeconds(253));

    private static TagCandidate Candidate(string source, string title, string artist, string album = "", uint? year = null,
        double? seconds = null, uint? track = null) => new()
    {
        Source = source,
        Title = title,
        Artist = artist,
        Album = album,
        Year = year,
        Track = track,
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };

    [Fact]
    public void The_song_ranks_above_another_recording_and_another_song_is_dropped()
    {
        var ranked = TagRanking.Rank(Song,
        [
            Candidate("iTunes", "밤편지 (Live)", "아이유", "Live Album", 2019, 290),
            Candidate("MusicBrainz", "밤편지", "아이유", "Palette", 2017, 253, 3),
            Candidate("Deezer", "밤양갱", "비비", "밤양갱", 2024, 146), // another song
        ]);

        Assert.Equal(["MusicBrainz", "iTunes"], ranked.Select(c => c.Source));
        Assert.True(ranked[0].Score > ranked[1].Score);
        Assert.True(ranked[0].Score is > 0.9 and <= 1);
    }

    [Fact]
    public void An_exact_title_may_have_its_artist_written_otherwise()
    {
        var ranked = TagRanking.Rank(Song, [Candidate("iTunes", "밤편지", "IU", "밤편지", 2017, 253)]);
        Assert.Single(ranked);
    }

    [Fact]
    public void A_source_listing_the_same_release_twice_counts_once()
    {
        var ranked = TagRanking.Rank(Song,
        [
            Candidate("iTunes", "밤편지", "아이유", "Palette", 2017, 253, 3),
            Candidate("iTunes", "밤편지", "아이유", "Palette", 2017, 253, 3), // the JP store's copy
            Candidate("Deezer", "밤편지", "아이유", "Palette", 2017, 253, 3),
        ]);
        Assert.Equal(["iTunes", "Deezer"], ranked.Select(c => c.Source));
    }

    [Fact]
    public void ITunes_gives_album_without_the_stores_suffix_and_every_number()
    {
        var json = JsonNode.Parse("""
            {"results":[{"trackName":"밤편지","artistName":"IU","collectionName":"밤편지 - Single","collectionArtistName":"IU",
              "releaseDate":"2017-03-24T12:00:00Z","primaryGenreName":"K-Pop","trackNumber":1,"trackCount":1,"discNumber":1,"trackTimeMillis":253000}]}
            """);
        var c = Assert.Single(ITunesTagSource.Parse(json));
        Assert.Equal(("밤편지", "IU", "IU", "K-Pop"), (c.Album, c.Artist, c.AlbumArtist, c.Genre));
        Assert.Equal((2017u, 1u, 1u, 1u), (c.Year!.Value, c.Track!.Value, c.TrackCount!.Value, c.Disc!.Value));
        Assert.Equal(TimeSpan.FromSeconds(253), c.Duration);
    }

    [Fact]
    public void MusicBrainz_puts_the_studio_album_first_with_the_whole_credit()
    {
        var json = JsonNode.Parse("""
            [{"score":100,"title":"Love Me","length":200000,
              "artist-credit":[{"name":"Paul Blanco","joinphrase":" & "},{"name":"Heize"}],
              "releases":[
                {"title":"Love Me (Remixes)","status":"Official","date":"2021-05-01",
                 "release-group":{"primary-type":"Single","secondary-types":["Remix"]},"media":[{"position":1,"track-count":4,"track":[{"number":"2"}]}]},
                {"title":"Single","status":"Official","date":"2020-01-10",
                 "release-group":{"primary-type":"Single"},"media":[{"position":1,"track-count":1,"track":[{"number":"1"}]}]},
                {"title":"Album","status":"Official","date":"2020-06-01",
                 "release-group":{"primary-type":"Album"},"media":[{"position":2,"track-count":12,"track":[{"number":"7"}]}]}
              ]}]
            """)!.AsArray();
        var list = MusicBrainzTagSource.Parse(json);

        Assert.Equal(["Album", "Single"], list.Select(c => c.Album)); // a remix single isn't the song's
        var album = list[0];
        Assert.Equal("Paul Blanco & Heize", album.Artist);
        Assert.Equal((2020u, 7u, 12u, 2u), (album.Year!.Value, album.Track!.Value, album.TrackCount!.Value, album.Disc!.Value));
    }

    [Fact]
    public void Deezer_takes_numbers_year_and_genre_from_the_track_and_album_pages()
    {
        var result = JsonNode.Parse("""{"id":1,"title":"밤편지","duration":253,"artist":{"name":"IU"},"album":{"id":2,"title":"Palette"}}""")!;
        var track = JsonNode.Parse("""{"track_position":3,"disk_number":1,"release_date":"2017-04-21"}""");
        var album = JsonNode.Parse("""{"nb_tracks":10,"artist":{"name":"IU"},"genres":{"data":[{"name":"Pop"}]}}""");

        var c = DeezerTagSource.Build(result, track, album);
        Assert.Equal(("Palette", "Pop", 2017u, 3u, 10u, 1u), (c.Album, c.Genre, c.Year!.Value, c.Track!.Value, c.TrackCount!.Value, c.Disc!.Value));

        // without the pages, the search's own fields still make a candidate
        var bare = DeezerTagSource.Build(result, null, null);
        Assert.Equal(("밤편지", "IU", "Palette"), (bare.Title, bare.Artist, bare.Album));
        Assert.Null(bare.Track);
    }

    private sealed class FakeSource(string name, Func<IReadOnlyList<TagCandidate>>? answer) : ITagSource
    {
        public string Name => name;
        public int Asked { get; private set; }

        public Task<IReadOnlyList<TagCandidate>> SearchAsync(TagQuery query, CancellationToken ct)
        {
            Asked++;
            return answer is null ? throw new HttpRequestException("down") : Task.FromResult(answer());
        }
    }

    [Fact]
    public async Task A_switched_off_source_isnt_asked_and_a_failing_one_is_named()
    {
        var mb = new FakeSource("MusicBrainz", () => [Candidate("MusicBrainz", "밤편지", "아이유", "Palette", 2017, 253)]);
        var itunes = new FakeSource("iTunes", null);
        var deezer = new FakeSource("Deezer", () => []);
        var service = new TagSearchService([mb, itunes, deezer]);

        var result = await service.SearchAsync(Song, ["Deezer"]);

        Assert.Equal((1, 1, 0), (mb.Asked, itunes.Asked, deezer.Asked));
        Assert.Equal(["MusicBrainz", "iTunes"], result.Sources);
        Assert.Equal(["iTunes"], result.FailedSources);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task With_every_source_asked_failing_it_says_so()
    {
        var service = new TagSearchService([new FakeSource("MusicBrainz", null), new FakeSource("iTunes", null)]);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync(Song, []));
    }

    private sealed class SlowSource(string name, TagCandidate answer) : ITagSource
    {
        public string Name => name;

        public async Task<IReadOnlyList<TagCandidate>> SearchAsync(TagQuery query, CancellationToken ct)
        {
            await Task.Delay(150, ct);
            return [answer];
        }
    }

    [Fact]
    public async Task Equally_good_answers_rank_in_the_sources_order_not_the_order_they_came_in()
    {
        // MusicBrainz answers last, but its equal answer still comes first
        var mb = new SlowSource("MusicBrainz", Candidate("MusicBrainz", "밤편지", "아이유", "Palette", 2017, 253));
        var itunes = new FakeSource("iTunes", () => [Candidate("iTunes", "밤편지", "아이유", "Palette", 2017, 253)]);
        var service = new TagSearchService([mb, itunes]);

        var result = await service.SearchAsync(Song, []);

        Assert.Equal(["MusicBrainz", "iTunes"], result.Candidates.Select(c => c.Source));
    }

    [Fact]
    public async Task With_every_source_switched_off_they_are_all_asked()
    {
        var mb = new FakeSource("MusicBrainz", () => []);
        var service = new TagSearchService([mb]);
        await service.SearchAsync(Song, ["MusicBrainz"]);
        Assert.Equal(1, mb.Asked);
    }

    // ---- lyrics beside the song ----

    [Fact]
    public void Finds_the_lrc_beside_the_song_in_utf8_or_cp949()
    {
        var folder = Directory.CreateTempSubdirectory("gotune-lrc");
        try
        {
            var song = Path.Combine(folder.FullName, "밤편지.mp3");
            File.WriteAllBytes(song, [0]);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(Path.Combine(folder.FullName, "밤편지.lrc"), Encoding.GetEncoding(949).GetBytes("[00:01.00]이 밤 그날의 반딧불을\n[00:05.00]당신의 창 가까이"));

            var found = Assert.Single(LocalLyricsSource.Find(song, "밤편지", "아이유"));
            Assert.True(found.IsSynced);
            Assert.Equal("폴더 파일", found.Source);
            Assert.Equal("밤편지.lrc", found.FileName);
            Assert.StartsWith("[00:01.00]이 밤 그날의 반딧불을", found.Lyrics);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Reads_utf8_with_or_without_a_bom()
    {
        Assert.Equal("가사", LocalLyricsSource.Read([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("가사")]));
        Assert.Equal("가사", LocalLyricsSource.Read(Encoding.UTF8.GetBytes("가사")));
    }

    [Fact]
    public void A_network_song_has_no_folder_to_look_in() =>
        Assert.Empty(LocalLyricsSource.Find("remote://abc/Music/song.mp3"));
}
