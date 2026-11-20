using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;
using TuneSync.App.Views;

namespace TuneSync.App;

// 소리 경로: the playing song's way from the file to the device, in a panel over the playback
// bar. Opened by clicking the output badges, or from the cover card; it follows changes (a new
// song, the equalizer, the volume, the output) while it is open.
public partial class ShellWindow
{
    private SignalPathViewModel? _signalPath;
    private DispatcherTimer? _signalTimer;
    private DateTime _signalClosedAt;

    private SignalPathViewModel SignalPath
    {
        get
        {
            if (_signalPath is not null) return _signalPath;
            _signalPath = new SignalPathViewModel(_playback, _settings)
            {
                OpenEqualizer = () => { SignalPathPopup.IsOpen = false; OpenEqualizer(); },
                OpenOutputSettings = () =>
                {
                    SignalPathPopup.IsOpen = false;
                    OpenTool<SettingsView>("설정", 820, 720, playerColors: true);
                    _services.GetRequiredService<SettingsView>().SelectCategory("Sound");
                },
            };
            SignalPathPanel.DataContext = _signalPath;
            return _signalPath;
        }
    }

    private void SignalBadges_Click(object sender, MouseButtonEventArgs e)
    {
        // the click that closed it (StaysOpen=False) also lands here: don't open it right again
        if (!SignalPathPopup.IsOpen && (DateTime.UtcNow - _signalClosedAt).TotalMilliseconds < 250) return;
        ToggleSignalPath();
        e.Handled = true;
    }

    private void CardSignalPath_Click(object sender, RoutedEventArgs e)
    {
        NowPlayingCard.IsOpen = false;
        // after the card has gone, so the click that closed it doesn't close this one too
        Dispatcher.BeginInvoke(() => SignalPathPopup.IsOpen = true, DispatcherPriority.Input);
    }

    private void ToggleSignalPath() => SignalPathPopup.IsOpen = !SignalPathPopup.IsOpen;

    private void SignalPathPanel_CloseRequested(object? sender, EventArgs e) => SignalPathPopup.IsOpen = false;

    private void SignalPathPopup_Opened(object? sender, EventArgs e)
    {
        SignalPath.Refresh();
        _playback.PropertyChanged += Playback_SignalChanged;
        // the volume, the glitch count and the like change without a notice: look again now and then
        _signalTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SignalPath.Refresh(), Dispatcher);
        _signalTimer.Start();
    }

    private void SignalPathPopup_Closed(object? sender, EventArgs e)
    {
        _signalClosedAt = DateTime.UtcNow;
        _playback.PropertyChanged -= Playback_SignalChanged;
        _signalTimer?.Stop();
    }

    private void Playback_SignalChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.OutputDescription) or nameof(PlaybackViewModel.CurrentFile)
            or nameof(PlaybackViewModel.CurrentQuality) or nameof(PlaybackViewModel.Volume) or nameof(PlaybackViewModel.IsMuted))
            SignalPath.Refresh();
    }

    /// <summary>Esc closes the panel (it takes no keyboard focus of its own).</summary>
    private bool CloseSignalPathOnEscape(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !SignalPathPopup.IsOpen) return false;
        SignalPathPopup.IsOpen = false;
        e.Handled = true;
        return true;
    }
}
