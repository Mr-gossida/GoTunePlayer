namespace TuneSync.Core.Models;

public enum MergeAction
{
    Copy,           // no conflict: move/copy into destination as-is
    KeepExisting,   // duplicate found, existing file wins: new file goes to quarantine
    ReplaceExisting // duplicate found, new file wins: existing file goes to quarantine
}

public sealed class MergePlanItem
{
    public required string SourcePath { get; init; }
    public string? SourceLrcPath { get; init; }
    public required string DestFileName { get; init; }
    public string? ConflictWithPath { get; init; }
    public required MergeAction Action { get; init; }
    public long SourceSize { get; init; }
    public long? ConflictSize { get; init; }
}
