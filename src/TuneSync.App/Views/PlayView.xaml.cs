using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>
/// Interaction logic for PlayView.xaml
/// </summary>
public partial class PlayView : Page
{
    private readonly PlayPageViewModel _viewModel;
    private DateTime _followPausedUntil;

    public PlayView(PlayPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.Playback.PropertyChanged += Playback_PropertyChanged;
        // A new song's lyrics replace the old ones: start again from the top once the new lines
        // are laid out (resetting earlier is undone by the old lines' layout still being there).
        _viewModel.Playback.LyricsLines.CollectionChanged += (_, e) =>
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset) return;
            ResetLyricsScroll();
            Dispatcher.BeginInvoke(() =>
            {
                ResetLyricsScroll();
                FollowCurrentLyric();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) =>
        {
            SyncPlaylistToCurrentTrack();
            Dispatcher.BeginInvoke(FollowCurrentLyric, System.Windows.Threading.DispatcherPriority.Loaded);
        };

        // Track change: capture the outgoing song's screen just before it's replaced...
        _viewModel.Playback.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackViewModel.CurrentFile)) CaptureOutgoing();
        };
    }

    // --- crossfade between songs ---

    /// <summary>Freezes the current now-playing area into an image laid exactly over it, so the
    /// old song can fade out while the new one (already swapped in underneath) fades in.</summary>
    private void CaptureOutgoing()
    {
        if (!IsLoaded || !NowPlayingGrid.IsVisible || _viewModel.Playback.CurrentFile is null) return;
        var width = NowPlayingGrid.ActualWidth;
        var height = NowPlayingGrid.ActualHeight;
        if (width < 1 || height < 1) return;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(NowPlayingGrid);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(NowPlayingGrid), null, new System.Windows.Rect(0, 0, width, height));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        CrossfadeSnapshot.Source = bitmap;
        CrossfadeSnapshot.Width = width;
        CrossfadeSnapshot.Height = height;
        CrossfadeSnapshot.Margin = NowPlayingGrid.Margin;
        CrossfadeSnapshot.Visibility = System.Windows.Visibility.Visible;

        // ...then old out, new in (overlapping, so there's never an empty frame).
        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
        };
        fadeOut.Completed += (_, _) =>
        {
            if (CrossfadeSnapshot.Source != bitmap) return; // a newer change took over
            CrossfadeSnapshot.Visibility = System.Windows.Visibility.Collapsed;
            CrossfadeSnapshot.Source = null;
        };
        CrossfadeSnapshot.BeginAnimation(OpacityProperty, fadeOut);

        NowPlayingGrid.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650))
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
        NowPlayingGrid.Opacity = 1; // the value it settles on once the fade-in finishes (FillBehavior.Stop)
    }

    /// <summary>Unsynced lyrics: each line drops in a moment after the one above it, like a
    /// waterfall pouring down the page.</summary>
    private void PlainLine_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement line) return;
        var container = ItemsControl.ContainerFromElement(PlainLyricsItems, line);
        var index = container is null ? 0 : PlainLyricsItems.ItemContainerGenerator.IndexFromContainer(container);
        var delay = TimeSpan.FromMilliseconds(250 + Math.Min(Math.Max(index, 0), 45) * 70);

        var shift = new System.Windows.Media.TranslateTransform(0, -18);
        line.RenderTransform = shift;
        line.Opacity = 0;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        line.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650))
        {
            BeginTime = delay,
            EasingFunction = ease,
        });
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(650))
        {
            BeginTime = delay,
            EasingFunction = ease,
        });
    }

    // --- playlist search: moves the selection to matching songs, never filters the list ---

    private List<LibraryFileViewModel> _matches = [];
    private int _matchIndex = -1;

    /// <summary>Ctrl+F from anywhere in the player window.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindMatches();
        GoToMatch(_matches.Count > 0 ? 0 : -1);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindMatches(); // the list may have been re-sorted or rescanned since typing
                if (_matches.Count == 0) break;
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var current = PlaylistBox.SelectedItem as LibraryFileViewModel;
                var at = current is null ? -1 : _matches.IndexOf(current);
                GoToMatch(at < 0 ? (step > 0 ? 0 : _matches.Count - 1) : ((at + step) % _matches.Count + _matches.Count) % _matches.Count);
                e.Handled = true;
                break;
            case Key.Down:
                FocusSelectedSong();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchBox.Text = "";
                e.Handled = true;
                break;
        }
    }

    private void FindMatches()
    {
        var query = SearchBox.Text.Trim();
        _matches = query.Length == 0
            ? []
            : [.. _viewModel.Library.PlaylistView.Cast<LibraryFileViewModel>().Where(f => Matches(f, query))];
    }

    private static bool Matches(LibraryFileViewModel file, string query) =>
        file.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || file.Artist.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || file.Album.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || file.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void GoToMatch(int index)
    {
        _matchIndex = index;
        if (index < 0)
        {
            SearchCounter.Text = SearchBox.Text.Trim().Length == 0 ? "" : "없음";
            return;
        }

        var match = _matches[index];
        PlaylistBox.SelectedItem = match;
        PlaylistBox.ScrollIntoView(match);
        SearchCounter.Text = $"{index + 1}/{_matches.Count}";
    }

    /// <summary>↓ in the search box: continue with the keyboard in the list itself (Enter plays).</summary>
    private void FocusSelectedSong()
    {
        if (PlaylistBox.SelectedItem is not { } selected) return;
        PlaylistBox.ScrollIntoView(selected);
        PlaylistBox.UpdateLayout();
        if (PlaylistBox.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container)
            container.Focus();
    }

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
        PlaylistBox.SelectedItem = file;

        var playback = _viewModel.Playback;
        menu.Items.Clear();
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

    private static void RevealInExplorer(string path)
    {
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

    private static void CopyPath(string path)
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

    private static T? FindAncestor<T>(System.Windows.DependencyObject? element) where T : System.Windows.DependencyObject
    {
        for (var current = element; current is not null; current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : System.Windows.LogicalTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    private void SortButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var library = _viewModel.Library;
        var menu = new ContextMenu();
        foreach (var option in library.PlaylistSortOptions)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true, IsChecked = Equals(option.Value, library.PlaylistSort) };
            item.Click += (_, _) => library.PlaylistSort = option.Value;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var ascending = new MenuItem { Header = "오름차순", IsCheckable = true, IsChecked = !library.PlaylistSortDescending };
        ascending.Click += (_, _) => library.PlaylistSortDescending = false;
        var descending = new MenuItem { Header = "내림차순", IsCheckable = true, IsChecked = library.PlaylistSortDescending };
        descending.Click += (_, _) => library.PlaylistSortDescending = true;
        menu.Items.Add(ascending);
        menu.Items.Add(descending);
        OpenMenu(menu, (System.Windows.UIElement)sender);
    }

    private void GroupButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var library = _viewModel.Library;
        var menu = new ContextMenu();
        foreach (var option in library.PlaylistGroupOptions)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true, IsChecked = Equals(option.Value, library.PlaylistGroup) };
            item.Click += (_, _) => library.PlaylistGroup = option.Value;
            menu.Items.Add(item);
        }
        OpenMenu(menu, (System.Windows.UIElement)sender);
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
        if (e.Key != Key.Enter || PlaylistBox.SelectedItem is not LibraryFileViewModel file) return;
        _viewModel.Playback.PlayFile(file);
        e.Handled = true;
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackViewModel.CurrentLyricsIndex):
                FollowCurrentLyric();
                break;
            case nameof(PlaybackViewModel.CurrentFile):
                ResetLyricsScroll();
                _followPausedUntil = DateTime.MinValue;
                SyncPlaylistToCurrentTrack();
                break;
            case nameof(PlaybackViewModel.IsQueueVisible):
            case nameof(PlaybackViewModel.IsLyricsVisible):
                ApplyResponsiveLayout();
                break;
        }
    }

    private void ResetLyricsScroll()
    {
        LyricsScroller.Reset(LyricsScrollViewer);
        PlainLyricsScrollViewer.ScrollToTop(); // unsynced lyrics are read top to bottom by hand
        _followPausedUntil = DateTime.MinValue;
    }

    // --- responsive layout ---

    private bool? _compact;

    /// <summary>Adapts to the window width: the queue narrows first, then (when the lyrics no
    /// longer have room beside the big cover) the cover shrinks into a strip above the lyrics.</summary>
    private void ApplyResponsiveLayout()
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var playback = _viewModel.Playback;

        QueuePanel.Width = width < 1150 ? 300 : 360;
        var content = width - (playback.IsQueueVisible ? QueuePanel.Width : 0);
        var compact = playback.IsLyricsVisible && content < 760;

        // Lyrics hidden: the big cover alone, never wider than the space it has.
        NowPlayingPanel.MaxWidth = Math.Max(200, content - 64);

        if (_compact == compact) return;
        _compact = compact;

        NowPlayingGrid.Margin = compact ? new System.Windows.Thickness(24, 16, 24, 16) : new System.Windows.Thickness(48, 24, 32, 24);
        CompactHeader.Visibility = compact ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (compact) NowPlayingPanel.Visibility = System.Windows.Visibility.Collapsed;
        else NowPlayingPanel.ClearValue(VisibilityProperty);

        Grid.SetColumn(LyricsArea, compact ? 0 : 1);
        Grid.SetColumnSpan(LyricsArea, compact ? 2 : 1);
        Grid.SetRow(LyricsArea, compact ? 1 : 0);
        Grid.SetRowSpan(LyricsArea, compact ? 1 : 2);
        Dispatcher.BeginInvoke(FollowCurrentLyric, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OpenCurrentInTagManager_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel.Playback.CurrentFile is not { } file) return;
        (System.Windows.Application.Current.MainWindow as ShellWindow)?.ShowInTagManager(file);
    }

    private void FollowCurrentLyric()
    {
        if (!IsLoaded || DateTime.UtcNow < _followPausedUntil) return;
        LyricsScroller.CenterOn(LyricsScrollViewer, LyricsItems, _viewModel.Playback.CurrentLyricsIndex);
    }

    /// <summary>The user is reading ahead/back with the wheel: stop the glide and leave the view
    /// where they put it for a few seconds before auto-follow resumes.</summary>
    private void LyricsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        LyricsScroller.Stop(LyricsScrollViewer);
        _followPausedUntil = DateTime.UtcNow.AddSeconds(4);
    }

    private void LyricLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: LyricsLineViewModel line }) return;
        _followPausedUntil = DateTime.MinValue;
        _viewModel.Playback.SeekTo(line.Time);
    }

    /// <summary>Keeps the playlist highlight on the playing track, including when it changes via
    /// next/previous, media keys or auto-advance rather than a click in this list.</summary>
    private void SyncPlaylistToCurrentTrack()
    {
        if (_viewModel.Playback.CurrentFile is not { } current) return;
        PlaylistBox.SelectedItem = current;
        // After layout: right after a sort/rescan or at startup the list may not be measured yet.
        if (IsLoaded)
            Dispatcher.BeginInvoke(() => PlaylistBox.ScrollIntoView(current), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
