using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class MergeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tunesync-merge-{Guid.NewGuid():N}");
    private readonly string _source;
    private readonly string _target;
    private readonly string _quarantine;

    public MergeServiceTests()
    {
        _source = Path.Combine(_root, "source");
        _target = Path.Combine(_root, "target");
        _quarantine = Path.Combine(_target, "_TuneSync_Quarantine");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void Tag(string path, string artist, string title)
    {
        TagService.Write(new TrackTags { Path = path, Artist = artist, Title = title });
    }

    private string PlaceFixture(string folder, string fixtureName, string destFileName, string artist, string title)
    {
        var tempCopy = TestFixtures.CopyFixture(fixtureName);
        var destPath = Path.Combine(folder, destFileName);
        File.Move(tempCopy, destPath);
        Tag(destPath, artist, title);
        return destPath;
    }

    [Fact]
    public void BuildPlan_NoConflict_PlansAMove()
    {
        PlaceFixture(_source, "sample.mp3", "new.mp3", "IU", "Good Day");

        var plan = MergeService.BuildPlan(_source, _target, new MergeRuleSet());

        var action = Assert.Single(plan);
        Assert.Equal(MergeActionType.Move, action.ActionType);
    }

    [Fact]
    public void ExecutePlan_NoConflict_MovesFileOutOfSourceIntoTarget()
    {
        var srcPath = PlaceFixture(_source, "sample.mp3", "new.mp3", "IU", "Good Day");
        var plan = MergeService.BuildPlan(_source, _target, new MergeRuleSet());

        MergeService.ExecutePlan(plan, _target, _quarantine);

        Assert.False(File.Exists(srcPath));
        Assert.True(File.Exists(Path.Combine(_target, "new.mp3")));
    }

    [Fact]
    public void Conflict_KeepLargerFile_SourceLarger_ReplacesTarget()
    {
        // sample.flac is the larger fixture, sample.mp3 the smaller one (see TestAssets generation).
        PlaceFixture(_target, "sample.mp3", "existing.mp3", "IU", "Good Day");
        var srcPath = PlaceFixture(_source, "sample.flac", "existing.flac", "IU", "Good Day");

        var rules = new MergeRuleSet { ConflictResolution = MergeConflictResolution.KeepLargerFile };
        var plan = MergeService.BuildPlan(_source, _target, rules);
        var action = Assert.Single(plan);
        Assert.Equal(MergeActionType.Replace, action.ActionType);

        MergeService.ExecutePlan(plan, _target, _quarantine);

        Assert.False(File.Exists(srcPath));
        Assert.True(File.Exists(Path.Combine(_target, "existing.flac")));
        Assert.Single(Directory.GetFiles(_quarantine)); // the old, smaller target file
    }

    [Fact]
    public void Conflict_KeepLargerFile_TargetLarger_QuarantinesSource()
    {
        PlaceFixture(_target, "sample.flac", "existing.flac", "IU", "Good Day");
        var srcPath = PlaceFixture(_source, "sample.mp3", "existing.mp3", "IU", "Good Day");
        var originalTargetBytes = File.ReadAllBytes(Path.Combine(_target, "existing.flac"));

        var rules = new MergeRuleSet { ConflictResolution = MergeConflictResolution.KeepLargerFile };
        var plan = MergeService.BuildPlan(_source, _target, rules);
        var action = Assert.Single(plan);
        Assert.Equal(MergeActionType.Quarantine, action.ActionType);

        MergeService.ExecutePlan(plan, _target, _quarantine);

        Assert.False(File.Exists(srcPath));
        // target file must be byte-for-byte untouched
        Assert.Equal(originalTargetBytes, File.ReadAllBytes(Path.Combine(_target, "existing.flac")));
        Assert.Single(Directory.GetFiles(_quarantine));
    }

    [Fact]
    public void Conflict_RenameWithSuffix_KeepsBothCopies()
    {
        PlaceFixture(_target, "sample.mp3", "existing.mp3", "IU", "Good Day");
        PlaceFixture(_source, "sample.flac", "existing.flac", "IU", "Good Day");

        var rules = new MergeRuleSet { ConflictResolution = MergeConflictResolution.RenameWithSuffix };
        var plan = MergeService.BuildPlan(_source, _target, rules);
        MergeService.ExecutePlan(plan, _target, _quarantine);

        var targetFiles = Directory.GetFiles(_target).Select(Path.GetFileName).ToList();
        Assert.Contains("existing.mp3", targetFiles);
        Assert.Contains("existing (2).flac", targetFiles);
        Assert.False(Directory.Exists(_quarantine) && Directory.GetFiles(_quarantine).Length > 0);
    }

    [Fact]
    public void ExecutePlan_NeverDeletes_TotalFileCountIsPreserved()
    {
        PlaceFixture(_target, "sample.flac", "existing.flac", "IU", "Good Day");
        PlaceFixture(_source, "sample.mp3", "existing.mp3", "IU", "Good Day");
        PlaceFixture(_source, "sample.m4a", "unique.m4a", "아이유", "밤편지");

        var rules = new MergeRuleSet { ConflictResolution = MergeConflictResolution.KeepLargerFile };
        var plan = MergeService.BuildPlan(_source, _target, rules);
        MergeService.ExecutePlan(plan, _target, _quarantine);

        var remaining = Directory.GetFiles(_source).Length
            + Directory.GetFiles(_target).Length
            + (Directory.Exists(_quarantine) ? Directory.GetFiles(_quarantine).Length : 0);

        Assert.Equal(3, remaining); // 1 target original + 2 source files, none ever deleted
    }

    [Fact]
    public void ExecutePlan_MovesLyricsSidecar_AlongsideAudioFile()
    {
        var audioPath = PlaceFixture(_source, "sample.mp3", "new.mp3", "IU", "Good Day");
        var lrcPath = Path.ChangeExtension(audioPath, ".lrc");
        File.WriteAllText(lrcPath, "[00:00.00]가사");

        var plan = MergeService.BuildPlan(_source, _target, new MergeRuleSet());
        MergeService.ExecutePlan(plan, _target, _quarantine);

        Assert.True(File.Exists(Path.Combine(_target, "new.lrc")));
        Assert.False(File.Exists(lrcPath));
    }

    [Fact]
    public void KeepBetterQuality_PrefersLosslessSource()
    {
        PlaceFixture(_source, "sample.flac", "song.flac", "IU", "Good Day");
        PlaceFixture(_target, "sample.mp3", "song.mp3", "IU", "Good Day");

        var plan = MergeService.BuildPlan(_source, _target, new MergeRuleSet { ConflictResolution = MergeConflictResolution.KeepBetterQuality });

        Assert.Equal(MergeActionType.Replace, Assert.Single(plan).ActionType);
    }

    [Fact]
    public void KeepBetterQuality_KeepsLosslessTarget()
    {
        PlaceFixture(_source, "sample.mp3", "song.mp3", "IU", "Good Day");
        PlaceFixture(_target, "sample.flac", "song.flac", "IU", "Good Day");

        var plan = MergeService.BuildPlan(_source, _target, new MergeRuleSet { ConflictResolution = MergeConflictResolution.KeepBetterQuality });

        Assert.Equal(MergeActionType.Quarantine, Assert.Single(plan).ActionType);
    }

    [Fact]
    public void Override_KeepBoth_GetsUniqueName()
    {
        PlaceFixture(_source, "sample.mp3", "song.mp3", "IU", "Good Day");
        PlaceFixture(_target, "sample.mp3", "song.mp3", "IU", "Good Day");
        var plan = MergeService.BuildPlan(_source, _target, new MergeRuleSet());

        var both = MergeService.Override(plan[0], MergeActionType.RenameAndMove);

        Assert.Equal(MergeActionType.RenameAndMove, both.ActionType);
        Assert.Equal("song (2).mp3", both.DestFileName);
    }

    [Fact]
    public void Undo_PutsEverythingBack_AndEmptyFoldersCanBeRemoved()
    {
        var sub = Path.Combine(_source, "sub");
        Directory.CreateDirectory(sub);
        var moved = PlaceFixture(sub, "sample.mp3", "new.mp3", "IU", "Good Day");
        var srcLoser = PlaceFixture(_source, "sample.mp3", "dup.mp3", "IU", "Blueming");
        var tgt = PlaceFixture(_target, "sample.flac", "dup.flac", "IU", "Blueming");
        var plan = MergeService.BuildPlan(_source, _target, new MergeRuleSet { ConflictResolution = MergeConflictResolution.KeepTargetAlways });

        var manifest = new List<(string From, string To)>();
        MergeService.ExecutePlan(plan, _target, _quarantine, manifest);
        Assert.False(File.Exists(moved));
        Assert.True(File.Exists(Path.Combine(_quarantine, "dup.mp3")));

        Assert.Equal(1, MergeService.RemoveEmptyFolders(_source, keepRoot: true));
        Assert.False(Directory.Exists(sub));

        Assert.Empty(MergeService.Undo(manifest));
        Assert.True(File.Exists(moved));
        Assert.True(File.Exists(srcLoser));
        Assert.True(File.Exists(tgt));
        Assert.False(File.Exists(Path.Combine(_target, "new.mp3")));
    }
}
