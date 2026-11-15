using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;
using TuneSync.App.Views;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace TuneSync.App;

// Focus mode: the now-playing picture over the whole player window.
public partial class ShellWindow
{
    // --- 음악 집중 모드 (click the big cover, or F11) ---
    // The screen saver's picture, inside the player window and answering to the mouse: drag the
    // seek bar, click a lyric line, and controls that show while the mouse moves.

    private NowPlayingStage? _focusStage;
    private System.Windows.Threading.DispatcherTimer? _focusIdle;

    public bool IsInFocusMode => _focusStage is not null;

    public void ToggleFocusMode()
    {
        if (IsInFocusMode) ExitFocusMode();
        else EnterFocusMode();
    }

    public void EnterFocusMode()
    {
        if (_focusStage is not null || !IsVisible || WindowState == WindowState.Minimized) return;
        var player = _services.GetRequiredService<PlayView>();
        var from = CoverBounds(player.FocusCover);

        var stage = new NowPlayingStage(_playback, width: ActualWidth, interactive: true);
        _focusStage = stage;
        FocusStageHost.Content = stage;
        FocusHost.Visibility = Visibility.Visible;
        FocusHost.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        ShowFocusChrome();

        // The cover flies from its place in the player to the middle of focus mode.
        if (from is { } start)
        {
            stage.Cover.Opacity = 0;
            Dispatcher.BeginInvoke(() =>
            {
                if (_focusStage != stage) return;
                if (CoverBounds(stage.Cover) is { } end) FlyCover(start, end, 10, 14, () => stage.Cover.Opacity = 1);
                else stage.Cover.Opacity = 1;
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public void ExitFocusMode(bool animate = true)
    {
        if (_focusStage is not { } stage) return;
        _focusStage = null;
        _focusIdle?.Stop();
        FocusHost.Cursor = null;
        void Done()
        {
            if (_focusStage is not null) return; // entered again meanwhile
            FocusHost.Visibility = Visibility.Collapsed;
            FocusStageHost.Content = null; // stops its timers and light
        }
        if (!animate)
        {
            FocusHost.BeginAnimation(OpacityProperty, null);
            Done();
            return;
        }

        // The cover flies back to its place in the player while the picture fades.
        var player = _services.GetRequiredService<PlayView>();
        var home = player.FocusCover;
        if (CoverBounds(stage.Cover) is { } from && CoverBounds(home) is { } to)
        {
            stage.Cover.Opacity = 0;
            home.Opacity = 0;
            FlyCover(from, to, 14, home == player.BigCover ? 10 : 8, () => home.Opacity = 1);
        }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(320));
        fade.Completed += (_, _) => Done();
        FocusHost.BeginAnimation(OpacityProperty, fade);
    }

    private void ExitFocus_Click(object sender, RoutedEventArgs e) => ExitFocusMode();

    /// <summary>An element's rectangle over the focus area, or null when it isn't on screen.</summary>
    private Rect? CoverBounds(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0) return null;
        return element.TransformToVisual(FocusFlyLayer).TransformBounds(new Rect(element.RenderSize));
    }

    private void FlyCover(Rect from, Rect to, double fromCorner, double toCorner, Action landed)
    {
        FocusFlyLayer.Children.Clear();
        var flyer = new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(fromCorner),
            Background = _playback.CurrentCover is { } cover
                ? new System.Windows.Media.ImageBrush(cover) { Stretch = System.Windows.Media.Stretch.UniformToFill }
                : (System.Windows.Media.Brush)FindResource("PlayerHoverBrush"),
            Width = from.Width,
            Height = from.Height,
        };
        System.Windows.Controls.Canvas.SetLeft(flyer, from.Left);
        System.Windows.Controls.Canvas.SetTop(flyer, from.Top);
        FocusFlyLayer.Children.Add(flyer);

        var length = TimeSpan.FromMilliseconds(460);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        System.Windows.Media.Animation.DoubleAnimation Anim(double a, double b) => new(a, b, length) { EasingFunction = ease };
        var move = Anim(from.Left, to.Left);
        move.Completed += (_, _) =>
        {
            FocusFlyLayer.Children.Remove(flyer);
            landed();
        };
        flyer.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, move);
        flyer.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, Anim(from.Top, to.Top));
        flyer.BeginAnimation(WidthProperty, Anim(from.Width, to.Width));
        flyer.BeginAnimation(HeightProperty, Anim(from.Height, to.Height));
    }

    /// <summary>The controls (and the pointer) show while the mouse moves, and step out of the
    /// way 3 s after it stops - not while the pointer is on them.</summary>
    private void FocusHost_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) => ShowFocusChrome();

    private void ShowFocusChrome()
    {
        if (_focusStage is null) return;
        FocusHost.Cursor = null;
        if (FocusChrome.Opacity < 0.99)
            FocusChrome.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        if (_focusIdle is null)
        {
            _focusIdle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _focusIdle.Tick += (_, _) =>
            {
                _focusIdle.Stop();
                if (_focusStage is null) return;
                if (FocusChrome.IsMouseOver)
                {
                    _focusIdle.Start();
                    return;
                }
                FocusChrome.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
                FocusHost.Cursor = System.Windows.Input.Cursors.None;
            };
        }
        _focusIdle.Stop();
        _focusIdle.Start();
    }
}
