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

    private List<RenamePreviewItem> Plan(RenameMode mode, params string[] paths) =>
        RenameService.Plan(RenameService.ReadTargets(paths), new RenameRuleSet(), mode);

    [Fact]
    public void Plan_MarksChangeSameAndMissingTags()
    {
        var change = PlaceFixture("sample.mp3", "x.mp3", "IU", "Good Day");
        var same = PlaceFixture("sample.mp3", "IU - Blueming.mp3", "IU", "Blueming");
        var missing = PlaceFixture("sample.mp3", "y.mp3", "", "Lonely");

        var plan = Plan(RenameMode.Single, change, same, missing);

        Assert.Equal(RenameKind.Change, plan[0].Kind);
        Assert.True(plan[0].IsIncluded);
        Assert.Equal(RenameKind.Same, plan[1].Kind);
        Assert.Equal(RenameKind.MissingTags, plan[2].Kind);
        Assert.False(plan[2].IsIncluded);
        Assert.Contains("아티스트", plan[2].Note);
    }

    [Fact]
    public void Plan_NumbersNamesTakenOnDiskOrInTheBatch()
    {
        PlaceFixture("sample.mp3", "IU - Good Day.mp3", "Someone", "Else"); // holds the name
        var a = PlaceFixture("sample.mp3", "a.mp3", "IU", "Good Day");
        var b = PlaceFixture("sample.mp3", "b.mp3", "IU", "Good Day");

        var plan = Plan(RenameMode.Single, a, b);

        Assert.Equal("IU - Good Day (2).mp3", plan[0].NewFileName);
        Assert.Equal("IU - Good Day (3).mp3", plan[1].NewFileName);
        Assert.All(plan, p => Assert.Equal(RenameKind.Collision, p.Kind));
    }

    [Fact]
    public void Plan_AutoMode_UsesAlbumPatternOnlyForAlbumTracks()
    {
        var single = PlaceFixture("sample.mp3", "s.mp3", "IU", "Good Day");
        var albumTrack = PlaceFixture("sample.mp3", "t.mp3", "IU", "Palette", track: 3);
        TagService.Write(new TrackTags { Path = albumTrack, Artist = "IU", Title = "Palette", Track = 3, Album = "Palette" });

        var plan = Plan(RenameMode.Auto, single, albumTrack);

        Assert.Equal("IU - Good Day.mp3", plan[0].NewFileName);
        Assert.Equal("03 Palette.mp3", plan[1].NewFileName);
    }

    [Fact]
    public void Execute_SkipsUntickedRows_AndUndoRestoresNamesAndSidecar()
    {
        var a = PlaceFixture("sample.mp3", "a.mp3", "IU", "Good Day");
        var b = PlaceFixture("sample.mp3", "b.mp3", "IU", "Blueming");
        File.WriteAllText(Path.ChangeExtension(a, ".lrc"), "[00:01.00]hi");
        var plan = Plan(RenameMode.Single, a, b);
        plan[1].IsIncluded = false;

        var moves = new List<(string From, string To)>();
        Assert.Empty(RenameService.Execute(plan, moves));
        Assert.True(File.Exists(Path.Combine(_dir, "IU - Good Day.lrc")));
        Assert.True(File.Exists(b));
        Assert.Equal(2, moves.Count);

        Assert.Empty(RenameService.Undo(moves));
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(Path.ChangeExtension(a, ".lrc")));
        Assert.False(File.Exists(Path.Combine(_dir, "IU - Good Day.mp3")));
    }
}
