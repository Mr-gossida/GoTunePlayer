using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>One row of the rename preview: old name, new name with the part that changes picked
/// out (<see cref="NewHead"/> + <see cref="NewChanged"/> + <see cref="NewTail"/>), and a tick box.</summary>
public sealed partial class RenamePreviewItemViewModel : ObservableObject
{
    private readonly Action _onIncludedChanged;

    public RenamePreviewItemViewModel(RenamePreviewItem item, string location, Action onIncludedChanged)
    {
        Item = item;
        Location = location;
        _onIncludedChanged = onIncludedChanged;

        // The new name split into what it shares with the old one (start and end) and what differs.
        var oldName = item.OldFileName;
        var newName = item.NewFileName;
        var head = 0;
        while (head < oldName.Length && head < newName.Length && oldName[head] == newName[head]) head++;
        var tail = 0;
        while (tail < oldName.Length - head && tail < newName.Length - head
               && oldName[^(tail + 1)] == newName[^(tail + 1)]) tail++;
        NewHead = newName[..head];
        NewChanged = newName[head..(newName.Length - tail)];
        NewTail = newName[(newName.Length - tail)..];
    }

    public RenamePreviewItem Item { get; }

    public string OldFileName => Item.OldFileName;
    public string NewFileName => Item.NewFileName;
    public string NewHead { get; }
    public string NewChanged { get; }
    public string NewTail { get; }

    /// <summary>The folder, when the rows come from several (subfolders, or a selection).</summary>
    public string Location { get; }
    public bool HasLocation => Location.Length > 0;

    public bool WillChange => Item.WillChange;
    public RenameKind Kind => Item.Kind;
    public bool NeedsLook => Item.Kind is RenameKind.MissingTags or RenameKind.Collision;
    public string Note => Item.Note;
    public bool HasNote => Item.Note.Length > 0;

    public string KindLabel => Item.Kind switch
    {
        RenameKind.Same => "그대로",
        RenameKind.MissingTags => "태그 부족",
        RenameKind.Collision => "번호 붙임",
        _ => "바뀜",
    };

    [ObservableProperty]
    private string _status = "";

    public bool IsIncluded
    {
        get => Item.IsIncluded;
        set
        {
            if (Item.IsIncluded == value || !Item.WillChange) return;
            Item.IsIncluded = value;
            OnPropertyChanged();
            _onIncludedChanged();
        }
    }

    public void SyncStatus() => Status = Item.Status is "대기 중" ? "" : Item.Status;
}
