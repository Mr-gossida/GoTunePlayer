using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

/// <summary>The 플레이 page: the playing song's cover card and lyrics, with the play list beside
/// them. This file wires the page to its view models; each concern has its own file
/// (PlayView.Intro / NowPlaying / Lyrics / Layout / Queue / QueueSearch / QueueDragDrop /
/// QueueStickyGroup).</summary>
public partial class PlayView : Page
{
    private readonly PlayPageViewModel _viewModel;

    public PlayView(PlayPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.Playback.PropertyChanged += Playback_PropertyChanged;
        HookEqualizer();
        HookNoLyrics();
        // The queue emptied or changed (folders removed, a rescan, a search): the "지금 재생 중"
        // button only makes sense while the playing song is in it.
        ((System.Collections.Specialized.INotifyCollectionChanged)PlaylistBox.Items).CollectionChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(UpdateBackToCurrent, System.Windows.Threading.DispatcherPriority.Loaded);
            // after the group headings are worked out again (LibraryViewModel.ScheduleGroupHeaders)
            Dispatcher.BeginInvoke(UpdateStickyGroup, System.Windows.Threading.DispatcherPriority.Background);
        };
        _viewModel.Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.PlaylistGroup))
                Dispatcher.BeginInvoke(UpdateStickyGroup, System.Windows.Threading.DispatcherPriority.Background);
        };
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
            case nameof(PlaybackViewModel.IsLyricsAreaShown):
                ApplyResponsiveLayout();
                ApplyIntroStage(animate: false);
                Dispatcher.BeginInvoke(FitNowPlayingPanel, System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            case nameof(PlaybackViewModel.IsIntroVisible):
                if (_viewModel.Playback.IsIntroVisible && _gaugeRetiring)
                {
                    // A new intro (next song): its bars start out visible.
                    _gaugeRetiring = false;
                    ShowGaugesAgain();
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
                ShowHeroGauge(_viewModel.Playback.IsIntroVisible && !IsEqualizerOpen && _viewModel.Playback.ShowIntroGauge, animate: false);
                break;
        }
    }

    /// <summary>The panel an ItemsControl lays its items out on (stack or tiles).</summary>
    private static System.Windows.Controls.Panel? ItemsHost(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Panel { IsItemsHost: true } panel) return panel;
            if (ItemsHost(child) is { } found) return found;
        }
        return null;
    }
}
