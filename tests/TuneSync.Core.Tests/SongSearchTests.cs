using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class SongSearchTests
{
    private static SongSearchText Song(string title, string artist = "", string album = "", string fileName = "") =>
        new(title, artist, album, fileName);

    private static int Rank(SongSearchText song, string query) =>
        SongSearch.Rank(song, query, query.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static List<string> Titles(IEnumerable<SongSearchText> songs, string query) =>
        [.. SongSearch.Find(songs, query, s => s).Select(s => s.Title)];

    [Fact]
    public void Ranks_title_matches_first_then_artist_album_and_file_name()
    {
        Assert.Equal(0, Rank(Song("Love"), "love"));
        Assert.Equal(1, Rank(Song("Love Story"), "love"));
        Assert.Equal(2, Rank(Song("My Love"), "love"));
        Assert.Equal(3, Rank(Song("Dinosaur", artist: "Love Band"), "love"));
        Assert.Equal(4, Rank(Song("Dinosaur", album: "Songs of Love"), "love"));
        Assert.Equal(5, Rank(Song("Dinosaur", fileName: "03 - love.mp3"), "love"));
        Assert.Equal(SongSearch.NoMatch, Rank(Song("Dinosaur", "AKMU", "Summer Episode", "01.flac"), "love"));
    }

    [Fact]
    public void The_best_place_a_query_is_found_decides_the_rank()
    {
        // In the title and the artist: the title counts.
        Assert.Equal(2, Rank(Song("My Love", artist: "Love Band", album: "Love", fileName: "love.mp3"), "love"));
        // In the artist and the album: the artist counts.
        Assert.Equal(3, Rank(Song("Dinosaur", artist: "Love Band", album: "Love"), "love"));
    }

    [Fact]
    public void Several_words_spread_over_the_fields_match_last()
    {
        var song = Song("Love Lee", "AKMU", "Love Lee", "01 Love Lee.flac");

        Assert.Equal(6, Rank(song, "akmu love"));
        Assert.Equal(6, Rank(song, "flac akmu lee"));
        Assert.Equal(SongSearch.NoMatch, Rank(song, "akmu dinosaur")); // every word must be there
        // The words together in one field are an ordinary match, not a several-words one.
        Assert.Equal(0, Rank(song, "love lee"));
    }

    [Fact]
    public void A_single_word_found_nowhere_as_a_whole_does_not_match()
    {
        Assert.Equal(SongSearch.NoMatch, Rank(Song("Lo", "ve"), "love"));
    }

    [Fact]
    public void Ignores_case()
    {
        Assert.Equal(0, Rank(Song("LOVE"), "love"));
        Assert.Equal(3, Rank(Song("Dinosaur", artist: "akmu"), "AKMU"));
    }

    [Fact]
    public void Finds_korean_text()
    {
        Assert.Equal(1, Rank(Song("오랜 날 오랜 밤", "악뮤"), "오랜 날"));
        Assert.Equal(3, Rank(Song("오랜 날 오랜 밤", "악뮤"), "악뮤"));
        Assert.Equal(6, Rank(Song("오랜 날 오랜 밤", "악뮤"), "악뮤 오랜"));
    }

    [Fact]
    public void Find_returns_matches_best_first_whatever_the_list_order()
    {
        SongSearchText[] songs =
        [
            Song("Several words", artist: "AKMU", fileName: "01.flac"),  // "akmu flac": 6 (for that query only)
            Song("File", fileName: "love.mp3"),                        // 5
            Song("Album", album: "Love"),                              // 4
            Song("Artist", artist: "Love"),                            // 3
            Song("My Love"),                                           // 2
            Song("Love Story"),                                        // 1
            Song("Unrelated"),
            Song("Love"),                                              // 0
        ];

        Assert.Equal(["Love", "Love Story", "My Love", "Artist", "Album", "File"], Titles(songs, "love"));
        Assert.Equal(["Several words"], Titles(songs, "akmu flac"));
    }

    [Fact]
    public void Find_keeps_list_order_within_a_rank()
    {
        SongSearchText[] songs = [Song("Love C"), Song("My Love"), Song("Love A"), Song("Love B")];

        Assert.Equal(["Love C", "Love A", "Love B", "My Love"], Titles(songs, "love"));
    }

    [Fact]
    public void Find_trims_the_query_and_finds_nothing_for_an_empty_one()
    {
        SongSearchText[] songs = [Song("Love"), Song("Dinosaur")];

        Assert.Equal(["Love"], Titles(songs, "  love "));
        Assert.Empty(Titles(songs, ""));
        Assert.Empty(Titles(songs, "   "));
        Assert.Empty(Titles(songs, "nothing like it"));
    }
}
