using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// The play list: playing and removing songs, its menus and view modes, and keeping the playing
// song in reach.
public partial class PlayView
{
    /// <summary>The ▶ shown over a queue row's thumbnail on hover.</summary>
    private void PlayItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: LibraryFileViewModel file }) return;
        PlaylistBox.SelectedItem = file;
        _viewModel.Playback.PlayFile(file);
        e.Handled = true;
    }

    // --- right-click menu on a queue row ---

    private void PlaylistBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Only on a song row (not on a group header or the empty area below the list).
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as System.Windows.DependencyObject);
        if (row?.DataContext is not LibraryFileViewModel file || PlaylistBox.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }
        // Right-clicking inside a multiple selection keeps it; elsewhere it selects that row.
        if (!PlaylistBox.SelectedItems.Contains(file)) PlaylistBox.SelectedItem = file;
        var selected = SelectedSongs();

        var playback = _viewModel.Playback;
        menu.Items.Clear();
        if (selected.Count > 1)
        {
            menu.Items.Add(MenuItem($"선택한 {selected.Count}곡 다음에 재생", Wpf.Ui.Controls.SymbolRegular.TextBulletListAdd24, () =>
            {
                // In list order, right after the current song.
                foreach (var song in Enumerable.Reverse(selected.OrderBy(s => PlaylistBox.Items.IndexOf(s))).Where(s => !s.IsNowPlaying))
                    playback.PlayAfterCurrent(song);
            }));
            menu.Items.Add(MenuItem($"선택한 {selected.Count}곡 나중에 재생", Wpf.Ui.Controls.SymbolRegular.AppsListDetail24, () =>
            {
                foreach (var song in selected.OrderBy(s => PlaylistBox.Items.IndexOf(s)).Where(s => !s.IsNowPlaying))
                    playback.PlayLater(song);
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem($"목록에서 빼기 ({selected.Count}곡)", Wpf.Ui.Controls.SymbolRegular.Delete24,
                () => _ = RemoveSelectedFromListAsync(), "Delete"));
            return;
        }
        menu.Items.Add(MenuItem("재생", Wpf.Ui.Controls.SymbolRegular.Play24, () => playback.PlayFile(file), "Enter"));
        if (file.IsUpNext)
        {
            menu.Items.Add(MenuItem("다음에 재생 취소", Wpf.Ui.Controls.SymbolRegular.Dismiss24, () => playback.CancelUpNext(file)));
        }
        else if (!file.IsNowPlaying)
        {
            menu.Items.Add(MenuItem("다음에 재생", Wpf.Ui.Controls.SymbolRegular.TextBulletListAdd24, () => playback.PlayAfterCurrent(file)));
            menu.Items.Add(MenuItem("나중에 재생", Wpf.Ui.Controls.SymbolRegular.AppsListDetail24, () => playback.PlayLater(file)));
        }
        if (playback.HasUpNext)
            menu.Items.Add(MenuItem("다음에 재생 목록 비우기", Wpf.Ui.Controls.SymbolRegular.Delete24, playback.ClearUpNext));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("태그 관리에서 보기", Wpf.Ui.Controls.SymbolRegular.Tag24, () =>
            (System.Windows.Window.GetWindow(this) as ShellWindow
             ?? System.Windows.Application.Current.MainWindow as ShellWindow)?.ShowInTagManager(file)));
        menu.Items.Add(MenuItem("파일 위치 열기", Wpf.Ui.Controls.SymbolRegular.FolderOpen24, () => RevealInExplorer(file.Path)));
        menu.Items.Add(MenuItem("파일 경로 복사", Wpf.Ui.Controls.SymbolRegular.Copy24, () => CopyPath(file.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("목록에서 빼기", Wpf.Ui.Controls.SymbolRegular.Delete24, () => _ = RemoveSelectedFromListAsync(), "Delete"));
    }

    private static MenuItem MenuItem(string header, Wpf.Ui.Controls.SymbolRegular icon, Action action, string? gesture = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = icon },
            InputGestureText = gesture ?? "",
        };
        item.Click += (_, _) => action();
        return item;
    }

    internal static void RevealInExplorer(string path)
    {
        if (TuneSync.Core.Models.RemotePath.IsRemote(path))
        {
            Toast.Info("네트워크 위치의 곡입니다", "서버에 있는 파일이라 탐색기로 열 수 없습니다. 태그·가사·커버는 GoTune에서 바로 고칠 수 있습니다.");
            return;
        }
        try
        {
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                Toast.Warning("파일을 찾을 수 없습니다", path);
        }
        catch (Exception ex)
        {
            Toast.Error("파일 위치를 열 수 없습니다", ex.Message);
        }
    }

    internal static void CopyPath(string path)
    {
        try
        {
            System.Windows.Clipboard.SetText(path);
            Toast.Info("경로를 복사했습니다", path);
        }
        catch (Exception ex)
        {
            Toast.Error("복사하지 못했습니다", ex.Message); // clipboard briefly held by another app
        }
    }

    private void SortButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var library = _viewModel.Library;
        var menu = new ContextMenu();
        foreach (var option in library.PlaylistSortOptions)
        {
            menu.Items.Add(CheckItem(option.Label, Equals(option.Value, library.PlaylistSort), () => library.PlaylistSort = option.Value));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("오름차순", !library.PlaylistSortDescending, () => library.PlaylistSortDescending = false));
        menu.Items.Add(CheckItem("내림차순", library.PlaylistSortDescending, () => library.PlaylistSortDescending = true));
        OpenMenu(menu, (System.Windows.UIElement)sender);
    }

    private void GroupButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var library = _viewModel.Library;
        var menu = new ContextMenu();
        foreach (var option in library.PlaylistGroupOptions)
        {
            menu.Items.Add(CheckItem(option.Label, Equals(option.Value, library.PlaylistGroup), () => library.PlaylistGroup = option.Value));
        }
        OpenMenu(menu, (System.Windows.UIElement)sender);
    }

    /// <summary>A menu entry with a check mark on the choice in effect.</summary>
    private static MenuItem CheckItem(string header, bool isChecked, Action choose)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => choose();
        return item;
    }

    private static void OpenMenu(ContextMenu menu, System.Windows.UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is LibraryFileViewModel file)
            _viewModel.Playback.PlayFile(file);
    }

    private void PlaylistBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            _ = RemoveSelectedFromListAsync();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || PlaylistBox.SelectedItem is not LibraryFileViewModel file) return;
        _viewModel.Playback.PlayFile(file);
        e.Handled = true;
    }

    // ---- view modes: standard rows, dense rows, cover grid ----

    private readonly DataTemplate _rowTemplate;
    private readonly Style _rowStyle;
    private readonly ItemsPanelTemplate _rowPanel;
    private ItemsPanelTemplate? _tilePanelTemplate;
    private VirtualizingTilePanel? _tilePanel;

    private void ApplyQueueView()
    {
        var mode = _viewModel.Playback.QueueViewMode;
        var keep = PlaylistBox.SelectedItem;
        _tilePanel = null;
        // Rows leave room for the overlay scrollbar themselves; tiles need it here.
        PlaylistBox.Padding = mode == QueueViewMode.Grid ? new Thickness(0, 0, 12, 0) : new Thickness(0);
        switch (mode)
        {
            case QueueViewMode.Grid:
                _tilePanelTemplate ??= new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel)));
                PlaylistBox.ItemsPanel = _tilePanelTemplate;
                PlaylistBox.ItemContainerStyle = (Style)FindResource("QueueTileStyle");
                PlaylistBox.ItemTemplate = (DataTemplate)FindResource("QueueTileTemplate");
                Dispatcher.BeginInvoke(() =>
                {
                    _tilePanel = FindDescendant<VirtualizingTilePanel>(PlaylistBox);
                    if (_tilePanel is not null) _tilePanel.TileSize = _viewModel.Playback.QueueTileSize;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            case QueueViewMode.Compact:
                PlaylistBox.ItemsPanel = _rowPanel;
                PlaylistBox.ItemContainerStyle = (Style)FindResource("QueueCompactItemStyle");
                PlaylistBox.ItemTemplate = (DataTemplate)FindResource("QueueCompactTemplate");
                break;
            default:
                PlaylistBox.ItemsPanel = _rowPanel;
                PlaylistBox.ItemContainerStyle = _rowStyle;
                PlaylistBox.ItemTemplate = _rowTemplate;
                break;
        }
        ViewButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = mode switch
            {
                QueueViewMode.Grid => Wpf.Ui.Controls.SymbolRegular.Grid24,
                QueueViewMode.Compact => Wpf.Ui.Controls.SymbolRegular.TextBulletListLtr24,
                _ => Wpf.Ui.Controls.SymbolRegular.AppsList24,
            },
        };
        if (keep is not null)
            Dispatcher.BeginInvoke(() => PlaylistBox.ScrollIntoView(keep), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ViewButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var playback = _viewModel.Playback;
        var menu = new ContextMenu();
        void Option(string label, QueueViewMode mode, Wpf.Ui.Controls.SymbolRegular icon)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = playback.QueueViewMode == mode,
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = icon },
            };
            item.Click += (_, _) => playback.QueueViewMode = mode;
            menu.Items.Add(item);
        }
        Option("기본", QueueViewMode.Standard, Wpf.Ui.Controls.SymbolRegular.AppsList24);
        Option("촘촘한 목록", QueueViewMode.Compact, Wpf.Ui.Controls.SymbolRegular.TextBulletListLtr24);
        Option("그리드", QueueViewMode.Grid, Wpf.Ui.Controls.SymbolRegular.Grid24);
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("제목 먼저", !playback.QueueArtistFirst, () => playback.QueueArtistFirst = false));
        menu.Items.Add(CheckItem("아티스트 먼저", playback.QueueArtistFirst, () => playback.QueueArtistFirst = true));
        if (playback.QueueViewMode == QueueViewMode.Grid)
        {
            menu.Items.Add(new Separator());
            var slider = new Slider
            {
                Minimum = 96,
                Maximum = 200,
                Value = playback.QueueTileSize,
                Width = 150,
                SmallChange = 4,
                LargeChange = 16,
                ToolTip = "Ctrl + 마우스 휠로도 바꿀 수 있습니다",
            };
            slider.ValueChanged += (_, args) => playback.QueueTileSize = args.NewValue;
            var size = new StackPanel();
            size.Children.Add(new TextBlock { Text = "타일 크기", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            size.Children.Add(slider);
            menu.Items.Add(new MenuItem { Header = size, StaysOpenOnClick = true });
        }
        OpenMenu(menu, (System.Windows.UIElement)sender);
    }

    /// <summary>Ctrl + wheel over the grid: bigger or smaller tiles.</summary>
    private void PlaylistBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _viewModel.Playback.QueueViewMode != QueueViewMode.Grid) return;
        _viewModel.Playback.QueueTileSize += e.Delta > 0 ? 8 : -8;
        e.Handled = true;
    }

    private List<LibraryFileViewModel> SelectedSongs() => [.. PlaylistBox.SelectedItems.OfType<LibraryFileViewModel>()];

    private void PlaylistBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.Library.QueueSelection = SelectedSongs();

    /// <summary>Delete / 목록에서 빼기: the selected songs leave the list; the row after them gets
    /// the selection, so pressing Delete again keeps going down the list.</summary>
    private async Task RemoveSelectedFromListAsync()
    {
        var songs = SelectedSongs();
        if (songs.Count == 0) return;
        var view = PlaylistBox.Items;
        var lastIndex = songs.Max(s => view.IndexOf(s));
        var leaving = songs.ToHashSet();
        var next = view.OfType<LibraryFileViewModel>().Skip(lastIndex + 1).FirstOrDefault(f => !leaving.Contains(f))
                   ?? view.OfType<LibraryFileViewModel>().Take(lastIndex).LastOrDefault(f => !leaving.Contains(f));
        await _viewModel.Library.RemoveFromListAsync(songs);
        if (next is null || !PlaylistBox.Items.Contains(next)) return;
        PlaylistBox.SelectedItem = next;
        PlaylistBox.ScrollIntoView(next);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (PlaylistBox.ItemContainerGenerator.ContainerFromItem(next) is ListBoxItem row) row.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // --- "지금 재생 중": the playing song is scrolled out of view in the queue ---

    private void PlaylistBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateBackToCurrent();
        UpdateStickyGroup();
    }

    /// <summary>The queue row of the playing song: the same object, or - after a rescan rebuilt
    /// the rows - the row for the same file. Null when the queue doesn't have it (cleared,
    /// removed, or hidden by the search).</summary>
    private LibraryFileViewModel? CurrentRow()
    {
        if (_viewModel.Playback.CurrentFile is not { } current) return null;
        if (PlaylistBox.Items.Contains(current)) return current;
        foreach (var item in PlaylistBox.Items)
            if (item is LibraryFileViewModel row && string.Equals(row.Path, current.Path, StringComparison.OrdinalIgnoreCase)) return row;
        return null;
    }

    private void UpdateBackToCurrent()
    {
        var show = false;
        if (_searchHit is null && CurrentRow() is { } current && PlaylistBox.ActualHeight > 0)
        {
            show = true;
            if (PlaylistBox.ItemContainerGenerator.ContainerFromItem(current) is System.Windows.FrameworkElement row && row.IsVisible)
            {
                var top = row.TransformToAncestor(PlaylistBox).Transform(new System.Windows.Point(0, 0)).Y;
                show = top + row.ActualHeight < 0 || top > PlaylistBox.ActualHeight - 8;
            }
        }
        var visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (BackToCurrentButton.Visibility == visibility) return;
        BackToCurrentButton.Visibility = visibility;
        if (show) FadeSlideIn(BackToCurrentButton, 0, 12);
    }

    private void BackToCurrent_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentRow() is not { } current) return;
        PlaylistBox.SelectedItem = current;
        PlaylistBox.ScrollIntoView(current);
    }

    /// <summary>The playback bar's cover was clicked: bring the playing song into view in the
    /// list and blink it, so the eye finds it. <paramref name="openQueue"/> also opens a hidden
    /// list and clears a search (the card's "재생 목록에서 보기"); otherwise a hidden list stays hidden.</summary>
    public void RevealCurrent(bool openQueue)
    {
        var playback = _viewModel.Playback;
        if (openQueue)
        {
            playback.IsQueueVisible = true;
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        }
        if (!playback.IsQueueVisible) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (CurrentRow() is not { } current) return;
            PlaylistBox.SelectedItem = current;
            PlaylistBox.ScrollIntoView(current);
            Dispatcher.BeginInvoke(() =>
            {
                UpdateBackToCurrent();
                if (PlaylistBox.ItemContainerGenerator.ContainerFromItem(current) is not System.Windows.UIElement row) return;
                row.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(170))
                {
                    AutoReverse = true,
                    RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2),
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                    EasingFunction = new System.Windows.Media.Animation.SineEase(),
                });
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Opens the list (if hidden) and searches it, e.g. for the playing song's album.</summary>
    public void SearchQueue(string text)
    {
        _viewModel.Playback.IsQueueVisible = true;
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Text = text;
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Keeps the playlist highlight on the playing track, including when it changes via
    /// next/previous, media keys or auto-advance rather than a click in this list.</summary>
    private void SyncPlaylistToCurrentTrack()
    {
        if (_viewModel.Playback.CurrentFile is not { } current) return;
        // Several rows picked by hand (to remove or queue them): a song change mustn't undo that.
        if (PlaylistBox.SelectedItems.Count > 1) return;
        PlaylistBox.SelectedItem = current;
        // After layout: right after a sort/rescan or at startup the list may not be measured yet.
        if (IsLoaded)
            Dispatcher.BeginInvoke(() =>
            {
                PlaylistBox.ScrollIntoView(current);
                UpdateBackToCurrent();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
