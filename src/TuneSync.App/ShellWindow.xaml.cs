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
        TaskbarControls.Attach(this, playback);
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
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.F:
                    FocusSearch();
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.M:
                    EnterMiniMode(_mini?.Mode ?? PlayerMode.MiniHorizontal);
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

    // --- mini player ---

    private MiniPlayerWindow? _mini;

    /// <summary>True once the app is shutting down (the mini player then closes for real).</summary>
    public bool IsClosing { get; private set; }

    private void MiniMode_Click(object sender, RoutedEventArgs e) =>
        EnterMiniMode(_mini?.Mode ?? PlayerMode.MiniHorizontal);

    /// <summary>Swaps this window for the mini player (playback continues untouched).</summary>
    public void EnterMiniMode(PlayerMode mode)
    {
        if (mode == PlayerMode.Full) { ExitMiniMode(); return; }
        _mini ??= new MiniPlayerWindow(_playback, _settings, this);
        _mini.SetMode(mode);
        _settings.PlayerMode = mode;
        AppSettingsStore.Save(_settings);
        _mini.Show();
        _mini.Activate();
        Hide();
    }

    public void ExitMiniMode()
    {
        _settings.PlayerMode = PlayerMode.Full;
        AppSettingsStore.Save(_settings);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _mini?.Hide();
    }

    private void SeekBar_SeekStarted(object? sender, EventArgs e) => _playback.BeginSeek();

    private void SeekBar_SeekCompleted(object? sender, double seconds) => _playback.EndSeek(seconds);

    protected override void OnClosed(EventArgs e)
    {
        _mini?.Close();
        base.OnClosed(e);
    }

    private void OpenTagManager_Click(object sender, RoutedEventArgs e) => OpenTool<LibraryView>("태그 관리", 1360, 820);

    private void OpenMerge_Click(object sender, RoutedEventArgs e) => OpenTool<MergeView>("폴더 합치기", 1100, 760);

    private void OpenRename_Click(object sender, RoutedEventArgs e) => OpenTool<RenameView>("이름 변경", 1100, 760);

    private void StartScreenSaver_Click(object sender, RoutedEventArgs e) => ScreenSaver.Show();

    // --- 예약 종료 ---

    private static readonly (string Label, TimeSpan After)[] SleepPresets =
    [
        ("15분 뒤", TimeSpan.FromMinutes(15)),
        ("30분 뒤", TimeSpan.FromMinutes(30)),
        ("1시간 뒤", TimeSpan.FromHours(1)),
        ("1시간 30분 뒤", TimeSpan.FromMinutes(90)),
        ("2시간 뒤", TimeSpan.FromHours(2)),
    ];

    private void SleepTimer_Click(object sender, RoutedEventArgs e)
    {
        var timer = _services.GetRequiredService<SleepTimerViewModel>();
        var menu = new ContextMenu();

        if (timer.IsActive)
        {
            menu.Items.Add(new MenuItem
            {
                Header = timer.IsShuttingDown ? "곧 PC가 종료됩니다" : $"PC 종료까지 {timer.RemainingText} 남음",
                IsEnabled = false,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Timer24 },
            });
            var cancel = new MenuItem { Header = "예약 종료 취소", Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 } };
            cancel.Click += (_, _) => timer.Cancel();
            menu.Items.Add(cancel);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = timer.IsActive ? "다시 예약" : "음악을 멈추고 PC 종료", IsEnabled = false });
        foreach (var (label, after) in SleepPresets)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => timer.Start(after);
            menu.Items.Add(item);
        }
        var custom = new MenuItem { Header = "직접 입력…", Icon = new SymbolIcon { Symbol = SymbolRegular.Edit24 } };
        custom.Click += async (_, _) =>
        {
            if (await AskSleepTimeAsync() is { } after) timer.Start(after);
        };
        menu.Items.Add(custom);

        menu.PlacementTarget = SleepTimerButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>"직접 입력": a number and a unit (분 / 시간), up to 24 hours.</summary>
    private static async Task<TimeSpan?> AskSleepTimeAsync()
    {
        var amount = new Wpf.Ui.Controls.NumberBox
        {
            Value = 45,
            Minimum = 1,
            Maximum = 1440,
            MaxDecimalPlaces = 0,
            SmallChange = 5,
            Width = 140,
            ClearButtonEnabled = false,
        };
        var unit = new ComboBox { Width = 100, Margin = new Thickness(8, 0, 0, 0), ItemsSource = new[] { "분 뒤", "시간 뒤" }, SelectedIndex = 0 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(amount);
        row.Children.Add(unit);
        var content = new StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "지정한 시간이 지나면 음악을 멈추고 PC를 종료합니다.\nPC가 꺼지기 전 1분 동안은 취소할 수 있습니다.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(row);

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "예약 종료 시간",
            Content = content,
            PrimaryButtonText = "예약",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = "취소",
            IsSecondaryButtonEnabled = false,
        };
        Dialogs.PlaceOverApp(box);
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return null;

        var value = amount.Value ?? 0;
        var after = unit.SelectedIndex == 1 ? TimeSpan.FromHours(value) : TimeSpan.FromMinutes(value);
        if (after < TimeSpan.FromMinutes(1) || after > TimeSpan.FromHours(24))
        {
            Toast.Warning("예약할 수 없는 시간", "1분에서 24시간 사이로 입력해 주세요.");
            return null;
        }
        return after;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenTool<SettingsView>("설정", 820, 720);

    /// <summary>The queue's "태그 관리에서 보기": opens (or raises) the tag manager on that song.</summary>
    public void ShowInTagManager(LibraryFileViewModel file)
    {
        OpenTool<LibraryView>("태그 관리", 1360, 820);
        _services.GetRequiredService<LibraryView>().ShowFile(file);
    }

    /// <summary>Opens a tool in its own window, or brings it to the front if it's already open.</summary>
    private void OpenTool<TPage>(string title, double width, double height) where TPage : Page
    {
        if (_openTools.TryGetValue(typeof(TPage), out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new ToolWindow(_services.GetRequiredService<TPage>(), title, width, height) { Owner = this };
        window.Closed += (_, _) => _openTools.Remove(typeof(TPage));
        _openTools[typeof(TPage)] = window;
        window.Show();
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
        var editor = (ViewModels.TagEditorViewModel?)_services.GetService(typeof(ViewModels.TagEditorViewModel));
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
