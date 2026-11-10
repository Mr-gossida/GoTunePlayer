using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class RenameServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tunesync-rename-{Guid.NewGuid():N}");

    public RenameServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string PlaceFixture(string fixtureName, string destFileName, string artist, string title, uint track = 0)
    {
        var tempCopy = TestFixtures.CopyFixture(fixtureName);
        var destPath = Path.Combine(_dir, destFileName);
        File.Move(tempCopy, destPath);
        TagService.Write(new TrackTags { Path = destPath, Artist = artist, Title = title, Track = track });
        return destPath;
    }

    [Fact]
    public void BuildPreview_ComputesNewNameFromTags()
    {
        var path = PlaceFixture("sample.mp3", "old_name.mp3", "IU", "Good Day");
        var rules = new RenameRuleSet { SingleTrackPattern = "{Artist} - {Title}" };

        var preview = RenameService.BuildPreview([path], rules, isAlbumTrack: false);

        var item = Assert.Single(preview);
        Assert.Equal("IU - Good Day.mp3", item.NewFileName);
        Assert.True(item.WillChange);
    }

    [Fact]
    public void Execute_RenamesFileOnDisk()
    {
        var path = PlaceFixture("sample.mp3", "old_name.mp3", "IU", "Good Day");
        var rules = new RenameRuleSet { SingleTrackPattern = "{Artist} - {Title}" };
        var preview = RenameService.BuildPreview([path], rules, isAlbumTrack: false);

        var errors = RenameService.Execute(preview);

        Assert.Empty(errors);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_dir, "IU - Good Day.mp3")));
    }

    [Fact]
    public void Execute_MovesLyricsSidecar_AlongsideRenamedFile()
    {
        var path = PlaceFixture("sample.mp3", "old_name.mp3", "IU", "Good Day");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:00.00]가사");
        var rules = new RenameRuleSet { SingleTrackPattern = "{Artist} - {Title}" };
        var preview = RenameService.BuildPreview([path], rules, isAlbumTrack: false);

        RenameService.Execute(preview);

        Assert.True(File.Exists(Path.Combine(_dir, "IU - Good Day.lrc")));
    }

    [Fact]
    public void Execute_SkipsItemsThatAlreadyMatch()
    {
        var path = PlaceFixture("sample.mp3", "IU - Good Day.mp3", "IU", "Good Day");
        var rules = new RenameRuleSet { SingleTrackPattern = "{Artist} - {Title}" };
        var preview = RenameService.BuildPreview([path], rules, isAlbumTrack: false);

        Assert.False(Assert.Single(preview).WillChange);

        var errors = RenameService.Execute(preview);

        Assert.Empty(errors);
        Assert.True(File.Exists(path)); // untouched, not even re-moved to itself
    }

    [Fact]
    public void Execute_AutoSuffixesOnCollision_NeverOverwrites()
    {
        var pathA = PlaceFixture("sample.mp3", "a.mp3", "IU", "Good Day");
        var pathB = PlaceFixture("sample.mp3", "b.mp3", "IU2", "Good Day2");
        // Force both to the same new name so they collide on disk.
        var rules = new RenameRuleSet { SingleTrackPattern = "같은이름" };
        var preview = RenameService.BuildPreview([pathA, pathB], rules, isAlbumTrack: false);

        var errors = RenameService.Execute(preview);

        Assert.Empty(errors);
        Assert.True(File.Exists(Path.Combine(_dir, "같은이름.mp3")));
        Assert.True(File.Exists(Path.Combine(_dir, "같은이름 (2).mp3")));
    }

    [Fact]
    public void AlbumTrackPattern_IncludesZeroPaddedTrackNumber()
    {
        var path = PlaceFixture("sample.mp3", "old.mp3", "IU", "Good Day", track: 7);
        var rules = new RenameRuleSet { AlbumTrackPattern = "{Track} {Title}" };

        var preview = RenameService.BuildPreview([path], rules, isAlbumTrack: true);

        Assert.Equal("07 Good Day.mp3", Assert.Single(preview).NewFileName);
    }
}
