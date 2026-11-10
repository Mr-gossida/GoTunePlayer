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
}
