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

    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void A_new_cover_keeps_the_files_other_pictures_and_undo_puts_them_all_back(string fixture)
    {
        var path = TestFixtures.CopyFixture(fixture);
        try
        {
            var front = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "cover_a.jpg"));
            var back = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "cover_b.jpg"));
            using (var tagged = TagLib.File.Create(path))
            {
                tagged.Tag.Pictures =
                [
                    new TagLib.Picture(new TagLib.ByteVector(front)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
                    new TagLib.Picture(new TagLib.ByteVector(back)) { Type = TagLib.PictureType.BackCover, MimeType = "image/jpeg" },
                ];
                tagged.Save();
            }
            var file = new LibraryFile { Path = path, Tags = TagService.Read(path), HasCover = true };
            var cover = (byte[])back.Clone();
            cover[^3] ^= 0x01; // another picture than either

            var undo = TagEditService.Write(file, null, cover, null);

            using (var written = TagLib.File.Create(path))
            {
                var pictures = written.Tag.Pictures;
                Assert.Equal(2, pictures.Length);
                Assert.Equal(cover, pictures[0].Data.Data); // the new cover, first
                Assert.Equal(back, pictures[1].Data.Data);  // the back cover stays
            }

            TagEditService.Undo(undo);

            using var restored = TagLib.File.Create(path);
            Assert.Equal([front, back], restored.Tag.Pictures.Select(p => p.Data.Data));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The editor shows only the first artist/genre and the year, so saving one field must leave the
    // others as the file has them: writing them back would keep one value and cut the date to "2017".
    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.m4a")]
    public void Saving_the_title_keeps_every_artist_and_genre_and_the_full_date(string fixture)
    {
        var path = TestFixtures.CopyFixture(fixture);
        try
        {
            NativeTags.Write(path, ["아이유", "성시경"], ["K-Pop", "발라드"], "2017-05-12");
            var file = new LibraryFile { Path = path, Tags = TagService.Read(path) };
            var originalTitle = file.Tags.Title;
            Assert.Equal("아이유", file.Tags.Artist);
            Assert.Equal(2017u, file.Tags.Year);

            var undo = TagEditService.Write(file, TagFields.Apply(file.Tags, new Dictionary<TagField, string>
            {
                [TagField.Title] = "새 제목",
            }), null, null);

            var saved = NativeTags.Read(path);
            Assert.Equal("새 제목", saved.Title);
            Assert.Equal(["아이유", "성시경"], saved.Artists);
            Assert.Equal(["K-Pop", "발라드"], saved.Genres);
            Assert.Equal("2017-05-12", saved.Date);

            TagEditService.Undo(undo);

            var restored = NativeTags.Read(path);
            Assert.Equal(originalTitle, TagService.Read(path).Title);
            Assert.Equal(["아이유", "성시경"], restored.Artists);
            Assert.Equal(["K-Pop", "발라드"], restored.Genres);
            Assert.Equal("2017-05-12", restored.Date);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.m4a")]
    public void Undoing_an_artist_edit_puts_every_artist_back(string fixture)
    {
        var path = TestFixtures.CopyFixture(fixture);
        try
        {
            NativeTags.Write(path, ["아이유", "성시경"], ["K-Pop", "발라드"], "2017-05-12");
            var file = new LibraryFile { Path = path, Tags = TagService.Read(path) };

            var undo = TagEditService.Write(file, TagFields.Apply(file.Tags, new Dictionary<TagField, string>
            {
                [TagField.Artist] = "태연",
            }), null, null);
            Assert.Equal(["태연"], NativeTags.Read(path).Artists);

            TagEditService.Undo(undo);

            var restored = NativeTags.Read(path);
            Assert.Equal(["아이유", "성시경"], restored.Artists);
            Assert.Equal(["K-Pop", "발라드"], restored.Genres);
            Assert.Equal("2017-05-12", restored.Date);
            Assert.Equal("아이유", file.Tags.Artist);
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
