using System.IO;
using System.Linq;
using System.Windows;
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
        var library = _viewModel.Library;
        library.ClearScopeCommand.Execute(null);
        if (!library.Filter.Matches(file)) library.Filter.Clear();
        _viewModel.Library.ViewMode = LibraryViewMode.List;
        Dispatcher.BeginInvoke(() =>
        {
            FileListGrid.SelectedItems.Clear();
            FileListGrid.SelectedItem = file;
            FileListGrid.ScrollIntoView(file);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Opened from the queue with songs picked ("태그 편집"): the list shows just those
    /// songs, all selected - one song opens its own editor, several the batch editor. The bar
    /// above the list says so and leads back to the whole library.</summary>
    public void ShowFiles(IReadOnlyList<LibraryFileViewModel> files)
    {
        if (files.Count == 0) return;
        var library = _viewModel.Library;
        library.Filter.Clear();
        library.SetScope(files, files.Count == 1 ? "재생 대기열에서 고른 곡" : $"재생 대기열에서 고른 {files.Count}곡");
        library.ViewMode = LibraryViewMode.List;
        Dispatcher.BeginInvoke(() =>
        {
            FileListGrid.SelectAll();
            if (FileListGrid.Items.Count > 0) FileListGrid.ScrollIntoView(FileListGrid.Items[0]);
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

    // ---- Tag editor ----

    /// <summary>Ctrl+S saves the editor's changes; Alt+↑/↓ moves to the previous/next song while
    /// typing in the editor (edits wait on each song, so nothing is lost).</summary>
    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var editor = _viewModel.Editor;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (editor.SaveCommand.CanExecute(null)) editor.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down
            && _viewModel.Library.ViewMode == LibraryViewMode.List && FileListGrid.Items.Count > 0)
        {
            var index = FileListGrid.SelectedIndex;
            var next = Math.Clamp(index + (key == Key.Down ? 1 : -1), 0, FileListGrid.Items.Count - 1);
            if (index < 0) next = 0;
            FileListGrid.SelectedItems.Clear();
            FileListGrid.SelectedIndex = next;
            FileListGrid.ScrollIntoView(FileListGrid.SelectedItem);
            e.Handled = true;
        }
    }

    /// <summary>Esc in a tag field puts it back the way it was (when it was changed).</summary>
    /// <summary>A field's box: takes the caret when its field asks (ticking 적용 on a field whose
    /// songs differ, with no value typed yet).</summary>
    private void FieldBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagFieldViewModel field } box || box.Tag is "hooked") return;
        box.Tag = "hooked";
        field.FocusRequested += () =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            Keyboard.Focus(box is Wpf.Ui.Controls.AutoSuggestBox suggest ? FindChild<TextBox>(suggest) ?? (IInputElement)box : box);
        };
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>A song's lyrics comparison in its own, larger window (the review panel is narrow).</summary>
    private void CompareWide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReviewFileViewModel item }) return;
        var page = new Page();
        page.Resources["CompareScroll"] = new Style(typeof(ScrollViewer));         // no height cap: the window decides
        page.Resources["CompareWideVisibility"] = Visibility.Collapsed;             // already wide
        page.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        page.Content = new ContentControl
        {
            Content = item,
            ContentTemplate = (DataTemplate)FindResource("LyricsCompareTemplate"),
            Margin = new Thickness(20, 4, 20, 20),
            Focusable = false,
        };
        new ToolWindow(page, item.CompareTitle, 960, 780) { Owner = Window.GetWindow(this) }.Show();
    }

    private void Field_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not FrameworkElement { DataContext: TagFieldViewModel field }) return;
        if (!field.IsChanged && !field.HasError) return;
        field.RevertCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>An auto-fill result's covers, large: the current one (full size, read from the
    /// file) next to the proposed one.</summary>
    private void AutoFillCover_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoMatchItemViewModel item }) return;
        var proposed = item.Proposal.ProposedCoverBytes;
        var items = new List<CoverPreviewItem>();
        if (item.Proposal.File.HasCover)
            items.Add(new CoverPreviewItem("현재 커버", () => Task.Run(() => Core.Services.CoverArtService.GetCover(item.Path))));
        items.Add(new CoverPreviewItem("새 커버", () => Task.FromResult(proposed)));
        CoverPreviewWindow.Open(Window.GetWindow(this), [.. items]);
        e.Handled = true;
    }

    /// <summary>The editor's cover tab: the cover large (and, with a new one waiting to be saved,
    /// the current one beside it).</summary>
    private void EditorCover_Click(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Editor.CoverPreviewItems() is not { Length: > 0 } items) return;
        CoverPreviewWindow.Open(Window.GetWindow(this), items);
    }

    // 커버 찾기 gallery: thumbnails are downloaded only for tiles that are (nearly) on screen.
    private bool _thumbCheckQueued;

    private void CoverCandidate_Loaded(object sender, RoutedEventArgs e) => QueueThumbnailCheck();

    private void CoverCandidate_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueThumbnailCheck();

    private void CoverScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => QueueThumbnailCheck();

    private void QueueThumbnailCheck()
    {
        if (_thumbCheckQueued) return;
        _thumbCheckQueued = true;
        // After layout, so the tiles have their positions.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _thumbCheckQueued = false;
            if (!CoverScroll.IsVisible) return;
            var view = new Rect(0, 0, CoverScroll.ViewportWidth, CoverScroll.ViewportHeight);
            view.Inflate(0, 120); // a row ahead, so scrolling finds them ready
            foreach (var item in CoverCandidateList.Items.OfType<CoverCandidateViewModel>())
            {
                if (CoverCandidateList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement tile || !tile.IsVisible) continue;
                var bounds = tile.TransformToAncestor(CoverScroll).TransformBounds(new Rect(tile.RenderSize));
                if (bounds.IntersectsWith(view)) item.EnsureThumbnail();
            }
        });
    }

    private static string? DroppedImage(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(TagEditorViewModel.IsImageFile) : null;

    private void CoverDrop_DragOver(object sender, DragEventArgs e)
    {
        var ok = _viewModel.Editor.HasSelection && DroppedImage(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        CoverDropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void CoverDrop_DragLeave(object sender, DragEventArgs e) => CoverDropHighlight.Visibility = Visibility.Collapsed;

    private void CoverDrop_Drop(object sender, DragEventArgs e)
    {
        CoverDropHighlight.Visibility = Visibility.Collapsed;
        if (_viewModel.Editor.HasSelection && DroppedImage(e) is { } path) _viewModel.Editor.SetCoverFromFile(path);
        e.Handled = true;
    }
}
