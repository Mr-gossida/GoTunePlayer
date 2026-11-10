namespace TuneSync.Core.Models;

public enum MergeActionType
{
    /// <summary>No conflict - moves straight into the target.</summary>
    Move,

    /// <summary>A conflict where the target wins - the source is quarantined untouched.</summary>
    Quarantine,

    /// <summary>A conflict where the source wins - the existing target file is quarantined and
    /// the source takes its place.</summary>
    Replace,

    /// <summary>Not a conflict by rule - the source is renamed with a numeric suffix so both
    /// copies survive in the target.</summary>
    RenameAndMove,
}

/// <summary>One planned move, produced by <see cref="Services.MergeService.BuildPlan"/> without
/// touching disk. <see cref="Services.MergeService.ExecutePlan"/> is what actually performs it.</summary>
public sealed class MergeAction
{
    public required string SourcePath { get; init; }
    public string? SourceLyricsPath { get; init; }
    public required string DestFileName { get; init; }
    public required MergeActionType ActionType { get; init; }
    public required string Reason { get; init; }
    public string? ConflictWithPath { get; init; }
    public long SourceSize { get; init; }
    public long? ConflictSize { get; init; }
}
