using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;
using TuneSync.App.Views;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using Wpf.Ui.Controls;

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
        base.OnPreviewKeyDown(e);
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

    protected override void OnClosing(CancelEventArgs e)
    {
        IsClosing = true;
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
