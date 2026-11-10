namespace TuneSync.Core.Models;

public enum MergeConflictResolution
{
    /// <summary>Between the source file and the matching file already in the target, keep
    /// whichever has the larger file size; the loser is quarantined, never deleted.</summary>
    KeepLargerFile,

    /// <summary>Same as <see cref="KeepLargerFile"/> but compares last-write time instead.</summary>
    KeepNewerFile,

    /// <summary>The source always wins: the target's existing file is quarantined and the
    /// source takes its place.</summary>
    KeepSourceAlways,

    /// <summary>The target always wins: the source is quarantined, the target is untouched.</summary>
    KeepTargetAlways,

    /// <summary>Not a real conflict - both copies are kept, the source gets a " (2)", " (3)", ...
    /// suffix so it lands in the target without overwriting anything.</summary>
    RenameWithSuffix,
}

/// <summary>A user-editable policy for <see cref="Services.MergeService"/>. Persisted as JSON so
/// "병합 규칙" can be a settings screen instead of a recompile.</summary>
public sealed class MergeRuleSet
{
    public MergeConflictResolution ConflictResolution { get; set; } = MergeConflictResolution.KeepLargerFile;

    /// <summary>Created under the target folder; every file that loses a duplicate comparison is
    /// moved here instead of being deleted.</summary>
    public string QuarantineFolderName { get; set; } = "_TuneSync_Quarantine";

    public bool IncludeSubfolders { get; set; } = true;
}
