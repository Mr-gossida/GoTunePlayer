using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>The HUD on and off, and how solid it is - the same switch for the hotkey, the button
/// in the title bar, the card's own menu and 설정 › 재생.</summary>
public sealed partial class HudViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private HudWindow? _window;

    public HudViewModel(AppSettings settings, PlaybackViewModel playback)
    {
        _settings = settings;
        Playback = playback;
        _isOpen = false;
        _opacity = settings.Hud.Opacity;
    }

    public PlaybackViewModel Playback { get; }

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>0.2..1.</summary>
    [ObservableProperty]
    private double _opacity;

    public string OpacityText => $"{Math.Round(Opacity * 100)}%";

    /// <summary>Opens it if it was open when the app last closed.</summary>
    public void Restore()
    {
        if (_settings.Hud.Enabled) IsOpen = true;
    }

    [RelayCommand]
    public void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            _window ??= new HudWindow(this, _settings);
            _window.ShowHud();
        }
        else
        {
            _window?.HideHud();
        }
        _settings.Hud.Enabled = value;
        AppSettingsStore.Save(_settings);
    }

    partial void OnOpacityChanged(double value)
    {
        _settings.Hud.Opacity = Math.Clamp(value, HudSettings.MinOpacity, 1);
        OnPropertyChanged(nameof(OpacityText));
        _window?.ApplyOpacity();
        SaveSoon();
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>Dragging the slider: saved once it rests.</summary>
    private void SaveSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); AppSettingsStore.Save(_settings); },
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>The app is closing: the window goes with it (its open state stays saved).</summary>
    public void Shutdown()
    {
        _window?.CloseForGood();
        _window = null;
    }
}
