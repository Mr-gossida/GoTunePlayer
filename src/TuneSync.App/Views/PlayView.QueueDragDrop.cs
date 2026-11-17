using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// Drag and drop on the play list: moving songs to a new place, and adding folders or files
// dropped from Explorer.
public partial class PlayView
{
    // ---- dragging songs to a new place ----

    private const string QueueSongsFormat = "TuneSync.QueueSongs";
    private Point _dragStart;
    private LibraryFileViewModel? _dragSong;
    private LibraryFileViewModel? _clickToSelect;

    private LibraryFileViewModel? SongAt(object? source) =>
        FindAncestor<ListBoxItem>(source as System.Windows.DependencyObject)?.DataContext as LibraryFileViewModel;

    private void PlaylistBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // a group's heading: its own handler folds the group (GroupHeading_PreviewMouseLeftButtonDown)
        if (IsInGroupHeading(e.OriginalSource as System.Windows.DependencyObject)) return;
        _dragStart = e.GetPosition(PlaylistBox);
        _dragSong = SongAt(e.OriginalSource);
        _clickToSelect = null;
        // Pressing on one of several selected songs: keep the selection (it's probably about to be
        // dragged); a plain click still selects just that song when the button comes up.
        if (_dragSong is not null && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None
            && PlaylistBox.SelectedItems.Count > 1 && PlaylistBox.SelectedItems.Contains(_dragSong))
        {
            _clickToSelect = _dragSong;
            e.Handled = true;
            PlaylistBox.Focus();
        }
    }

    private void PlaylistBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_clickToSelect is { } song) PlaylistBox.SelectedItem = song;
        _clickToSelect = null;
        _dragSong = null;
    }

    private void PlaylistBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSong is not { } grabbed || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(PlaylistBox) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance * 2
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;

        var order = PlaylistBox.Items;
        var songs = PlaylistBox.SelectedItems.Contains(grabbed)
            ? SelectedSongs().OrderBy(s => order.IndexOf(s)).ToList()
            : [grabbed];
        _dragSong = null;
        _clickToSelect = null;
        try
        {
            System.Windows.DragDrop.DoDragDrop(PlaylistBox, new System.Windows.DataObject(QueueSongsFormat, songs), System.Windows.DragDropEffects.Move);
        }
        finally
        {
            HideDropTarget();
        }
    }

    private void PlaylistBox_DragLeave(object sender, System.Windows.DragEventArgs e) => HideDropTarget();

    /// <summary>Where a drop at <paramref name="point"/> puts the songs: before which song (null:
    /// at the end), and where to draw the line showing it.</summary>
    private (LibraryFileViewModel? Before, Rect Line) DropTarget(Point point)
    {
        var grid = _viewModel.Playback.QueueViewMode == QueueViewMode.Grid;
        var items = PlaylistBox.Items;
        var hit = PlaylistBox.InputHitTest(point) as System.Windows.DependencyObject;
        var row = FindAncestor<ListBoxItem>(hit);
        if (row?.DataContext is not LibraryFileViewModel song)
        {
            // Below the last song (or in a gap): the end of the list.
            var last = items.Count > 0 ? PlaylistBox.ItemContainerGenerator.ContainerFromIndex(items.Count - 1) as ListBoxItem : null;
            if (last is null) return (null, Rect.Empty);
            var b = last.TransformToAncestor(PlaylistBox).TransformBounds(new Rect(last.RenderSize));
            return (null, grid ? new Rect(b.Right - 2, b.Top + 6, 3, b.Height - 12) : new Rect(b.Left + 4, b.Bottom - 1.5, b.Width - 20, 3));
        }
        var bounds = row.TransformToAncestor(PlaylistBox).TransformBounds(new Rect(row.RenderSize));
        var index = items.IndexOf(song);
        var after = grid ? point.X > bounds.Left + bounds.Width / 2 : point.Y > bounds.Top + bounds.Height / 2;
        var before = after ? (index + 1 < items.Count ? items[index + 1] as LibraryFileViewModel : null) : song;
        var line = grid
            ? new Rect((after ? bounds.Right : bounds.Left) - 1.5, bounds.Top + 6, 3, bounds.Height - 12)
            : new Rect(bounds.Left + 4, (after ? bounds.Bottom : bounds.Top) - 1.5, bounds.Width - 20, 3);
        return (before, line);
    }

    private void ShowDropTarget(Point point)
    {
        var (_, line) = DropTarget(point);
        if (line.IsEmpty)
        {
            HideDropTarget();
            return;
        }
        Canvas.SetLeft(DropLine, line.Left);
        Canvas.SetTop(DropLine, line.Top);
        DropLine.Width = Math.Max(3, line.Width);
        DropLine.Height = Math.Max(3, line.Height);
        DropLine.Visibility = System.Windows.Visibility.Visible;
    }

    private void HideDropTarget() => DropLine.Visibility = System.Windows.Visibility.Collapsed;

    /// <summary>Near the top or bottom edge while dragging: the list scrolls that way.</summary>
    private void AutoScrollWhileDragging(Point point)
    {
        if (FindDescendant<ScrollViewer>(PlaylistBox) is not { } scroller) return;
        const double edge = 36;
        if (point.Y < edge) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - (edge - point.Y) / 2 - 4);
        else if (point.Y > PlaylistBox.ActualHeight - edge)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + (point.Y - (PlaylistBox.ActualHeight - edge)) / 2 + 4);
    }

    // --- dropping folders/files from Explorer onto the list adds them ---

    private static string[] DroppedPaths(System.Windows.DragEventArgs e) =>
        e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths ? paths : [];

    private void PlaylistBox_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(QueueSongsFormat))
        {
            ShowDropTarget(e.GetPosition(PlaylistBox));
            AutoScrollWhileDragging(e.GetPosition(PlaylistBox));
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        var ok = DroppedPaths(e).Any(p => System.IO.Directory.Exists(p) || TuneSync.Core.Services.TagService.IsSupported(p));
        e.Effects = ok ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void PlaylistBox_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(QueueSongsFormat) is List<LibraryFileViewModel> moving)
        {
            var (before, _) = DropTarget(e.GetPosition(PlaylistBox));
            HideDropTarget();
            // Dropped onto one of the songs being moved: they go before the next one that stays.
            if (before is not null && moving.Contains(before))
            {
                var items = PlaylistBox.Items.OfType<LibraryFileViewModel>().ToList();
                before = items.Skip(items.IndexOf(before)).FirstOrDefault(f => !moving.Contains(f));
            }
            _viewModel.Library.MoveInQueue(moving, before);
            PlaylistBox.SelectedItems.Clear();
            foreach (var song in moving) PlaylistBox.SelectedItems.Add(song);
            PlaylistBox.ScrollIntoView(moving[0]);
            e.Handled = true;
            return;
        }
        var entries = DroppedPaths(e)
            .Select(p => System.IO.Directory.Exists(p)
                ? new TuneSync.Core.Models.LibraryFolderEntry { Path = p, IncludeSubfolders = true }
                : System.IO.File.Exists(p) && TuneSync.Core.Services.TagService.IsSupported(p)
                    ? new TuneSync.Core.Models.LibraryFolderEntry { Path = p, IsFile = true }
                    : null)
            .OfType<TuneSync.Core.Models.LibraryFolderEntry>()
            .ToList();
        if (entries.Count == 0)
        {
            Toast.Info("추가할 수 없습니다", "음악 폴더나 음악 파일(mp3, flac, m4a 등)을 끌어다 놓아 주세요.");
            return;
        }
        await _viewModel.Library.AddSourcesAsync(entries);
    }
}
