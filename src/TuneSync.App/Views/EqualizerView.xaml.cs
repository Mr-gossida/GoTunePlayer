using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>The 이퀄라이저 window's page: switch and presets on top, the response curve over the
/// band sliders, the tone controls, and the headroom/limiter line. Takes the player's colors
/// like 설정 does.</summary>
public partial class EqualizerView : Page
{
    private readonly EqualizerViewModel _viewModel;
    private readonly PlaybackViewModel _playback;
    private readonly DispatcherTimer _limiterTimer;
    private DateTime _limiterLitUntil;
    private bool _renaming;

    public EqualizerView(EqualizerViewModel viewModel, PlaybackViewModel playback)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _playback = playback;

        // The limiter lamp: looked at a few times a second while the page is open.
        _limiterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _limiterTimer.Tick += (_, _) => ShowLimiter();

        Loaded += (_, _) =>
        {
            UseAccent();
            PlayerPalette.Changed += UseAccent;
            _playback.PropertyChanged += Playback_PropertyChanged;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            ShowBypass();
            ShowCustomLabel();
            _limiterTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            PlayerPalette.Changed -= UseAccent;
            _playback.PropertyChanged -= Playback_PropertyChanged;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _limiterTimer.Stop();
            CloseNameBar();
        };
    }

    /// <summary>The Windows accent the switch and boxes use, swapped for the player's (as in 설정).</summary>
    private void UseAccent()
    {
        if (Application.Current.TryFindResource(PlayerPalette.AccentKey) is not Brush accent ||
            Application.Current.TryFindResource(PlayerPalette.OnAccentKey) is not Brush onAccent) return;
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
                                    "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "SystemAccentColorPrimaryBrush" })
            Resources[key] = accent;
        Resources["TextOnAccentFillColorPrimaryBrush"] = onAccent;
        Resources["TextOnAccentFillColorSecondaryBrush"] = onAccent;
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.IsToneBypassed) or nameof(PlaybackViewModel.CurrentFile)) ShowBypass();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EqualizerViewModel.SelectedPreset)) ShowCustomLabel();
    }

    private void ShowBypass() => BypassNotice.Visibility = _playback.IsToneBypassed ? Visibility.Visible : Visibility.Collapsed;

    private void ShowCustomLabel() => CustomLabel.Visibility = _viewModel.SelectedPreset is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Lit (with how much it is holding back) while the limiter works, and for a moment
    /// after, so a single peak can be seen.</summary>
    private void ShowLimiter()
    {
        var reduction = _playback.TakeLimiterReductionDb();
        if (reduction > 0.1)
        {
            _limiterLitUntil = DateTime.UtcNow.AddMilliseconds(700);
            LimiterText.Text = $"리미터 −{reduction:0.0} dB";
            LimiterLamp.Opacity = 1;
        }
        else if (LimiterLamp.Opacity > 0.5 && DateTime.UtcNow > _limiterLitUntil)
        {
            LimiterText.Text = "리미터";
            LimiterLamp.Opacity = 0.22;
        }
    }

    // --- naming a preset: the preset box gives way to a name box ---

    private void Save_Click(object sender, RoutedEventArgs e) => OpenNameBar(renaming: false);

    private void Rename_Click(object sender, RoutedEventArgs e) => OpenNameBar(renaming: true);

    private void OpenNameBar(bool renaming)
    {
        _renaming = renaming;
        // Saving over a preset of one's own is the usual wish: offer its name.
        NameBox.Text = _viewModel.SelectedPreset is { IsBuiltIn: false } preset ? preset.Name : "";
        NameConfirm.Content = renaming ? "바꾸기" : "저장";
        PresetBar.Visibility = Visibility.Collapsed;
        NameBar.Visibility = Visibility.Visible;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void CloseNameBar()
    {
        NameBar.Visibility = Visibility.Collapsed;
        PresetBar.Visibility = Visibility.Visible;
    }

    private void NameCancel_Click(object sender, RoutedEventArgs e) => CloseNameBar();

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConfirmName();
        else if (e.Key == Key.Escape) CloseNameBar();
        else return;
        e.Handled = true;
    }

    private void NameConfirm_Click(object sender, RoutedEventArgs e) => ConfirmName();

    private async void ConfirmName()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        if (_renaming)
        {
            if (!_viewModel.RenamePreset(name))
            {
                Toast.Warning("이름을 바꿀 수 없습니다", "이미 있는 프리셋 이름입니다.");
                return;
            }
        }
        else
        {
            if (Core.Audio.EqPresets.IsBuiltInName(name))
            {
                Toast.Warning("저장할 수 없습니다", "기본 프리셋과 같은 이름은 쓸 수 없습니다.");
                return;
            }
            var own = _viewModel.SelectedPreset is { IsBuiltIn: false } current && current.Name == name;
            if (!own && _viewModel.HasUserPreset(name)
                && !await Dialogs.ConfirmAsync("프리셋 덮어쓰기", $"'{name}' 프리셋이 이미 있습니다. 지금 설정으로 바꿀까요?", "덮어쓰기"))
                return;
            _viewModel.SavePreset(name);
        }
        CloseNameBar();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPreset is not { IsBuiltIn: false } preset) return;
        if (await Dialogs.ConfirmAsync("프리셋 삭제", $"'{preset.Name}' 프리셋을 삭제할까요? 지금 소리는 그대로 유지됩니다.", "삭제"))
            _viewModel.DeletePreset();
    }
}
