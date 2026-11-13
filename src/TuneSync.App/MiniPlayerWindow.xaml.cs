using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>The compact player: a horizontal strip or a vertical card that can float above other
/// windows. It drives the same <see cref="PlaybackViewModel"/> as the full player, so switching
/// between them never interrupts playback.</summary>
public partial class MiniPlayerWindow : FluentWindow
{
    private const double StripWidth = 500, StripHeight = 104;
    private const double CardWidth = 320, CardHeight = 620;

    private readonly PlaybackViewModel _playback;
    private readonly AppSettings _settings;
    private readonly ShellWindow _shell;
    private PlayerMode _mode = PlayerMode.MiniHorizontal;

    public MiniPlayerWindow(PlaybackViewModel playback, AppSettings settings, ShellWindow shell)
    {
        InitializeComponent();
        _playback = playback;
        _settings = settings;
        _shell = shell;
        DataContext = playback;
        TaskbarControls.Attach(this, playback, new("전체 플레이어로", "\uE740", () => _shell.ExitMiniMode()));
        WindowSnapper.Attach(this); // magnetic screen edges while dragging

        playback.PropertyChanged += Playback_PropertyChanged;
        SizeChanged += (_, _) => KeepOnScreen();

        var snackbar = new Wpf.Ui.SnackbarService();
        snackbar.SetSnackbarPresenter(SnackbarPresenter);
        Toast.Register(this, snackbar);
    }

    public PlayerMode Mode => _mode;

    /// <summary>Switches between the strip and the card, each reopening where it was last left.</summary>
    public void SetMode(PlayerMode mode)
    {
        if (IsVisible) RememberPosition();
        _mode = mode;
        var vertical = mode == PlayerMode.MiniVertical;
        HorizontalLayout.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        VerticalLayout.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;

        var saved = vertical ? _settings.MiniVertical : _settings.MiniHorizontal;
        ApplySizing(saved);
        var area = SystemParameters.WorkArea;
        var left = saved?.Left ?? area.Right - Width - 24;
        var top = saved?.Top ?? area.Bottom - (vertical ? CardHeight : StripHeight) - 24;
        // Keep it on screen even if a monitor was removed since.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(left, top, Width, 40)))
        {
            left = area.Right - Width - 24;
            top = area.Bottom - (vertical ? CardHeight : StripHeight) - 24;
        }
        Left = left;
        Top = top;
    }

    // --- resizing: each mini player has its own limits and remembers its size ---

    private const double StripMinWidth = 420, StripMaxWidth = 1000;
    private const double CardMinWidth = 280, CardMaxWidth = 520;
    private const double QueueMinHeight = 140;
    private bool _applyingSize;

    /// <summary>The strip: width only. The card: width (the cover scales with it); its height
    /// follows the content while the queue is folded, and can be dragged freely while it's out
    /// (the queue takes the extra room).</summary>
    private void ApplySizing(WindowPlacement? saved = null)
    {
        _applyingSize = true;
        try
        {
            if (_mode != PlayerMode.MiniVertical)
            {
                SizeToContent = SizeToContent.Manual;
                MinWidth = StripMinWidth;
                MaxWidth = StripMaxWidth;
                MinHeight = MaxHeight = StripHeight;
                Width = Math.Clamp(saved?.Width is > 0 and var w ? w : Width is > 0 and not double.NaN ? Width : StripWidth, StripMinWidth, StripMaxWidth);
                Height = StripHeight;
                return;
            }

            MinWidth = CardMinWidth;
            MaxWidth = CardMaxWidth;
            if (saved?.Width is > 0) Width = Math.Clamp(saved.Width, CardMinWidth, CardMaxWidth);
            else if (double.IsNaN(Width) || Width < CardMinWidth || Width > CardMaxWidth) Width = CardWidth;

            MinHeight = 0;
            MaxHeight = SystemParameters.WorkArea.Height; // never taller than the screen
            SizeToContent = SizeToContent.Height;
            if (!_playback.IsMiniQueueExpanded) return;

            // Queue out: start from the content's height with the queue at its minimum, then let
            // the user (or the last saved size) decide how much taller it gets.
            UpdateLayout();
            var shortest = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            MinHeight = Math.Min(shortest, MaxHeight);
            var wanted = _settings.MiniVerticalQueueHeight > shortest ? _settings.MiniVerticalQueueHeight : shortest + 180;
            Height = Math.Min(wanted, MaxHeight);
        }
        finally
        {
            _applyingSize = false;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_applyingSize || !IsVisible || _mode != PlayerMode.MiniVertical) return;

        if (!_playback.IsMiniQueueExpanded)
        {
            // Folded queue: the height always fits the content (a drag on the bottom edge
            // springs back).
            if (SizeToContent != SizeToContent.Height)
                Dispatcher.BeginInvoke(() => ApplySizing(), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        // Wider card = bigger cover: keep room for at least the queue's minimum height.
        if (MiniQueue.ActualHeight > 0)
            MinHeight = Math.Clamp(ActualHeight - MiniQueue.ActualHeight + QueueMinHeight, 0, MaxHeight);
        KeepOnScreen();
        RememberPositionSoon();
    }

    private void RememberPosition()
    {
        if (_mode == PlayerMode.MiniVertical && _playback.IsMiniQueueExpanded && SizeToContent == SizeToContent.Manual)
            _settings.MiniVerticalQueueHeight = ActualHeight;
        var placement = new WindowPlacement { Left = Left, Top = Top, Width = ActualWidth > 0 ? ActualWidth : Width, Height = Height };
        if (_mode == PlayerMode.MiniVertical) _settings.MiniVertical = placement;
        else _settings.MiniHorizontal = placement;
        AppSettingsStore.Save(_settings);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsVisible) RememberPositionSoon();
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>A drag moves the window many times a second; save once it has settled.</summary>
    private void RememberPositionSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick -= SaveTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        RememberPosition();
    }

    /// <summary>No title bar here: the whole window is the drag handle.</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click anywhere that isn't a control: back to the full player, like
        // double-clicking a title bar.
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            _shell.ExitMiniMode();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _shell.ExitMiniMode();
            e.Handled = true;
            return;
        }
        if (PlayerShortcuts.Handle(e, _playback))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.CurrentFile) or nameof(PlaybackViewModel.IsMiniQueueExpanded))
            ScrollQueueToCurrent();
        if (e.PropertyName == nameof(PlaybackViewModel.IsMiniQueueExpanded) && _mode == PlayerMode.MiniVertical)
        {
            if (!_playback.IsMiniQueueExpanded && SizeToContent == SizeToContent.Manual)
                _settings.MiniVerticalQueueHeight = ActualHeight; // remember how tall it was
            Dispatcher.BeginInvoke(() =>
            {
                ApplySizing();
                KeepOnScreen();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Folding the queue out grows the window downward; if that would run off the
    /// bottom of the screen, slide it up instead.</summary>
    private void KeepOnScreen()
    {
        if (!IsVisible) return;
        var area = SystemParameters.WorkArea;
        if (Top + ActualHeight > area.Bottom)
            Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }

    private void ScrollQueueToCurrent()
    {
        if (!_playback.IsMiniQueueExpanded || _playback.CurrentFile is not { } current) return;
        Dispatcher.BeginInvoke(() =>
        {
            MiniQueue.SelectedItem = current;
            MiniQueue.ScrollIntoView(current);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MiniQueue_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MiniQueue.SelectedItem is LibraryFileViewModel file) _playback.PlayFile(file);
    }

    private void MiniQueue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || MiniQueue.SelectedItem is not LibraryFileViewModel file) return;
        _playback.PlayFile(file);
        e.Handled = true;
    }

    private void SeekBar_SeekStarted(object? sender, EventArgs e) => _playback.BeginSeek();

    private void SeekBar_SeekCompleted(object? sender, double seconds) => _playback.EndSeek(seconds);

    private void Pin_Click(object sender, RoutedEventArgs e) => _playback.ToggleMiniTopmostCommand.Execute(null);

    private void Expand_Click(object sender, RoutedEventArgs e) => _shell.ExitMiniMode();

    private void Rotate_Click(object sender, RoutedEventArgs e) =>
        _shell.EnterMiniMode(_mode == PlayerMode.MiniVertical ? PlayerMode.MiniHorizontal : PlayerMode.MiniVertical);

    /// <summary>Closing the mini player quits the app, like closing the full player does.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => _shell.Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closed by Windows (Alt+F4, taskbar): quit the whole app, not just leave it windowless.
        if (!_shell.IsClosing)
        {
            e.Cancel = true;
            _shell.Close();
            return;
        }
        base.OnClosing(e);
    }
}
