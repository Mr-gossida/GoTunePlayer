using System.Diagnostics;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

/// <summary>Defects found reviewing the Core services: file names, rename patterns, settings with
/// nulls, the scan index and linked folders.</summary>
public class CoreReviewFixesTests
{
    [Theory]
    [InlineData("01 - Intro")]
    [InlineData("01. - Intro")]
    public void A_track_number_isnt_read_as_the_artist(string name) => Assert.Null(FilenameParser.Parse(name));

    [Theory]
    [InlineData("IU - 밤편지", "IU", "밤편지")]
    [InlineData("03. IU - 밤편지", "IU", "밤편지")]
    [InlineData("[IU] 밤편지", "IU", "밤편지")]
    public void Artist_and_title_are_still_read(string name, string artist, string title) =>
        Assert.Equal((artist, title), FilenameParser.Parse(name));

    private static TrackTags Tags(string title = "밤편지", uint track = 7, uint disc = 0) =>
        new() { Path = "x.mp3", Artist = "IU", Title = title, Track = track, Disc = disc };

    [Fact]
    public void Disc_is_empty_without_one_and_not_written_twice()
    {
        var rules = new RenameRuleSet();
        Assert.Equal("07 밤편지", NamePatternFormatter.Format("{Disc}{Track} {Title}", Tags(), rules));
        Assert.Equal("2-07 밤편지", NamePatternFormatter.Format("{Disc}-{Track} {Title}", Tags(disc: 2), rules));
        Assert.Equal("2-07 밤편지", NamePatternFormatter.Format("{Track} {Title}", Tags(disc: 2), rules)); // the prefix on its own
    }

    [Fact]
    public void A_name_comes_out_as_windows_keeps_it()
    {
        var rules = new RenameRuleSet();
        Assert.Equal("밤편지끝", NamePatternFormatter.Format("{Title}", Tags("밤편지\t끝..."), rules)); // tab out, trailing dots off
        Assert.DoesNotContain(':', NamePatternFormatter.Format("{Artist}: {Title}", Tags(), rules)); // the pattern's own text too
        Assert.Equal("CON_", NamePatternFormatter.Format("{Title}", Tags("CON"), rules));
    }

    [Fact]
    public void A_null_list_in_the_settings_reads_as_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gotune-null-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "LibraryFolders": null, "DisabledTagSources": null }""");
            var settings = AppSettingsStore.Load(path);
            Assert.NotNull(settings.LibraryFolders);
            Assert.NotNull(settings.DisabledTagSources);
            Assert.False(File.Exists(path + ".bad")); // read fine, nothing kept aside
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_full_scan_drops_the_songs_it_didnt_meet()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gotune-index-{Guid.NewGuid():N}.json");
        try
        {
            var index = new LibraryIndex(file);
            var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            LibraryFile Song(string path) => new() { Path = path, Tags = new TrackTags { Path = path, Title = path } };
            index.Put(Song("a.mp3"), 1, stamp);
            index.Put(Song("b.mp3"), 1, stamp);

            index.BeginScan();
            Assert.NotNull(index.TryGet("a.mp3", 1, stamp)); // met; b.mp3 is gone
            index.ForgetUnmet();

            Assert.NotNull(index.TryGet("a.mp3", 1, stamp));
            Assert.Null(index.TryGet("b.mp3", 1, stamp));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    public void A_local_song_is_saved_through_a_copy_and_an_edit_that_fails_leaves_it_whole(string fixture)
    {
        var path = TestFixtures.CopyFixture(fixture);
        try
        {
            var before = File.ReadAllBytes(path);
            Assert.Throws<InvalidOperationException>(() => TuneSync.Core.Services.Remote.RemoteFiles.Edit(path, f =>
            {
                f.Tag.Title = "중간에 실패";
                throw new InvalidOperationException("the edit failed");
            }));
            Assert.Equal(before, File.ReadAllBytes(path)); // untouched

            TagService.Write(new TrackTags { Path = path, Artist = "IU", Title = "밤편지" });
            Assert.Equal("밤편지", TagService.Read(path).Title);
            // no copy left (its own: other tests save in the same folder meanwhile)
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "~" + Path.GetFileName(path) + ".gotune-tmp")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_linked_folder_leading_back_up_is_walked_once()
    {
        var root = Directory.CreateTempSubdirectory("gotune-walk");
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, "song.mp3"), [0]);
            var sub = Directory.CreateDirectory(Path.Combine(root.FullName, "sub"));
            File.WriteAllBytes(Path.Combine(sub.FullName, "two.mp3"), [0]);
            // a junction inside "sub" pointing back at the root: the walk used to go round it
            var loop = Path.Combine(sub.FullName, "loop");
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{loop}\" \"{root.FullName}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                mklink.WaitForExit();
            Assert.True(Directory.Exists(loop));

            var files = MediaLibraryScanner.WalkFiles(root.FullName, recurse: true, FileAttributes.System).Select(f => f.Name).OrderBy(n => n).ToList();

            Assert.Equal(["song.mp3", "two.mp3"], files);
        }
        finally
        {
            foreach (var link in root.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).SelectMany(d => d.EnumerateDirectories()))
                if (link.Attributes.HasFlag(FileAttributes.ReparsePoint)) link.Delete(); // the link only, not its target
            root.Delete(true);
        }
    }
}
