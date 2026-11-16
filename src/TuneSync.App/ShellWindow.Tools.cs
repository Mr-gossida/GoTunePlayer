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

// The tools that open in their own windows (tag manager, merge, rename, settings).
public partial class ShellWindow
{
    private void OpenTagManager_Click(object sender, RoutedEventArgs e) => OpenTool<LibraryView>("태그 관리", 1360, 820);

    private void OpenMerge_Click(object sender, RoutedEventArgs e) => OpenTool<MergeView>("폴더 합치기", 1100, 760);

    private void OpenRename_Click(object sender, RoutedEventArgs e) => OpenTool<RenameView>("이름 변경", 1100, 760);

    private void StartScreenSaver_Click(object sender, RoutedEventArgs e) => ScreenSaver.Show();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenTool<SettingsView>("설정", 820, 720, playerColors: true);

    /// <summary>The playback bar's button: the equalizer in the lyrics' place, or back to the lyrics.</summary>
    private void OpenEqualizer_Click(object sender, RoutedEventArgs e)
    {
        var equalizer = _services.GetRequiredService<EqualizerViewModel>();
        equalizer.IsPanelOpen = !equalizer.IsPanelOpen;
    }

    /// <summary>설정 › 음향's "이퀄라이저 열기": the player comes to the front with the equalizer
    /// showing (the full player, if a mini player was up).</summary>
    public void OpenEqualizer()
    {
        if (!IsVisible) ExitMiniMode();
        ExitFocusMode(animate: false);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _services.GetRequiredService<EqualizerViewModel>().IsPanelOpen = true;
    }

    /// <summary>The queue's "태그 관리에서 보기": opens (or raises) the tag manager on that song.</summary>
    public void ShowInTagManager(LibraryFileViewModel file)
    {
        OpenTool<LibraryView>("태그 관리", 1360, 820);
        _services.GetRequiredService<LibraryView>().ShowFile(file);
    }

    /// <summary>Opens a tool in its own window, or brings it to the front if it's already open.</summary>
    private void OpenTool<TPage>(string title, double width, double height, bool playerColors = false) where TPage : Page
    {
        if (_openTools.TryGetValue(typeof(TPage), out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new ToolWindow(_services.GetRequiredService<TPage>(), title, width, height, playerColors) { Owner = this };
        window.Closed += (_, _) => _openTools.Remove(typeof(TPage));
        _openTools[typeof(TPage)] = window;
        window.Show();
    }
}
