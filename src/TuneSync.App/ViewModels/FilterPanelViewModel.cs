using CommunityToolkit.Mvvm.ComponentModel;

namespace TuneSync.App.ViewModels;

/// <summary>The three "누락만 보기" (missing-only) toggles over the library file list. Combined
/// with OR semantics: with none checked everything shows, with any checked only files missing at
/// least one of the checked kinds show.</summary>
public sealed partial class FilterPanelViewModel : ObservableObject
{
    public event EventHandler? FilterChanged;

    [ObservableProperty]
    private bool _showMissingCoverOnly;

    [ObservableProperty]
    private bool _showMissingTagsOnly;

    [ObservableProperty]
    private bool _showMissingLyricsOnly;

    partial void OnShowMissingCoverOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowMissingTagsOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowMissingLyricsOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    public bool IsActive => ShowMissingCoverOnly || ShowMissingTagsOnly || ShowMissingLyricsOnly;

    public bool Matches(LibraryFileViewModel item)
    {
        if (!IsActive) return true;

        return (ShowMissingCoverOnly && item.IsMissingCover)
            || (ShowMissingTagsOnly && item.IsMissingTags)
            || (ShowMissingLyricsOnly && item.IsMissingLyrics);
    }
}
