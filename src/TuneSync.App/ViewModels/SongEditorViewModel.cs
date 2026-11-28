using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TuneSync.App.ViewModels;

/// <summary>The one-song tag editor window (SongEditorView): its own <see cref="TagEditorViewModel"/>
/// on one song at a time, stepping through the songs it was opened with (‹ 2 / 14 ›). Edits
/// wait on each song as in 태그 관리 and are saved together, without the several-songs review.</summary>
public sealed partial class SongEditorViewModel : ObservableObject
{
    public SongEditorViewModel(TagEditorViewModel editor, IReadOnlyList<LibraryFileViewModel> songs, int index)
    {
        Editor = editor;
        Songs = songs;
        editor.ReviewSeveral = false;
        editor.OffersRename = true;
        editor.LimitTo(songs);
        Show(Math.Clamp(index, 0, songs.Count - 1));
    }

    public TagEditorViewModel Editor { get; }

    public IReadOnlyList<LibraryFileViewModel> Songs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(Current))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    private int _index;

    public LibraryFileViewModel Current => Songs[Index];

    /// <summary>Opened with several songs: the ‹ › to step through them.</summary>
    public bool HasMany => Songs.Count > 1;

    public string PositionText => $"{Index + 1} / {Songs.Count}";

    private bool CanPrevious() => Index > 0;

    private bool CanNext() => Index < Songs.Count - 1;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous() => Show(Index - 1);

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() => Show(Index + 1);

    private void Show(int index)
    {
        Index = index;
        Editor.SetSelection([Songs[index]]);
    }
}
