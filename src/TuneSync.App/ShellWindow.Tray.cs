using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace TuneSync.App;

// 설정 › 닫기 버튼을 누르면: the close button can hide the player to the notification area
// (system tray) instead of quitting; music keeps playing and the tray icon brings it back.
public partial class ShellWindow
{
    private TrayIcon? _tray;
    private ContextMenu? _trayMenu;

    /// <summary>Set by 종료 in the tray menu (and when Windows signs out): this close really quits.</summary>
    private bool _exitRequested;

    /// <summary>Every player window is hidden; only the tray icon is left.</summary>
    public bool IsInTray { get; private set; }

    private void SetUpTray()
    {
        // Windows signing out or shutting down must quit, not hide (and not lose what's unsaved).
        Application.Current.SessionEnding += OnSessionEnding;
        _playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlaybackViewModel.NowPlayingTitle) or nameof(PlaybackViewModel.NowPlayingSubtitle)
                or nameof(PlaybackViewModel.IsPlaying))
                UpdateTrayToolTip();
        };
        UpdateTray();
    }

    /// <summary>The icon shows while the option is on, and always while the player hides in it
    /// (the option may be switched off from a tool window meanwhile).</summary>
    public void UpdateTray()
    {
        var wanted = IsInTray || _settings.CloseAction == CloseAction.Tray;
        if (!wanted)
        {
            if (_tray is not null) _tray.IsVisible = false;
            return;
        }
        if (_tray is null)
        {
            _tray = new TrayIcon();
            _tray.Activated += RestoreFromTray;
            _tray.MenuRequested += ShowTrayMenu;
        }
        UpdateTrayToolTip();
        _tray.IsVisible = true;
    }

    private void UpdateTrayToolTip()
    {
        if (_tray is null) return;
        if (!_playback.HasCurrentFile)
        {
            _tray.ToolTip = "GoTune Player";
            return;
        }
        var song = string.IsNullOrWhiteSpace(_playback.NowPlayingSubtitle)
            ? _playback.NowPlayingTitle
            : $"{_playback.NowPlayingTitle} - {_playback.NowPlayingSubtitle}";
        _tray.ToolTip = $"GoTune Player\n{(_playback.IsPlaying ? "▶" : "❚❚")} {song}";
    }

    /// <summary>The close button with 시스템 트레이로 숨기기 on: every player window goes away,
    /// the music keeps playing. The first time, Windows shows a short notice saying so.</summary>
    private void HideToTray()
    {
        IsInTray = true;
        UpdateTray();
        _mini?.Hide();
        Hide();
        if (_settings.TrayNoticeShown || _tray is null) return;
        _tray.ShowNotification("GoTune Player가 계속 재생됩니다",
            "알림 영역의 GoTune 아이콘을 누르면 다시 열립니다. 완전히 끄려면 아이콘을 마우스 오른쪽 단추로 눌러 [종료]를 고르세요.");
        _settings.TrayNoticeShown = true;
        AppSettingsStore.Save(_settings);
    }

    /// <summary>Back from the tray as it was: the mini player if that was in use, otherwise
    /// this window (maximized stays maximized).</summary>
    public void RestoreFromTray()
    {
        if (_settings.PlayerMode != PlayerMode.Full && _mini is not null)
        {
            LeaveTray();
            _mini.Show();
            BringUp(_mini);
            return;
        }
        ShowFullFromTray();
    }

    private void ShowFullFromTray()
    {
        if (_settings.PlayerMode != PlayerMode.Full)
        {
            ExitMiniMode(); // also leaves the tray
            BringUp(this);
            return;
        }
        LeaveTray();
        Show();
        RestoreFromMinimized();
        BringUp(this);
    }

    /// <summary>A player window is showing again (also called by the mini player switches, so a
    /// hotkey that opens the mini player from the tray counts).</summary>
    private void LeaveTray()
    {
        if (!IsInTray) return;
        IsInTray = false;
        UpdateTray();
    }

    /// <summary>Activates the window and lifts it above the others (also when GoTune is started
    /// again while running, see App).</summary>
    internal static void BringUp(Window window)
    {
        window.Activate();
        // Activate alone may only flash the taskbar button when another app has the focus, so
        // the window flips its always-on-top state and back, which raises it (a pinned mini
        // player to the top of the other always-on-top windows). Through SetCurrentValue:
        // assigning Topmost would replace the mini player's binding to 항상 위에 표시 for good,
        // and its pin button would do nothing until the app restarted.
        var topmost = window.Topmost;
        window.SetCurrentValue(Window.TopmostProperty, !topmost);
        window.SetCurrentValue(Window.TopmostProperty, topmost);
        window.Focus();
    }

    /// <summary>종료 in the tray menu: quits for real, through the same checks as before
    /// (unsaved tag edits) and the same saving of the window and session.</summary>
    public void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    // --- the tray menu ---

    private MenuItem? _trayPlayPause, _trayHud, _trayHudClickThrough;

    private void ShowTrayMenu()
    {
        _trayMenu ??= BuildTrayMenu();
        var playing = _playback.IsPlaying;
        _trayPlayPause!.Header = playing ? "일시정지" : "재생";
        _trayPlayPause.Icon = new SymbolIcon { Symbol = playing ? SymbolRegular.Pause24 : SymbolRegular.Play24 };
        // (WPF-UI's menu item draws the icon instead of a check mark, so the text says the state)
        _trayHud!.Header = Hud.IsOpen ? "HUD 숨기기" : "HUD 표시";
        // a click-through HUD can't be clicked, so this is one of the ways back
        _trayHudClickThrough!.Header = Hud.ClickThrough ? "HUD 마우스 통과 끄기" : "HUD 마우스 통과 켜기";
        _trayHudClickThrough.Visibility = Hud.IsOpen || Hud.ClickThrough ? Visibility.Visible : Visibility.Collapsed;
        _trayMenu.IsOpen = false;
        _trayMenu.IsOpen = true;
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 200 };
        MenuItem Item(string header, SymbolRegular icon, Action click)
        {
            var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = icon } };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }

        _trayPlayPause = Item("재생", SymbolRegular.Play24, () => _playback.TogglePlayPauseCommand.Execute(null));
        Item("이전 곡", SymbolRegular.Previous24, () => _playback.PlayPreviousCommand.Execute(null));
        Item("다음 곡", SymbolRegular.Next24, () => _playback.PlayNextCommand.Execute(null));
        menu.Items.Add(new Separator());
        Item("열기", SymbolRegular.Open24, ShowFullFromTray).FontWeight = FontWeights.SemiBold;
        Item("미니 플레이어", SymbolRegular.PictureInPictureEnter24, () =>
        {
            if (_settings.PlayerMode == PlayerMode.Full)
            {
                EnterLastMiniMode();
                if (_mini is not null) BringUp(_mini);
            }
            else RestoreFromTray(); // brings the mini player up itself
        });
        _trayHud = Item("HUD 표시", SymbolRegular.Board24, () => Hud.Toggle());
        _trayHudClickThrough = Item("HUD 마우스 통과 켜기", SymbolRegular.CursorClick24, () => Hud.ClickThrough = !Hud.ClickThrough);
        menu.Items.Add(new Separator());
        Item("종료", SymbolRegular.Power24, ExitFromTray);

        // A menu opened from the tray belongs to no active window, so Windows wouldn't close it
        // on a click elsewhere: make its own popup the foreground window (the tray click gave
        // this app the right to).
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source)
                SetForegroundWindow(source.Handle);
        };
        return menu;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
