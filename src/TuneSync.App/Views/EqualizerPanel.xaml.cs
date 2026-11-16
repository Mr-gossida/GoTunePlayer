using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.ViewModels;

namespace TuneSync.App.Views;

/// <summary>The equalizer panel the 플레이 page shows in place of the lyrics (see the XAML for the
/// layout). This file: the player-colored accent, the wide/narrow layouts, the preset chips,
/// naming presets, the bypass note and the limiter lamp.</summary>
public partial class EqualizerPanel : UserControl
{
    /// <summary>Below this width the tone bars go into two columns above the bands.</summary>
    private const double WideFrom = 720;

    /// <summary>Below this width the dB reading over each band is left out.</summary>
    private const double ReadingsFrom = 560;

    private readonly EqualizerViewModel _viewModel;
    private readonly PlaybackViewModel _playback;
    private readonly DispatcherTimer _limiterTimer;
    private DateTime _limiterLitUntil;
    private bool _renaming;
    private readonly Core.Audio.LiveAnalyzer _analyzer = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame;
    private bool _drawnQuiet;
    private bool? _wide;

    public EqualizerPanel()
    {
        InitializeComponent();
        var services = ((App)Application.Current).Services;
        DataContext = _viewModel = services.GetRequiredService<EqualizerViewModel>();
        _playback = services.GetRequiredService<PlaybackViewModel>();

        // The limiter lamp: looked at a few times a second while the panel shows.
        _limiterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _limiterTimer.Tick += (_, _) => ShowLimiter();

        SizeChanged += (_, _) => ApplyLayout();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Start();
            else Stop();
        };
    }

    private void Start()
    {
        UseAccent();
        PlayerPalette.Changed -= UseAccent;
        PlayerPalette.Changed += UseAccent;
        _playback.PropertyChanged -= Playback_PropertyChanged;
        _playback.PropertyChanged += Playback_PropertyChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ShowBypass();
        ShowCustomChip();
        ApplyLayout();
        _limiterTimer.Start();
        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
        _lastFrame = 0;
        Dispatcher.BeginInvoke(BringChosenChipIntoView, DispatcherPriority.Loaded);
    }

    private void Stop()
    {
        PlayerPalette.Changed -= UseAccent;
        _playback.PropertyChanged -= Playback_PropertyChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _limiterTimer.Stop();
        CompositionTarget.Rendering -= OnRendering;
        CloseNameBar();
    }

    /// <summary>Wide: the four tone bars stacked on the left of the bands. Narrow: in two columns
    /// above them.</summary>
    private void ApplyLayout()
    {
        if (ActualWidth <= 0) return;
        // Too narrow for a reading over each band: the handles and the curve say it.
        BandReadings.Visibility = ActualWidth >= ReadingsFrom ? Visibility.Visible : Visibility.Collapsed;
        var wide = ActualWidth >= WideFrom;
        if (_wide == wide) return;
        _wide = wide;
        Grid.SetRow(ToneSection, wide ? 1 : 0);
        Grid.SetColumnSpan(ToneSection, wide ? 1 : 2);
        Grid.SetColumn(BandSection, wide ? 1 : 0);
        Grid.SetColumnSpan(BandSection, wide ? 1 : 2);
        ToneRow.Height = wide ? new GridLength(0) : GridLength.Auto;
        ToneSection.Margin = wide ? new Thickness(0, 0, 12, 0) : new Thickness(0, 0, 0, 10);
        ToneSection.Padding = wide ? new Thickness(20, 16, 20, 16) : new Thickness(8, 6, 8, 6);
        ToneSection.Width = wide ? 280 : double.NaN;
        // narrow: two bars side by side
        ToneBars.Columns = wide ? 1 : 2;
        foreach (var bar in new[] { BassBar, TrebleBar, WidthBar, BalanceBar })
            bar.Margin = wide ? new Thickness(0, 10, 0, 10) : new Thickness(8, 2, 8, 2);
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
        if (e.PropertyName != nameof(EqualizerViewModel.SelectedPreset)) return;
        ShowCustomChip();
        Dispatcher.BeginInvoke(BringChosenChipIntoView, DispatcherPriority.Loaded);
    }

    private void ShowBypass() => BypassNotice.Visibility = _playback.IsToneBypassed ? Visibility.Visible : Visibility.Collapsed;

    private void ShowCustomChip() => CustomChip.Visibility = _viewModel.SelectedPreset is null ? Visibility.Visible : Visibility.Collapsed;

    private void BringChosenChipIntoView()
    {
        if (_viewModel.SelectedPreset is { } preset && PresetChips.ItemContainerGenerator.ContainerFromItem(preset) is FrameworkElement chip)
            chip.BringIntoView();
    }

    /// <summary>The wheel scrolls the chip strip sideways.</summary>
    private void ChipScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChipScroller.ScrollToHorizontalOffset(ChipScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    /// <summary>The live spectrum and the meters: a new picture with each screen frame (at most
    /// ~60 a second), from what is being heard right now. Once all has sunk to nothing (paused,
    /// stopped) it stops redrawing until sound comes back.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var seconds = now - _lastFrame;
        if (_lastFrame > 0 && seconds < 0.014) return;
        _lastFrame = now;
        _analyzer.Update(_playback.OutputScope, Spectrum.Columns, EqCurve.LowHz, EqCurve.HighHz, seconds);
        if (_analyzer.IsQuiet && _drawnQuiet) return;
        _drawnQuiet = _analyzer.IsQuiet;
        Spectrum.Show(_analyzer);
        Meter.Show(_analyzer);
    }

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

    // --- naming a preset: the action buttons give way to a name box ---

    private void Save_Click(object sender, RoutedEventArgs e) => OpenNameBar(renaming: false);

    private void Rename_Click(object sender, RoutedEventArgs e) => OpenNameBar(renaming: true);

    private void OpenNameBar(bool renaming)
    {
        _renaming = renaming;
        // Saving over a preset of one's own is the usual wish: offer its name.
        NameBox.Text = _viewModel.SelectedPreset is { IsBuiltIn: false } preset ? preset.Name : "";
        NameConfirm.Content = renaming ? "바꾸기" : "저장";
        PresetActions.Visibility = Visibility.Collapsed;
        NameBar.Visibility = Visibility.Visible;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void CloseNameBar()
    {
        NameBar.Visibility = Visibility.Collapsed;
        PresetActions.Visibility = Visibility.Visible;
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
