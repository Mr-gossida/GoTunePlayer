using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>Read-only display wrapper around one planned <see cref="RenamePreviewItem"/> row.</summary>
public sealed class RenamePreviewItemViewModel(RenamePreviewItem item)
{
    public RenamePreviewItem Item { get; } = item;

    public string OldFileName => Item.OldFileName;
    public string NewFileName => Item.NewFileName;
    public bool WillChange => Item.WillChange;
    public string Status => Item.Status;
}
