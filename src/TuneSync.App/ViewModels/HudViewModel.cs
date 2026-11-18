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
        _clickThrough = settings.Hud.ClickThrough;
        _clickThroughButtons = settings.Hud.ClickThroughButtons;
        _clear = settings.Hud.Clear;
        _clearEffect = settings.Hud.ClearEffect;
        _layout = settings.Hud.Layout;
    }

    /// <summary>The card's layout. Each one keeps one size for every song.</summary>
    [ObservableProperty]
    private HudLayout _layout;

    partial void OnLayoutChanged(HudLayout value)
    {
        _settings.Hud.Layout = value;
        AppSettingsStore.Save(_settings);
    }

    /// <summary>클리어 모드: no card, just the HUD's contents with an outline (the opacity setting
    /// doesn't apply then).</summary>
    [ObservableProperty]
    private bool _clear;

    /// <summary>클리어 모드's 효과.</summary>
    [ObservableProperty]
    private HudClearEffect _clearEffect;

    public IReadOnlyList<Choice<HudClearEffect>> ClearEffectOptions { get; } =
    [
        new(HudClearEffect.Shadow, "그림자 · 부드럽게"),
        new(HudClearEffect.Neon, "네온 · 커버 색으로 은은하게"),
        new(HudClearEffect.Outline, "테두리 · 진하게"),
    ];

    partial void OnClearEffectChanged(HudClearEffect value)
    {
        _settings.Hud.ClearEffect = value;
        AppSettingsStore.Save(_settings);
        _window?.ApplyClear();
    }

    /// <summary>The opacity is the card's; with no card (클리어) it is set aside.</summary>
    public bool CanSetOpacity => !Clear;

    partial void OnClearChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSetOpacity));
        _settings.Hud.Clear = value;
        AppSettingsStore.Save(_settings);
        _window?.ApplyClear();
    }

    public PlaybackViewModel Playback { get; }

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>0.2..1.</summary>
    [ObservableProperty]
    private double _opacity;

    public string OpacityText => $"{Math.Round(Opacity * 100)}%";

    /// <summary>Clicks pass through the card to whatever is behind it. The card can't then be
    /// grabbed or used, so this is switched back off from 설정 or the tray menu.</summary>
    [ObservableProperty]
    private bool _clickThrough;

    partial void OnClickThroughChanged(bool value)
    {
        _settings.Hud.ClickThrough = value;
        AppSettingsStore.Save(_settings);
        _window?.ApplyClickThrough();
    }

    /// <summary>With click-through on, the playback buttons can still be pressed (and the card
    /// brightens while the mouse is over it); everywhere else clicks still go through.</summary>
    [ObservableProperty]
    private bool _clickThroughButtons;

    /// <summary>The hotkeys (설정 › 단축키): switch and say so - on the HUD itself for a moment
    /// (a click-through HUD gives no other sign), or as a notice when the HUD isn't showing.</summary>
    public void ToggleClear()
    {
        Clear = !Clear;
        Tell(Clear ? "클리어 모드 켬" : "클리어 모드 끔");
    }

    public void ToggleClickThrough()
    {
        ClickThrough = !ClickThrough;
        Tell(ClickThrough ? "마우스 통과 켬" : "마우스 통과 끔");
    }

    private void Tell(string text)
    {
        if (IsOpen && _window is not null) _window.Flash(text);
        else Toast.Info("HUD", text);
    }

    partial void OnClickThroughButtonsChanged(bool value)
    {
        _settings.Hud.ClickThroughButtons = value;
        AppSettingsStore.Save(_settings);
        _window?.ApplyClickThrough();
    }

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
