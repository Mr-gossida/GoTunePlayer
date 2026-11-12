using TuneSync.Core.Models;

namespace TuneSync.Core.Tests;

public class TagFieldsTests
{
    [Theory]
    [InlineData(TagField.Track, "3/12", "3")]
    [InlineData(TagField.Track, " 03 ", "3")]
    [InlineData(TagField.Disc, "2 / ", "2")]
    [InlineData(TagField.Year, "2017", "2017")]
    [InlineData(TagField.Year, "", "")]
    [InlineData(TagField.Track, "0", "")]
    [InlineData(TagField.Artist, "  방탄소년단 ", "방탄소년단")]
    public void Normalizes_valid_input(TagField field, string text, string expected)
    {
        Assert.True(TagFields.TryNormalize(field, text, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(TagField.Year, "17")]
    [InlineData(TagField.Year, "abcd")]
    [InlineData(TagField.Track, "three")]
    [InlineData(TagField.Track, "1000")]
    public void Rejects_invalid_numbers(TagField field, string text)
    {
        Assert.False(TagFields.TryNormalize(field, text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Apply_changes_only_edited_fields_on_a_copy()
    {
        var original = new TrackTags { Path = "a.mp3", Title = "봄날", Artist = "BTS", Album = "WINGS", Track = 1, Year = 2017 };
        var edits = new Dictionary<TagField, string> { [TagField.Artist] = "방탄소년단", [TagField.Track] = "5", [TagField.Year] = "" };

        var result = TagFields.Apply(original, edits);

        Assert.Equal("방탄소년단", result.Artist);
        Assert.Equal(5u, result.Track);
        Assert.Equal(0u, result.Year);
        Assert.Equal("봄날", result.Title);
        Assert.Equal("WINGS", result.Album);
        Assert.Equal("BTS", original.Artist); // the original is untouched
        Assert.Equal("", TagFields.Get(result, TagField.Year));
        Assert.Equal("5", TagFields.Get(result, TagField.Track));
    }
}
