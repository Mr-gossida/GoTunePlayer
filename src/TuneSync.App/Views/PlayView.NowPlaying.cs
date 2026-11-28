using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// The cover card itself: fitting it into the room there is, the crossfade from one song to the
// next, and the cover as the way into focus mode.
public partial class PlayView
{
    /// <summary>The cover card at its usual width (340 as the intro's centerpiece, 300 beside the
    /// lyrics, 400 alone). When the window is too short for all of it, the cover gets smaller
    /// (the text keeps its width, so it doesn't wrap onto more lines), and if that's still not
    /// enough the spectrum and then the quality/album lines step aside - nothing is cut off.</summary>
    private void FitNowPlayingPanel()
    {
        var full = _hero ? 340.0 : _viewModel.Playback.IsLyricsAreaShown ? 300.0 : 400.0;
        if (_hero) NowPlayingPanel.Width = full;
        else NowPlayingPanel.ClearValue(WidthProperty); // the style's width

        var visible = System.Windows.Visibility.Visible;
        void Cover(double size)
        {
            BigCover.Width = size;
            BigCover.Height = size;
        }
        Cover(full);
        // Hidden (still taking its room) while it's shown big in the middle instead (PlayView.NoLyrics).
        PanelSpectrum.Visibility = _ambientShown == true ? System.Windows.Visibility.Hidden : visible;
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

    private void OpenCurrentInTagManager_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel.Playback.CurrentFile is not { } file) return;
        (System.Windows.Application.Current.MainWindow as ShellWindow)?.ShowInTagManager(file);
    }

    // --- the big cover opens 음악 집중 모드 ---

    // The big cover, or in a narrow window the small one in the header: both open it.

    /// <summary>The cover on show now (the big one, or the header's in a narrow window): where
    /// focus mode's cover flies from and back to.</summary>
    public System.Windows.FrameworkElement FocusCover => BigCover.IsVisible ? BigCover : SmallCover;

    private System.Windows.UIElement HintOf(object cover) => cover == SmallCover ? SmallFocusHint : FocusHint;

    private void BigCover_MouseEnter(object sender, MouseEventArgs e) =>
        HintOf(sender).BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));

    private void BigCover_MouseLeave(object sender, MouseEventArgs e) =>
        HintOf(sender).BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));

    private void BigCover_Click(object sender, MouseButtonEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is not ShellWindow shell) return;
        var hint = HintOf(sender);
        hint.BeginAnimation(OpacityProperty, null);
        hint.Opacity = 0;
        shell.EnterFocusMode();
        e.Handled = true;
    }
}
