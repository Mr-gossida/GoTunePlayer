namespace TuneSync.Core.Models;

public sealed class RenamePreviewItem
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public string OldFileName => Path.GetFileName(OldPath);
    public string NewFileName => Path.GetFileName(NewPath);
    public bool WillChange => !string.Equals(OldPath, NewPath, StringComparison.Ordinal);
    public string Status { get; set; } = "대기 중";
}
