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

// Switching between this window and the mini player.
public partial class ShellWindow
{
    // --- mini player ---

    private MiniPlayerWindow? _mini;

    /// <summary>Set while the app opens minimized at Windows sign-in: that minimize must stay one.</summary>
    public bool StartingQuietly { get; set; }

    /// <summary>True once the app is shutting down (the mini player then closes for real).</summary>
    public bool IsClosing { get; private set; }

    private void MiniMode_Click(object sender, RoutedEventArgs e) =>
        EnterMiniMode(_mini?.Mode ?? PlayerMode.MiniHorizontal);

    /// <summary>Swaps this window for the mini player (playback continues untouched).</summary>
    public void EnterMiniMode(PlayerMode mode)
    {
        if (mode == PlayerMode.Full) { ExitMiniMode(); return; }
        ExitFocusMode(animate: false);
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
}
