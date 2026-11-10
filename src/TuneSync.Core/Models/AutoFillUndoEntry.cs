namespace TuneSync.Core.Models;

/// <summary>One file's pre-apply tag snapshot, written to the undo log before AutoFillService
/// overwrites it - so "완전 자동" mode isn't silently irreversible.</summary>
public sealed class AutoFillUndoEntry
{
    public required string Path { get; init; }
    public required TrackTags Before { get; init; }
    public DateTime AppliedAtUtc { get; init; } = DateTime.UtcNow;
}
