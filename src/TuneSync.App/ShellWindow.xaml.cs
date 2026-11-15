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

/// <summary>The main window is the player (playback bar + 플레이 screen). The secondary tools
/// open in their own windows from the title bar's 도구 menu.</summary>
public partial class ShellWindow : FluentWindow
{
    private readonly IServiceProvider _services;
    private readonly PlaybackViewModel _playback;
    private readonly AppSettings _settings;
    private readonly Dictionary<Type, ToolWindow> _openTools = [];

    public ShellWindow(IServiceProvider services, PlaybackViewModel playback, AppSettings settings)
    {
        InitializeComponent();
        _services = services;
        _playback = playback;
        _settings = settings;
        DataContext = playback;

        RestorePlacement();
        ThemeHelper.Apply(settings.Theme, this);

        var snackbar = new Wpf.Ui.SnackbarService();
        snackbar.SetSnackbarPresenter(SnackbarPresenter);
        Toast.Register(this, snackbar);

        PlayerFrame.Navigate(services.GetRequiredService<PlayView>());
        TaskbarControls.Attach(this, playback, new("미니 플레이어로", "\uE73F", EnterLastMiniMode));
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                StartingQuietly = false;
                return;
            }
            // 설정 › 최소화하면 미니 플레이어로 (not when Windows started us minimized at sign-in)
            if (!_settings.MinimizeToMini || StartingQuietly || IsClosing || !IsVisible) return;
            Hide(); // first, so the window doesn't flash back up while being restored
            WindowState = WindowState.Normal;
            EnterLastMiniMode();
        };
        SleepTimerButton.DataContext = services.GetRequiredService<SleepTimerViewModel>();
        ScreenSaver.Start(services);

        // Cover backdrop: the old picture fades out while the new one fades in, instead of a cut.
        playback.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName != nameof(PlaybackViewModel.BackdropCover)) return;
            BackdropPrevious.Source = BackdropImage.Source;
        };
        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlaybackViewModel.BackdropCover)) return;
            var duration = TimeSpan.FromMilliseconds(900);
            BackdropPrevious.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.55, 0, duration));
            BackdropImage.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 0.55, duration));
        };

        // Narrow window: drop the volume slider (the mute button stays) so the view toggles and
        // the mini-player button always fit in the transport bar.
        SizeChanged += (_, _) =>
        {
            var roomy = ActualWidth >= 1100;
            VolumeSlider.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
            VolumeText.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            ToggleFocusMode();
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Escape && NowPlayingCard.IsOpen)
        {
            NowPlayingCard.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Escape && IsInFocusMode)
        {
            ExitFocusMode();
            e.Handled = true;
            return;
        }
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.F:
                    FocusSearch();
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.M:
                    EnterLastMiniMode();
                    e.Handled = true;
                    return;
            }
        }
        if (PlayerShortcuts.Handle(e, _playback))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Mouse wheel over the volume slider: 5% per notch.</summary>
    private void Volume_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        _playback.ChangeVolume(e.Delta > 0 ? 0.05 : -0.05);
        e.Handled = true;
    }

    private void FocusSearch()
    {
        _playback.IsQueueVisible = true; // the search box lives in the queue panel
        Dispatcher.BeginInvoke(() => _services.GetRequiredService<PlayView>().FocusSearch(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => FocusSearch();

    private void SeekBar_SeekStarted(object? sender, EventArgs e) => _playback.BeginSeek();

    private void SeekBar_SeekCompleted(object? sender, double seconds) => _playback.EndSeek(seconds);

    protected override void OnClosed(EventArgs e)
    {
        _mini?.Close();
        base.OnClosed(e);
    }

    /// <summary>Reopens where the user left the window, unless that spot is no longer on any
    /// screen (a monitor was unplugged) - then it falls back to centering.</summary>
    private void RestorePlacement()
    {
        if (_settings.Window is not { } p || p.Width < MinWidth || p.Height < MinHeight) return;

        var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleArea = new Rect(p.Left, p.Top, p.Width, 40);
        if (!visible.IntersectsWith(titleArea)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        if (p.IsMaximized) WindowState = WindowState.Maximized;
    }

    // --- transport bar ---

    private const double TransportMaxCenter = 720; // controls + seek bar never wider than this
    private const double TransportMinCenter = 320;
    private const double TransportMinLeft = 160;

    private void Transport_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTransport();

    /// <summary>Keeps play/pause and the seek bar in the middle of the window: both sides get the
    /// same width whenever there's room (the controls used to drift right on a wide window,
    /// because all the spare width went to the left column). When the window is too narrow for
    /// that, the right side keeps the room its buttons need and the left side gives way first.</summary>
    private void LayoutTransport()
    {
        var total = TransportGrid.ActualWidth;
        if (total <= 0) return;
        var tools = TransportTools.ActualWidth;

        double side;
        var center = Math.Min(TransportMaxCenter, total - 2 * tools);
        if (center >= TransportMinCenter)
        {
            side = (total - center) / 2;
            SetTransportColumns(side, side);
            return;
        }

        // Too narrow to balance: buttons on the right keep their room, the rest is shared.
        center = Math.Max(TransportMinCenter, total - tools - TransportMinLeft);
        var left = Math.Max(TransportMinLeft, total - tools - center);
        SetTransportColumns(left, tools);
    }

    private void SetTransportColumns(double left, double right)
    {
        TransportLeft.MinWidth = 0;
        TransportLeft.Width = new GridLength(Math.Max(0, left));
        TransportRight.Width = new GridLength(Math.Max(0, right));
        TransportCenter.Width = new GridLength(1, GridUnitType.Star);
    }

    private bool _discardConfirmed;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Unsaved tag edits: ask before they're lost.
        var editor = _services.GetService<TagEditorViewModel>();
        if (!_discardConfirmed && editor is { HasPending: true })
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(async () =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (!await Dialogs.ConfirmAsync("저장하지 않은 태그 변경",
                        $"저장하지 않은 변경 내용이 {editor.PendingFileCount}곡 있습니다. 저장하지 않고 닫을까요?", "저장하지 않고 닫기"))
                    return;
                _discardConfirmed = true;
                Close();
            });
            return;
        }

        IsClosing = true;
        _playback.SaveSession(); // reopen on this song, at this spot
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        // (Never shown - started and closed in the mini player - leaves NaN bounds: keep the old ones.)
        if (!bounds.IsEmpty && !double.IsNaN(bounds.Left) && !double.IsNaN(bounds.Width))
        {
            _settings.Window = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                IsMaximized = WindowState == WindowState.Maximized,
            };
            AppSettingsStore.Save(_settings);
        }
        base.OnClosing(e);
    }
}
