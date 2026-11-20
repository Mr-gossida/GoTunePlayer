using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class TagServiceTests
{
    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void WriteThenRead_RoundTripsAllFields(string fixtureName)
    {
        var path = TestFixtures.CopyFixture(fixtureName);
        try
        {
            var tags = new TrackTags
            {
                Path = path,
                Title = "Test Title",
                Artist = "Test Artist",
                AlbumArtist = "Test Album Artist",
                Album = "Test Album",
                Composer = "Test Composer",
                Genre = "Test Genre",
                Track = 7,
                Disc = 2,
                Year = 2024,
            };

            TagService.Write(tags);
            var read = TagService.Read(path);

            Assert.Equal(tags.Title, read.Title);
            Assert.Equal(tags.Artist, read.Artist);
            Assert.Equal(tags.AlbumArtist, read.AlbumArtist);
            Assert.Equal(tags.Album, read.Album);
            Assert.Equal(tags.Composer, read.Composer);
            Assert.Equal(tags.Genre, read.Genre);
            Assert.Equal(tags.Track, read.Track);
            Assert.Equal(tags.Disc, read.Disc);
            Assert.Equal(tags.Year, read.Year);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Auto-fill hands Write the whole TrackTags with one field changed. TrackTags holds only the
    // first artist/genre and the year, so the fields it didn't change must stay as the file has them.
    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void Write_LeavesUnchangedFieldsAsTheFileHasThem(string fixtureName)
    {
        var path = TestFixtures.CopyFixture(fixtureName);
        try
        {
            NativeTags.Write(path, ["아이유", "성시경"], ["K-Pop", "발라드"], "2017-05-12");
            var tags = TagService.Read(path);
            tags.Album = "Palette";

            TagService.Write(tags);

            var read = NativeTags.Read(path);
            Assert.Equal("Palette", TagService.Read(path).Album);
            Assert.Equal(["아이유", "성시경"], read.Artists);
            Assert.Equal(["K-Pop", "발라드"], read.Genres);
            Assert.Equal("2017-05-12", read.Date);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void WriteThenReadLyrics_RoundTrips(string fixtureName)
    {
        var path = TestFixtures.CopyFixture(fixtureName);
        try
        {
            const string lyrics = "첫 줄\n두 번째 줄";
            TagService.WriteLyrics(path, lyrics);

            Assert.Equal(lyrics, TagService.ReadLyrics(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLyrics_ReturnsNull_WhenNoneWritten()
    {
        var path = TestFixtures.CopyFixture("sample.mp3");
        try
        {
            Assert.Null(TagService.ReadLyrics(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("song.flac", true)]
    [InlineData("song.m4a", true)]
    [InlineData("cover.jpg", false)]
    [InlineData("notes.txt", false)]
    public void IsSupported_MatchesKnownAudioExtensions(string fileName, bool expected)
    {
        Assert.Equal(expected, TagService.IsSupported(fileName));
    }
}
