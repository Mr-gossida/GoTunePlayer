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
        AppFonts.Install(settings); // before any window loads: they all follow the app font
        InitializeComponent();
        _services = services;
        _playback = playback;
        _settings = settings;
        DataContext = playback;

        RestorePlacement();
        ThemeHelper.Apply(settings.Theme, this);
        Loaded += (_, _) => WatchHud();

        Toast.Register(this, Toasts);

        PlayerFrame.Navigate(services.GetRequiredService<PlayView>());
        TaskbarControls.Attach(this, playback, new("미니 플레이어로", TaskbarControls.SwitchIcon.ToMini, EnterLastMiniMode));
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                StartingQuietly = false;
                _shownState = WindowState; // what to come back to from the mini player
                return;
            }
            // 설정 › 최소화하면 미니 플레이어로 (not when Windows started us minimized at sign-in).
            // The window stays minimized while hidden; ExitMiniMode brings it back up as it was.
            if (!_settings.MinimizeToMini || StartingQuietly || IsClosing || !IsVisible) return;
            Hide();
            EnterLastMiniMode();
        };
        SleepTimerButton.DataContext = services.GetRequiredService<SleepTimerViewModel>();
        EqualizerButton.DataContext = services.GetRequiredService<EqualizerViewModel>();
        ScreenSaver.Start(services);
        SetUpTray();

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

        // Narrow window: drop only the percentage (it's in the mute button's tooltip). The slider
        // stays - it used to go too, leaving mouse users nothing but the mute button - and still
        // fits beside the view toggles at the minimum window width (900).
        SizeChanged += (_, _) =>
            VolumeText.Visibility = ActualWidth >= 1100 ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            ToggleFocusMode();
            e.Handled = true;
            return;
        }
        if (CloseSignalPathOnEscape(e)) return;
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
        // Esc: closes the equalizer (not while typing, e.g. a preset name)
        if (e.Key == System.Windows.Input.Key.Escape && System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.TextBox
            && _services.GetRequiredService<EqualizerViewModel>() is { IsPanelOpen: true } equalizer)
        {
            equalizer.IsPanelOpen = false;
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
        _tray?.Dispose();
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
        // 설정 › 닫기 버튼을 누르면 › 시스템 트레이로 숨기기: hide and keep playing. (Nothing is lost
        // while hidden, so unsaved tag edits are only asked about on a real exit.)
        if (!_exitRequested && !IsClosing && _settings.CloseAction == CloseAction.Tray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Unsaved tag edits, or a 싱크 가사 일괄 만들기 at work (quitting would cut it off, maybe
        // in the middle of writing a song): ask first.
        var unsaved = UnsavedEdits();
        var batch = LyricsSyncActivity.BatchRunning;
        if (_asking)
        {
            e.Cancel = true; // asked already (a second close, or Windows signing out meanwhile)
            return;
        }
        if (!_discardConfirmed && (unsaved > 0 || batch))
        {
            e.Cancel = true;
            _asking = true;
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await AskAndQuitAsync(unsaved, batch);
                }
                finally
                {
                    _asking = false;
                }
            });
            return;
        }

        IsClosing = true;
        SaveForNextTime();
        base.OnClosing(e);
    }

    /// <summary>The question asked before quitting is open.</summary>
    private bool _asking;

    /// <summary>Asks before quitting with unsaved edits or a batch at work; quits on yes.</summary>
    private async Task AskAndQuitAsync(int unsaved, bool batch)
    {
        if (IsInTray) RestoreFromTray(); // 종료 from the tray: the question needs a window
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var (title, message, button) =
            unsaved > 0 && batch ? ("저장하지 않은 태그 변경",
                $"저장하지 않은 변경 내용이 {unsaved}곡 있고, 싱크 가사 일괄 만들기도 진행 중입니다. 저장하지 않고 종료할까요? 일괄 작업은 지금 곡에서 멈추고, 끝난 곡은 이미 저장되어 있습니다.",
                "저장하지 않고 종료")
            : unsaved > 0 ? ("저장하지 않은 태그 변경", $"저장하지 않은 변경 내용이 {unsaved}곡 있습니다. 저장하지 않고 닫을까요?", "저장하지 않고 닫기")
            : ("싱크 가사 일괄 만들기", "싱크 가사 일괄 만들기가 진행 중입니다. 종료하면 지금 곡에서 멈춥니다. 끝난 곡은 이미 저장되어 있습니다.", "멈추고 종료");
        var quit = await Dialogs.ConfirmAsync(title, KeepWords.Joined(message), button);
        ShutdownBlock.Clear(this);
        if (!quit)
        {
            _exitRequested = false; // stays open; the close button goes back to what it's set to
            return;
        }
        _discardConfirmed = true;
        // Stopped first, and waited for: a song being written is finished, not cut off.
        if (LyricsSyncActivity.Batch is { ViewModel.IsRunning: true } running)
        {
            try { await running.ViewModel.StopAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { /* stuck: quit anyway */ }
        }
        _asking = false;
        Close();
    }

    /// <summary>Songs with unsaved tag edits. (During a rescan, edits wait for their song's new row
    /// and aren't in the editor's count yet.)</summary>
    private int UnsavedEdits() =>
        (_services.GetService<TagEditorViewModel>()?.PendingFileCount ?? 0) + (_services.GetService<LibraryViewModel>()?.PendingEditsInScan ?? 0);

    /// <summary>Windows is signing out or shutting down. The window isn't closed then (no
    /// <see cref="OnClosing"/>), so unsaved edits were dropped without a word and the session and
    /// window place weren't kept. Now: with something unsaved or a batch at work, the sign-out is
    /// held - Windows lists the app with the reason - and the usual question comes up; otherwise
    /// what the next start needs is saved here.</summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _exitRequested = true; // must quit, not hide to the tray
        var unsaved = UnsavedEdits();
        if (!_discardConfirmed && (unsaved > 0 || LyricsSyncActivity.BatchRunning))
        {
            e.Cancel = true;
            ShutdownBlock.Set(this, unsaved > 0 ? $"저장하지 않은 태그 변경이 {unsaved}곡 있습니다." : "싱크 가사 일괄 만들기가 진행 중입니다.");
            Dispatcher.BeginInvoke(Close);
            return;
        }
        if (!IsClosing) SaveForNextTime();
    }

    /// <summary>The song and spot to reopen on, and the window's place.</summary>
    private void SaveForNextTime()
    {
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
    }
}
