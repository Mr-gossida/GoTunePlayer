using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TuneSync.App.ViewModels;

/// <summary>Composition root for the 태그 관리 page: groups the file browser, the tag editor, the
/// auto-fill task panel and playback for the view's DataContext, and passes the list selection on.</summary>
public sealed partial class LibraryPageViewModel : ObservableObject
{
    public LibraryPageViewModel(LibraryViewModel library, AutoMatchViewModel autoMatch, PlaybackViewModel playback, TagEditorViewModel editor)
    {
        Library = library;
        AutoMatch = autoMatch;
        Playback = playback;
        Editor = editor;
    }

    public TagEditorViewModel Editor { get; }
    public LibraryViewModel Library { get; }
    public AutoMatchViewModel AutoMatch { get; }
    public PlaybackViewModel Playback { get; }

    [ObservableProperty]
    private LibraryFileViewModel? _selectedFile;

    /// <summary>How many rows are selected; the details panel shows one song's details only when
    /// exactly one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSingleSelection), nameof(ShowMultiSelection), nameof(ShowNoSelection))]
    private int _selectedCount;

    public bool ShowSingleSelection => SelectedCount == 1 && SelectedFile is not null;
    public bool ShowMultiSelection => SelectedCount > 1;
    public bool ShowNoSelection => SelectedCount == 0;

    /// <summary>Called by the view whenever the list/grid selection changes.</summary>
    public void OnSelectionChanged()
    {
        var selection = Library.Files.Where(f => f.IsSelected).ToList();
        SelectedCount = selection.Count;
        OnPropertyChanged(nameof(ShowSingleSelection));
        AutoMatch.OnSelectionChanged(selection);
        Editor.SetSelection(selection);
    }
}
