using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>
/// Interaction logic for LibraryView.xaml
/// </summary>
public partial class LibraryView : Page
{
    private readonly LibraryPageViewModel _viewModel;

    public LibraryView(LibraryPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // List and grid keep separate selections; clear both on switch so "선택한 파일" never
        // includes rows selected in the view that's now hidden.
        _viewModel.Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LibraryViewModel.ViewMode)) return;
            FileListGrid.UnselectAll();
            FileTileList.UnselectAll();
            foreach (var file in _viewModel.Library.Files) file.IsSelected = false;
            _viewModel.OnSelectionChanged();
        };
    }

    /// <summary>Mirrors the visible control's selection onto the rows' IsSelected, which is what
    /// auto-fill uses for "선택한 파일". Uses the added/removed deltas, so it stays correct for
    /// rows that are virtualized (not currently realized on screen).</summary>
    private void FileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems)
            if (item is LibraryFileViewModel file) file.IsSelected = false;
        foreach (var item in e.AddedItems)
            if (item is LibraryFileViewModel file) file.IsSelected = true;
        _viewModel.OnSelectionChanged();
    }

    /// <summary>Opened from the player's queue ("태그 관리에서 보기"): shows that one song selected
    /// in the list, clearing the missing-only filters if they would hide it.</summary>
    public void ShowFile(LibraryFileViewModel file)
    {
        var filter = _viewModel.Library.Filter;
        if (!filter.Matches(file))
        {
            filter.ShowMissingCoverOnly = false;
            filter.ShowMissingTagsOnly = false;
            filter.ShowMissingLyricsOnly = false;
        }
        _viewModel.Library.ViewMode = LibraryViewMode.List;
        Dispatcher.BeginInvoke(() =>
        {
            FileListGrid.SelectedItems.Clear();
            FileListGrid.SelectedItem = file;
            FileListGrid.ScrollIntoView(file);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FileListGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject source && IsInColumnHeader(source)) return;
        if (FileListGrid.SelectedItem is LibraryFileViewModel file)
            _viewModel.Playback.PlayFile(file);
    }

    private void FileListGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || FileListGrid.SelectedItem is not LibraryFileViewModel file) return;
        _viewModel.Playback.PlayFile(file);
        e.Handled = true;
    }

    private void FileTileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTileList.SelectedItem is LibraryFileViewModel file)
            _viewModel.Playback.PlayFile(file);
    }

    private static bool IsInColumnHeader(System.Windows.DependencyObject element)
    {
        for (var current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.DataGridColumnHeader) return true;
            if (current is DataGridRow) return false;
        }
        return false;
    }
}
