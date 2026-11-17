using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// The lyrics column: following the line being sung, stepping aside while the user scrolls by hand,
// and the way back to the current line.
public partial class PlayView
{
    private DateTime _followPausedUntil;

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

    private void ResetLyricsScroll()
    {
        LyricsScroller.Reset(LyricsScrollViewer);
        PlainLyricsScrollViewer.ScrollToTop(); // unsynced lyrics are read top to bottom by hand
        _followPausedUntil = DateTime.MinValue;
    }

    /// <summary>Where the line being sung rests: a third of the way down rather than in the
    /// middle, so more of what's coming is in view than what has been sung (like phone players).</summary>
    private const double LyricsAnchor = 0.3;

    /// <summary>Resizing: keep the lyrics padded to the anchor (above) and the rest of the view
    /// (below) and the current line pinned there, in the same layout pass (so nothing shakes
    /// while dragging).</summary>
    private void LyricsScrollViewer_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        var height = LyricsScrollViewer.ActualHeight;
        LyricsScroller.SetAnchor(LyricsScrollViewer, LyricsAnchor);
        LyricsItems.Margin = new System.Windows.Thickness(0, height * LyricsAnchor, 0, height * (1 - LyricsAnchor));
        LyricsFade.EndPoint = new System.Windows.Point(0, Math.Max(1, LyricsScrollViewer.ActualHeight));
        if (DateTime.UtcNow < _followPausedUntil) return;
        // No forced UpdateLayout here: the lines are already arranged for this size, and the
        // centering offset doesn't depend on the margin just set.
        LyricsScroller.SnapTo(LyricsScrollViewer, LyricsItems, _viewModel.Playback.CurrentLyricsIndex);
    }

    private void MakeSyncedLyrics_Click(object sender, System.Windows.RoutedEventArgs e) => OpenLyricsSync();

    private void EditSyncedLyrics_Click(object sender, System.Windows.RoutedEventArgs e) => OpenLyricsSync();

    /// <summary>The sync editor on the playing song's lyrics: synced ones to fix ("싱크 편집"),
    /// plain ones to time ("싱크 가사 만들기"). Nothing when the song has no lyrics.</summary>
    public void OpenLyricsSync()
    {
        var playback = _viewModel.Playback;
        var lyrics = playback.HasSyncedLyrics ? playback.SyncedLyricsText : playback.PlainLyrics;
        if (playback.CurrentFile is not { } file || string.IsNullOrWhiteSpace(lyrics)) return;
        LyricsSyncWindow.Run(System.Windows.Window.GetWindow(this), file, lyrics);
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
}
