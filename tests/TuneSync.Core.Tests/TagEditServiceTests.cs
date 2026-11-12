using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class TagEditServiceTests
{
    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void Writes_tags_cover_and_lyrics_together_and_undoes_them(string fixture)
    {
        var path = TestFixtures.CopyFixture(fixture);
        try
        {
            var original = TagService.Read(path);
            var originalCover = CoverArtService.GetCover(path);
            var originalLyrics = TagService.ReadLyrics(path);
            var file = new LibraryFile { Path = path, Tags = original.Clone(), HasCover = originalCover is not null };

            var newTags = TagFields.Apply(file.Tags, new Dictionary<TagField, string>
            {
                [TagField.Artist] = "방탄소년단",
                [TagField.Track] = "7",
            });
            var cover = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "cover_b.jpg"));
            var undo = TagEditService.Write(file, newTags, cover, "[00:01.00]첫 줄");

            var written = TagService.Read(path);
            Assert.Equal("방탄소년단", written.Artist);
            Assert.Equal(7u, written.Track);
            Assert.Equal(original.Title, written.Title);
            Assert.Equal(cover, CoverArtService.GetCover(path));
            Assert.Equal("[00:01.00]첫 줄", TagService.ReadLyrics(path));
            Assert.True(file.HasCover);
            Assert.True(file.IsLyricsSynced);
            Assert.Equal("방탄소년단", file.Tags.Artist);

            TagEditService.Undo(undo);

            var restored = TagService.Read(path);
            Assert.Equal(original.Artist, restored.Artist);
            Assert.Equal(original.Track, restored.Track);
            Assert.Equal(originalCover, CoverArtService.GetCover(path));
            Assert.Equal(originalLyrics, TagService.ReadLyrics(path));
            Assert.Equal(original.Artist, file.Tags.Artist);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_cover_and_lyrics_remove_them()
    {
        var path = TestFixtures.CopyFixture("sample.mp3");
        try
        {
            var file = new LibraryFile { Path = path, Tags = TagService.Read(path) };
            TagEditService.Write(file, null, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "cover_a.jpg")), "가사");
            TagEditService.Write(file, null, [], "");

            Assert.Null(CoverArtService.GetCover(path));
            Assert.Null(TagService.ReadLyrics(path));
            Assert.False(file.HasCover);
            Assert.False(file.HasLyrics);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
