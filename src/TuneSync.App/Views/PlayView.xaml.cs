using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

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
        // The queue emptied or changed (folders removed, a rescan, a search): the "지금 재생 중"
        // button only makes sense while the playing song is in it.
        ((System.Collections.Specialized.INotifyCollectionChanged)PlaylistBox.Items).CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(UpdateBackToCurrent, System.Windows.Threading.DispatcherPriority.Loaded);
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
        SizeChanged += (_, _) =>
        {
            ClampQueueWidth();
            ApplyResponsiveLayout(resizing: true);
            FitNowPlayingPanel();
        };

        // The play list's look: the row template/style/panel in the XAML are the standard view.
        _rowTemplate = PlaylistBox.ItemTemplate;
        _rowStyle = PlaylistBox.ItemContainerStyle;
        _rowPanel = PlaylistBox.ItemsPanel;
        QueuePanel.Width = _viewModel.Playback.QueueWidth;
        ApplyQueueView();
        Loaded += (_, _) =>
        {
            SyncPlaylistToCurrentTrack();
            Dispatcher.BeginInvoke(FollowCurrentLyric, System.Windows.Threading.DispatcherPriority.Loaded);
        };

        // Track change: capture the outgoing song's screen just before it's replaced...
        _viewModel.Playback.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName != nameof(PlaybackViewModel.CurrentFile)) return;
            CaptureOutgoing();
            // The crossfade covers this change; don't also glide/reveal underneath it.
            _trackChanging = true;
            Dispatcher.BeginInvoke(() => _trackChanging = false, System.Windows.Threading.DispatcherPriority.Background);
        };
        Loaded += (_, _) =>
        {
            ApplyIntroStage(animate: false);
            FitNowPlayingPanel();
        };
    }

    // --- intro stage: hero card during the opening, then a dramatic hand-off to the lyrics ---

    private bool _hero;
    private bool _lyricsHeldBack;
    private bool _trackChanging;

    /// <summary>During a long instrumental opening the cover card moves to the middle of the
    /// screen (wide layout) or a big equalizer pulse stands in for the lyrics (narrow layout).
    /// When the singing is about to start, the cover glides back to its place and the lyrics rise
    /// in out of a blur.</summary>
    private void ApplyIntroStage(bool animate)
    {
        var playback = _viewModel.Playback;
        var intro = playback.IsIntroVisible;
        var hero = intro && playback.IsLyricsVisible && _compact != true;
        animate = animate && IsLoaded && !_trackChanging;
        // Only while an intro is on (it holds looping animations) - and, after it, until its
        // fade-out (the style's exit animation) has played instead of vanishing at once.
        var pulse = _compact == true && intro;
        if (pulse) IntroPulse.Visibility = System.Windows.Visibility.Visible;
        else if (animate && IntroPulse.IsVisible) CollapseAfter(IntroPulse, TimeSpan.FromMilliseconds(550), () => _viewModel.Playback.IsIntroVisible);
        else IntroPulse.Visibility = System.Windows.Visibility.Collapsed;

        // Measured on the cover, which sits at the card's top-left corner: the card's width and
        // its cover's size needn't match (a short window gets a smaller cover), and the glide
        // has to scale the card by how the cover changes.
        System.Windows.Rect? before = animate && _hero && !intro && NowPlayingPanel.IsVisible ? BoundsInLayer(BigCover) : null;
        ShowHeroGauge(intro && playback.ShowIntroGauge, animate);

        if (hero != _hero)
        {
            var from = before;
            _hero = hero;
            if (hero)
            {
                Grid.SetColumnSpan(NowPlayingPanel, 2);
                NowPlayingPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                NowPlayingPanel.Margin = new System.Windows.Thickness(0);
                FitNowPlayingPanel();
            }
            else
            {
                NowPlayingPanel.ClearValue(Grid.ColumnSpanProperty);
                NowPlayingPanel.ClearValue(HorizontalAlignmentProperty);
                NowPlayingPanel.ClearValue(MarginProperty);
                FitNowPlayingPanel();
                if (from is { } start)
                {
                    UpdateLayout();
                    // Cover, song info and spectrum travel together, as one card. Unhurried: the
                    // intro ends a few seconds before the singing, so there's time.
                    GlideElement(NowPlayingPanel, start, BoundsInLayer(BigCover), TimeSpan.FromMilliseconds(1400));
                }
            }
        }

        if (intro != _lyricsHeldBack)
        {
            _lyricsHeldBack = intro;
            if (intro)
            {
                LyricsScrollViewer.BeginAnimation(OpacityProperty, null);
                LyricsScrollViewer.Opacity = 0;
            }
            else if (animate)
            {
                RevealLyrics(LyricsScrollViewer, delay: TimeSpan.FromMilliseconds(450));
            }
            else
            {
                LyricsScrollViewer.BeginAnimation(OpacityProperty, null);
                LyricsScrollViewer.Opacity = 1;
            }
        }
    }

    /// <summary>The cover card at its usual width (340 as the intro's centerpiece, 300 beside the
    /// lyrics, 400 alone). When the window is too short for all of it, the cover gets smaller
    /// (the text keeps its width, so it doesn't wrap onto more lines), and if that's still not
    /// enough the spectrum and then the quality/album lines step aside - nothing is cut off.</summary>
    private void FitNowPlayingPanel()
    {
        var full = _hero ? 340.0 : _viewModel.Playback.IsLyricsVisible ? 300.0 : 400.0;
        if (_hero) NowPlayingPanel.Width = full;
        else NowPlayingPanel.ClearValue(WidthProperty); // the style's width

        var visible = System.Windows.Visibility.Visible;
        void Cover(double size)
        {
            BigCover.Width = size;
            BigCover.Height = size;
        }
        Cover(full);
        PanelSpectrum.Visibility = visible;
        PanelQuality.Visibility = visible;
        PanelDetails.Visibility = visible;

        var room = NowPlayingGrid.ActualHeight;
        if (room <= 0 || NowPlayingPanel.Visibility != visible) return;
        double Over()
        {
            NowPlayingPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            return NowPlayingPanel.DesiredSize.Height - room;
        }

        var over = Over();
        if (over <= 0.5) return;
        const double smallestCover = 120;
        Cover(Math.Max(smallestCover, full - over - 2));
        if ((over = Over()) <= 0.5) return;

        // Still too tall with the smallest cover: drop the extras, least important first.
        foreach (var extra in new System.Windows.UIElement[] { PanelSpectrum, PanelQuality, PanelDetails })
        {
            extra.Visibility = System.Windows.Visibility.Collapsed;
            Cover(smallestCover);
            if ((over = Over()) <= 0.5)
            {
                // Give the cover back what the extra freed beyond what was needed.
                Cover(Math.Min(full, smallestCover - over - 2));
                return;
            }
        }
    }

    /// <summary>The intro card's progress bar: shown at once, faded out when the intro ends (it
    /// fills up first, so it ends complete rather than cut off). It takes no room in the card, so
    /// this never moves the card.</summary>
    private void ShowHeroGauge(bool show, bool animate)
    {
        if (show)
        {
            if (!_gaugeRetiring)
            {
                HeroGauge.BeginAnimation(OpacityProperty, null);
                HeroGauge.Opacity = 1;
            }
            HeroGauge.Visibility = System.Windows.Visibility.Visible;
            return;
        }
        if (!animate || !HeroGauge.IsVisible)
        {
            HeroGauge.BeginAnimation(OpacityProperty, null);
            HeroGauge.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(700))
        {
            BeginTime = TimeSpan.FromMilliseconds(150),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        fade.Completed += (_, _) =>
        {
            if (_viewModel.Playback.IsIntroVisible) return; // a new intro began meanwhile
            HeroGauge.BeginAnimation(OpacityProperty, null);
            HeroGauge.Visibility = System.Windows.Visibility.Collapsed;
        };
        HeroGauge.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Collapses an element once its own fade-out has had time to play, unless it is
    /// wanted again by then.</summary>
    private void CollapseAfter(System.Windows.UIElement element, TimeSpan delay, Func<bool> stillWanted)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!stillWanted()) element.Visibility = System.Windows.Visibility.Collapsed;
        };
        timer.Start();
    }

    /// <summary>The lyrics' entrance after the intro: they rise from below, grow to full size and
    /// come into focus out of a heavy blur, all at once.</summary>
    internal static void RevealLyrics(System.Windows.FrameworkElement element, TimeSpan? delay = null, double originX = 0, double rise = 70)
    {
        var duration = TimeSpan.FromMilliseconds(1300);
        var begin = delay ?? TimeSpan.Zero;
        var ease = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var blur = new System.Windows.Media.Effects.BlurEffect
        {
            Radius = 30,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
        };
        var scale = new System.Windows.Media.ScaleTransform(0.88, 0.88);
        var shift = new System.Windows.Media.TranslateTransform(0, rise);
        element.Effect = blur;
        element.RenderTransformOrigin = new System.Windows.Point(originX, 0.5);
        element.RenderTransform = new System.Windows.Media.TransformGroup { Children = { scale, shift } };

        System.Windows.Media.Animation.DoubleAnimation Anim(double from, double to, TimeSpan length) =>
            new(from, to, length) { EasingFunction = ease, BeginTime = begin };
        // Invisible until the (possibly delayed) fade starts; the fade then holds full opacity.
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800))
        {
            BeginTime = begin,
        });
        var focus = Anim(30, 0, duration);
        focus.Completed += (_, _) =>
        {
            if (element.Effect == blur) element.Effect = null; // no effect left running afterwards
        };
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, focus);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, Anim(0.88, 1, duration));
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, Anim(0.88, 1, duration));
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, Anim(rise, 0, duration));
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
        // Capture exactly the layout box at 1:1. A plain VisualBrush would fit the content's
        // *visual* bounds (the cover's shadow, overflowing lyrics) into the box instead, which
        // shrinks and shifts the snapshot so the old cover appeared to jump sideways.
        // The brush's viewbox is measured from the parent's origin, so it starts at the grid's own
        // offset (its margin); otherwise the margin would be applied twice.
        var area = new System.Windows.Rect(0, 0, width, height);
        var offset = System.Windows.Media.VisualTreeHelper.GetOffset(NowPlayingGrid);
        var brush = new System.Windows.Media.VisualBrush(NowPlayingGrid)
        {
            ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
            Viewbox = new System.Windows.Rect(offset.X, offset.Y, width, height),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
            Viewport = area,
            Stretch = System.Windows.Media.Stretch.None,
            AlignmentX = System.Windows.Media.AlignmentX.Left,
            AlignmentY = System.Windows.Media.AlignmentY.Top,
        };
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(brush, null, area);
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

    private static void RevealInExplorer(string path)
    {
        if (TuneSync.Core.Models.RemotePath.IsRemote(path))
        {
            Toast.Info("네트워크 위치의 곡입니다", "서버에 있는 파일이라 탐색기로 열 수 없습니다. 태그·가사·커버는 TuneSync에서 바로 고칠 수 있습니다.");
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

    private static T? FindDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---- dragging songs to a new place ----

    private const string QueueSongsFormat = "TuneSync.QueueSongs";
    private Point _dragStart;
    private LibraryFileViewModel? _dragSong;
    private LibraryFileViewModel? _clickToSelect;

    private LibraryFileViewModel? SongAt(object? source) =>
        FindAncestor<ListBoxItem>(source as System.Windows.DependencyObject)?.DataContext as LibraryFileViewModel;

    private void PlaylistBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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

    // ---- the play list's width: drag its edge; it snaps to the standard width ----

    private const double QueueStandardWidth = 320;
    private const double QueueMinWidth = 260;
    private const double QueueSnap = 16;

    /// <summary>As wide as the window allows while the player keeps enough room for its cover
    /// and controls.</summary>
    private double QueueMaxWidth => Math.Max(QueueMinWidth, Math.Min(760, ActualWidth - 460));

    private void QueueResizer_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) { }

    private void QueueResizer_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // From the pointer, not the thumb's own deltas: the thumb moves with the edge it resizes.
        var wanted = ActualWidth - Mouse.GetPosition(this).X;
        var width = Math.Clamp(wanted, QueueMinWidth, QueueMaxWidth);
        var snapped = Math.Abs(width - QueueStandardWidth) <= QueueSnap && QueueStandardWidth <= QueueMaxWidth;
        if (snapped) width = QueueStandardWidth;
        QueueResizer.Tag = snapped ? "snapped" : null;
        if (Math.Abs(QueuePanel.Width - width) < 0.5) return;
        QueuePanel.Width = width;
        ApplyResponsiveLayout(resizing: true);
    }

    private void QueueResizer_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        QueueResizer.Tag = null;
        _viewModel.Playback.QueueWidth = QueuePanel.Width;
        ApplyResponsiveLayout();
        FitNowPlayingPanel();
    }

    private void QueueResizer_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        QueuePanel.Width = Math.Min(QueueStandardWidth, QueueMaxWidth);
        _viewModel.Playback.QueueWidth = QueuePanel.Width;
        ApplyResponsiveLayout();
        FitNowPlayingPanel();
    }

    /// <summary>A smaller window: the play list gives up width before the player does.</summary>
    private void ClampQueueWidth()
    {
        if (ActualWidth <= 0) return;
        var wanted = Math.Max(QueueMinWidth, _viewModel.Playback.QueueWidth);
        QueuePanel.Width = Math.Min(wanted, QueueMaxWidth);
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

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackViewModel.CurrentLyricsIndex):
                FollowCurrentLyric();
                break;
            case nameof(PlaybackViewModel.CurrentFile):
                // A new title may take more or fewer lines.
                Dispatcher.BeginInvoke(FitNowPlayingPanel, System.Windows.Threading.DispatcherPriority.Loaded);
                HideBackToLyric();
                ResetLyricsScroll();
                _followPausedUntil = DateTime.MinValue;
                SyncPlaylistToCurrentTrack();
                break;
            case nameof(PlaybackViewModel.IsQueueVisible):
            case nameof(PlaybackViewModel.IsLyricsVisible):
                ApplyResponsiveLayout();
                ApplyIntroStage(animate: false);
                Dispatcher.BeginInvoke(FitNowPlayingPanel, System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            case nameof(PlaybackViewModel.IsIntroVisible):
                if (_viewModel.Playback.IsIntroVisible && _gaugeRetiring)
                {
                    // A new intro (next song): its bars start out visible.
                    _gaugeRetiring = false;
                    foreach (var gauge in new System.Windows.UIElement[] { HeroGauge, PulseGauge })
                    {
                        gauge.BeginAnimation(OpacityProperty, null);
                        gauge.Opacity = 1;
                    }
                }
                ApplyIntroStage(animate: true);
                GlideIntroGauge();
                break;
            case nameof(PlaybackViewModel.IntroProgress):
            case nameof(PlaybackViewModel.IsPlaying):
                GlideIntroGauge();
                break;
            case nameof(PlaybackViewModel.QueueViewMode):
                ApplyQueueView();
                break;
            case nameof(PlaybackViewModel.QueueTileSize):
                if (_tilePanel is not null) _tilePanel.TileSize = _viewModel.Playback.QueueTileSize;
                break;
            case nameof(PlaybackViewModel.ShowIntroGauge):
                ShowHeroGauge(_viewModel.Playback.IsIntroVisible && _viewModel.Playback.ShowIntroGauge, animate: false);
                break;
        }
    }

    /// <summary>The intro's "lyrics start soon" bar. Position updates come every 200 ms, so binding
    /// the bar to them made it step; instead each update starts a linear glide from where the bar
    /// is now to the end, timed to the intro's remaining length - which keeps it moving every frame
    /// and still corrects itself against the real position.</summary>
    private void GlideIntroGauge()
    {
        // Two bars show the intro: under the lyrics-area equalizer, and on the hero card.
        GlideGauge(IntroGaugeScale);
        GlideGauge(HeroGaugeScale);
        RetireGaugesNearTheEnd();
    }

    private bool _gaugeRetiring;
    private static readonly TimeSpan GaugeRetireTime = TimeSpan.FromMilliseconds(800);

    /// <summary>The bars dissolve where they are over the intro's last moments, finishing just as
    /// they fill - so they are gone before the card and lyrics start moving, instead of being
    /// carried off mid-fade. Seeking back brings them back.</summary>
    private void RetireGaugesNearTheEnd()
    {
        var playback = _viewModel.Playback;
        if (!playback.IsIntroVisible || playback.IntroLength <= TimeSpan.Zero) return;
        var remaining = TimeSpan.FromSeconds((1 - playback.IntroProgress) * playback.IntroLength.TotalSeconds);
        var near = playback.IsPlaying && remaining <= GaugeRetireTime;
        if (near == _gaugeRetiring) return;
        _gaugeRetiring = near;
        foreach (var gauge in new System.Windows.UIElement[] { HeroGauge, PulseGauge })
        {
            if (near)
                gauge.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, remaining)
                {
                    EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                });
            else
            {
                gauge.BeginAnimation(OpacityProperty, null); // sought back, or paused: show it again
                gauge.Opacity = 1;
            }
        }
    }

    private void GlideGauge(System.Windows.Media.ScaleTransform gauge)
    {
        var playback = _viewModel.Playback;
        var shown = gauge.ScaleX;
        var target = playback.IntroProgress;
        if (!playback.IsIntroVisible)
        {
            // The intro is over: fill up (it may be a hair short) and hold there while it fades
            // out, rather than snapping back to 0 mid-fade.
            gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(shown, 1, TimeSpan.FromMilliseconds(250)));
            return;
        }
        if (!playback.IsPlaying || playback.IntroLength <= TimeSpan.Zero)
        {
            gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            gauge.ScaleX = target;
            return;
        }
        // A new song or a seek: start from the real position instead of gliding across.
        var from = Math.Abs(shown - target) > 0.05 ? target : shown;
        var remaining = (1 - target) * playback.IntroLength.TotalSeconds;
        if (remaining <= 0.01)
        {
            gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            gauge.ScaleX = 1;
            return;
        }
        gauge.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new System.Windows.Media.Animation.DoubleAnimation(from, 1, TimeSpan.FromSeconds(remaining)));
    }

    private void ResetLyricsScroll()
    {
        LyricsScroller.Reset(LyricsScrollViewer);
        PlainLyricsScrollViewer.ScrollToTop(); // unsynced lyrics are read top to bottom by hand
        _followPausedUntil = DateTime.MinValue;
    }

    // --- responsive layout ---

    private bool? _compact;

    /// <summary>Adapts to the window width: the queue switches between two widths, and when the
    /// lyrics no longer have room beside the big cover, the cover glides up into a strip above
    /// the lyrics. Switching back needs a little more room than switching in, so dragging around
    /// the threshold doesn't make it flip back and forth. While the window edge is being dragged,
    /// the layout switch waits until the drag pauses (see <see cref="_layoutSettle"/>), so the
    /// drag itself never stalls on building the other layout.</summary>
    private void ApplyResponsiveLayout(bool resizing = false)
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var playback = _viewModel.Playback;

        // The queue keeps one fixed width (QueuePanel, 320): re-laying out its grouped list at a
        // new width cost ~100ms in one frame, which was the stutter felt while resizing.
        var content = width - (playback.IsQueueVisible ? QueuePanel.Width : 0);
        var compact = playback.IsLyricsVisible && (_compact == true ? content < 800 : content < 760);

        // Lyrics hidden: the big cover alone, never wider than the space it has.
        NowPlayingPanel.MaxWidth = Math.Max(200, content - 64);

        if (_compact == compact)
        {
            _layoutSettle?.Stop();
            return;
        }
        if (resizing && _compact is not null)
        {
            // Mid-drag: switch once the window has stayed put for a moment.
            _layoutSettle ??= CreateSettleTimer();
            _layoutSettle.Stop();
            _layoutSettle.Start();
            return;
        }
        _layoutSettle?.Stop();

        var animate = _compact is not null && IsLoaded && NowPlayingGrid.IsVisible;
        _compact = compact;

        // Where the cover is now, before the layout changes.
        var from = compact ? BigCover : SmallCover;
        System.Windows.Rect? fromRect = animate && from.IsVisible ? BoundsInLayer(from) : null;

        NowPlayingGrid.Margin = compact ? new System.Windows.Thickness(24, 16, 24, 16) : new System.Windows.Thickness(48, 24, 32, 24);
        CompactHeader.Visibility = compact ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (compact) NowPlayingPanel.Visibility = System.Windows.Visibility.Collapsed;
        else NowPlayingPanel.ClearValue(VisibilityProperty);

        Grid.SetColumn(LyricsArea, compact ? 0 : 1);
        Grid.SetColumnSpan(LyricsArea, compact ? 2 : 1);
        Grid.SetRow(LyricsArea, compact ? 1 : 0);
        Grid.SetRowSpan(LyricsArea, compact ? 1 : 2);

        if (animate)
        {
            UpdateLayout();
            var to = compact ? SmallCover : BigCover;
            if (fromRect is { } start && to.IsVisible && _viewModel.Playback.CurrentCover is { } cover)
                GlideCover(cover, start, BoundsInLayer(to), to, compact ? 8 : 10);

            // Text and lyrics settle into place with a soft fade and slide. Lyrics only slide
            // (no fade): fading the whole lyrics area means drawing every line into an extra
            // full-size layer.
            FadeSlideIn(compact ? CompactHeader : NowPlayingPanel, compact ? 0 : -16, compact ? -10 : 0);
            FadeSlideIn(LyricsArea, compact ? 0 : 24, compact ? 24 : 0, fade: false);
        }
        ApplyIntroStage(animate: false); // hero card (wide) vs. pulse (narrow) during an intro
        Dispatcher.BeginInvoke(FollowCurrentLyric, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private System.Windows.Threading.DispatcherTimer? _layoutSettle;

    private System.Windows.Threading.DispatcherTimer CreateSettleTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyResponsiveLayout();
        };
        return timer;
    }

    /// <summary>Starts transition animations one frame late. The frame right after a layout
    /// switch is a slow one (WPF builds the new arrangement), so animations started at once lost
    /// their first part to it and visibly jumped; started a frame later they play from the top.</summary>
    private static void OnNextFrame(Action start)
    {
        var frames = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (++frames < 2) return;
            System.Windows.Media.CompositionTarget.Rendering -= handler;
            start();
        };
        System.Windows.Media.CompositionTarget.Rendering += handler;
    }

    private static readonly TimeSpan LayoutTransition = TimeSpan.FromMilliseconds(450);

    // TransformToVisual, not TransformToAncestor: the layer is a sibling of the covers, not a parent.
    private System.Windows.Rect BoundsInLayer(System.Windows.FrameworkElement element) =>
        element.TransformToVisual(TransitionLayer).TransformBounds(new System.Windows.Rect(element.RenderSize));

    /// <summary>A copy of the cover flies from its old rectangle to its new one (like Android's
    /// shared-element transition) while the real one stays hidden until it lands.</summary>
    private void GlideCover(System.Windows.Media.ImageSource cover, System.Windows.Rect from, System.Windows.Rect to,
                            System.Windows.FrameworkElement target, double cornerRadius, TimeSpan? duration = null)
    {
        var length = duration ?? LayoutTransition;
        foreach (var old in TransitionLayer.Children.OfType<System.Windows.FrameworkElement>().ToList())
            TransitionLayer.Children.Remove(old);
        BigCover.Opacity = 1;
        SmallCover.Opacity = 1;

        var flyer = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(cornerRadius),
            Background = new System.Windows.Media.ImageBrush(cover) { Stretch = System.Windows.Media.Stretch.UniformToFill },
            Width = from.Width,
            Height = from.Height,
        };
        Canvas.SetLeft(flyer, from.Left);
        Canvas.SetTop(flyer, from.Top);
        TransitionLayer.Children.Add(flyer);
        target.Opacity = 0;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        System.Windows.Media.Animation.DoubleAnimation Anim(double a, double b) => new(a, b, length) { EasingFunction = ease };
        var move = Anim(from.Left, to.Left);
        move.Completed += (_, _) =>
        {
            if (!TransitionLayer.Children.Contains(flyer)) return; // superseded by a newer switch
            TransitionLayer.Children.Remove(flyer);
            target.Opacity = 1;
        };
        // The flyer waits at its start for the slow layout frame to pass, then moves.
        OnNextFrame(() =>
        {
            flyer.BeginAnimation(Canvas.LeftProperty, move);
            flyer.BeginAnimation(Canvas.TopProperty, Anim(from.Top, to.Top));
            flyer.BeginAnimation(WidthProperty, Anim(from.Width, to.Width));
            flyer.BeginAnimation(HeightProperty, Anim(from.Height, to.Height));
        });
    }

    /// <summary>Moves an element that has just been laid out somewhere else from where it was to
    /// where it is now, scaling it from its old size - the whole thing as one piece.</summary>
    private void GlideElement(System.Windows.FrameworkElement element, System.Windows.Rect from, System.Windows.Rect to, TimeSpan length)
    {
        if (to.Width <= 0 || from.Width <= 0) return;
        foreach (var old in TransitionLayer.Children.OfType<System.Windows.FrameworkElement>().ToList())
            TransitionLayer.Children.Remove(old);
        BigCover.Opacity = 1;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;

        var start = from.Width / to.Width;
        var scale = new System.Windows.Media.ScaleTransform(start, start);
        var shift = new System.Windows.Media.TranslateTransform(from.Left - to.Left, from.Top - to.Top);
        var group = new System.Windows.Media.TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(shift);
        element.RenderTransformOrigin = new System.Windows.Point(0, 0);
        element.RenderTransform = group;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        OnNextFrame(() =>
        {
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(start, 1, length) { EasingFunction = ease });
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(start, 1, length) { EasingFunction = ease });
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(shift.X, 0, length) { EasingFunction = ease });
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(shift.Y, 0, length) { EasingFunction = ease });
        });
    }

    private static void FadeSlideIn(System.Windows.UIElement element, double fromX, double fromY, bool fade = true,
        TimeSpan? duration = null, TimeSpan? delay = null)
    {
        var length = duration ?? LayoutTransition;
        var begin = delay ?? TimeSpan.Zero;
        // Show the start state right away; the motion starts a frame later (OnNextFrame).
        var shift = new System.Windows.Media.TranslateTransform(fromX, fromY);
        element.RenderTransform = shift;
        if (fade) element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 0, TimeSpan.Zero));
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        OnNextFrame(() =>
        {
            if (fade)
                element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, length)
                {
                    EasingFunction = ease,
                    BeginTime = begin,
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                });
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, length) { EasingFunction = ease, BeginTime = begin });
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(fromY, 0, length) { EasingFunction = ease, BeginTime = begin });
        });
    }

    /// <summary>Resizing: keep the lyrics padded by half the viewport and the current line
    /// pinned in the middle, in the same layout pass (so nothing shakes while dragging).</summary>
    private void LyricsScrollViewer_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        var half = LyricsScrollViewer.ActualHeight / 2;
        LyricsItems.Margin = new System.Windows.Thickness(0, half, 0, half);
        LyricsFade.EndPoint = new System.Windows.Point(0, Math.Max(1, LyricsScrollViewer.ActualHeight));
        if (DateTime.UtcNow < _followPausedUntil) return;
        // No forced UpdateLayout here: the lines are already arranged for this size, and the
        // centering offset doesn't depend on the margin just set.
        LyricsScroller.SnapTo(LyricsScrollViewer, LyricsItems, _viewModel.Playback.CurrentLyricsIndex);
    }

    private void MakeSyncedLyrics_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var playback = _viewModel.Playback;
        if (playback.CurrentFile is not { } file || string.IsNullOrWhiteSpace(playback.PlainLyrics)) return;
        LyricsSyncWindow.Run(System.Windows.Window.GetWindow(this), file, playback.PlainLyrics);
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
        ShowBackToLyric();
    }

    private void LyricLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: LyricsLineViewModel line }) return;
        _followPausedUntil = DateTime.MinValue;
        HideBackToLyric();
        _viewModel.Playback.SeekTo(line.Time);
    }

    // --- "현재 가사로": after scrolling the lyrics by hand, a way straight back ---

    private System.Windows.Threading.DispatcherTimer? _backToLyricTimer;

    private void ShowBackToLyric()
    {
        if (!_viewModel.Playback.HasSyncedLyrics) return;
        if (BackToLyricButton.Visibility != System.Windows.Visibility.Visible)
        {
            BackToLyricButton.Visibility = System.Windows.Visibility.Visible;
            FadeSlideIn(BackToLyricButton, 0, 12);
        }
        // Auto-follow comes back by itself after a few seconds; the button goes with it.
        _backToLyricTimer ??= CreateBackToLyricTimer();
        _backToLyricTimer.Stop();
        _backToLyricTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateBackToLyricTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            HideBackToLyric();
            FollowCurrentLyric();
        };
        return timer;
    }

    private void HideBackToLyric()
    {
        _backToLyricTimer?.Stop();
        BackToLyricButton.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void BackToLyric_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _followPausedUntil = DateTime.MinValue;
        HideBackToLyric();
        FollowCurrentLyric();
    }

    // --- "지금 재생 중": the playing song is scrolled out of view in the queue ---

    private void PlaylistBox_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateBackToCurrent();

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
        if (CurrentRow() is { } current && PlaylistBox.ActualHeight > 0)
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
