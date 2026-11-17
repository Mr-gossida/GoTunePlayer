using CommunityToolkit.Mvvm.ComponentModel;

namespace TuneSync.App.ViewModels;

/// <summary>The filters over the library file list. "누락만 보기" (missing-only): with OR semantics -
/// with none checked everything shows, with any checked only files missing at least one of the
/// checked kinds show. "가사 종류" (일반 / 싱크): a separate condition, ANDed with the first; within
/// it, either kind checked shows songs with that kind of lyrics.</summary>
public sealed partial class FilterPanelViewModel : ObservableObject
{
    public event EventHandler? FilterChanged;

    [ObservableProperty]
    private bool _showMissingCoverOnly;

    [ObservableProperty]
    private bool _showMissingTagsOnly;

    [ObservableProperty]
    private bool _showMissingLyricsOnly;

    /// <summary>Songs whose lyrics are plain text (no line times).</summary>
    [ObservableProperty]
    private bool _showPlainLyricsOnly;

    /// <summary>Songs with synced (LRC) lyrics.</summary>
    [ObservableProperty]
    private bool _showSyncedLyricsOnly;

    partial void OnShowMissingCoverOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowMissingTagsOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowMissingLyricsOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowPlainLyricsOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowSyncedLyricsOnlyChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    public bool IsActive => ShowMissingCoverOnly || ShowMissingTagsOnly || ShowMissingLyricsOnly
        || ShowPlainLyricsOnly || ShowSyncedLyricsOnly;

    public bool Matches(LibraryFileViewModel item)
    {
        var missingOn = ShowMissingCoverOnly || ShowMissingTagsOnly || ShowMissingLyricsOnly;
        var missing = !missingOn
            || (ShowMissingCoverOnly && item.IsMissingCover)
            || (ShowMissingTagsOnly && item.IsMissingTags)
            || (ShowMissingLyricsOnly && item.IsMissingLyrics);
        if (!missing) return false;

        if (!ShowPlainLyricsOnly && !ShowSyncedLyricsOnly) return true;
        var model = item.Model;
        return (ShowPlainLyricsOnly && model.HasLyrics && !model.IsLyricsSynced)
            || (ShowSyncedLyricsOnly && model.HasLyrics && model.IsLyricsSynced);
    }

    /// <summary>Turns every filter off (so songs picked elsewhere are all in view).</summary>
    public void Clear()
    {
        ShowMissingCoverOnly = false;
        ShowMissingTagsOnly = false;
        ShowMissingLyricsOnly = false;
        ShowPlainLyricsOnly = false;
        ShowSyncedLyricsOnly = false;
    }
}
