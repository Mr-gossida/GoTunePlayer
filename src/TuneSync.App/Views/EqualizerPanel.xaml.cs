using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    /// <summary>Below this width the tone bars go under the bands.</summary>
    private const double WideFrom = 720;

    /// <summary>Under the bands, from this width the four bars fit in one row.</summary>
    private const double FourAcrossFrom = 440;

    /// <summary>The tone section's width beside the bands, and the gap between them.</summary>
    private const double ToneWidth = 280, SectionGap = 12;

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

    public EqualizerPanel()
    {
        InitializeComponent();
        var services = ((App)Application.Current).Services;
        DataContext = _viewModel = services.GetRequiredService<EqualizerViewModel>();
        _playback = services.GetRequiredService<PlaybackViewModel>();

        // The limiter lamp: looked at a few times a second while the panel shows.
        _limiterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _limiterTimer.Tick += (_, _) => ShowLimiter();

        ReverbCard.CustomPopupPlacementCallback = PlaceReverbCard;

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
        ReverbCard.IsOpen = false;
    }

    /// <summary>The reverb card opens beside its button, out over the bands, bottom edges in line
    /// (the button sits at the bottom of the tone card); where there's no room there, above the
    /// button. The sizes come in screen pixels and without the card's margin (the room for its
    /// shadow), so that margin is added here, scaled to the screen.</summary>
    private CustomPopupPlacement[] PlaceReverbCard(Size popup, Size target, Point offset)
    {
        var scale = VisualTreeHelper.GetDpi(ReverbOpener).DpiScaleX;
        double margin = 12 * scale, gap = 8 * scale;
        return
        [
            new CustomPopupPlacement(new Point(target.Width + gap - margin, target.Height - popup.Height - margin), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(-margin, -popup.Height - gap - margin), PopupPrimaryAxis.Vertical),
        ];
    }

    /// <summary>Wide: the tone bars on the left of the bands. Narrow: under them. Either way
    /// fitted to the height there is, the bands coming first: the roomiest arrangement that fits
    /// (bars stacked, the level meter, a dB reading over each band), else more compact ones
    /// (bars side by side, no meter, no readings). Only when even the last is too tall does the
    /// body scroll (the bands at the top).</summary>
    private void ApplyLayout()
    {
        double width = BodyScroller.ActualWidth, height = BodyScroller.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var wide = ActualWidth >= WideFrom;
        var readings = ActualWidth >= ReadingsFrom;
        var across = wide ? 2 : width >= FourAcrossFrom ? 4 : 2;
        // Stacked switches (one per row) where the tone card is beside the bands; side by side
        // when it is short of height, and always under the bands (it's wide there).
        // The last one also drops the dB readings (the preamp's too): the handles show the levels.
        (int Columns, bool Meter, bool Stacked, bool Readings)[] tries = wide
            ? [(1, true, true, true), (2, true, true, true), (2, false, true, true), (2, false, false, true), (2, false, false, false)]
            : [(2, true, false, true), (across, true, false, true), (across, false, false, true), (across, false, false, false)];

        Grid.SetColumn(ToneSection, 0);
        Grid.SetRow(ToneSection, wide ? 0 : 1);
        Grid.SetColumnSpan(ToneSection, wide ? 1 : 2);
        Grid.SetColumn(BandSection, wide ? 1 : 0);
        Grid.SetColumnSpan(BandSection, wide ? 1 : 2);
        BottomRow.Height = wide ? new GridLength(0) : GridLength.Auto;
        ToneSection.Margin = wide ? new Thickness(0, 0, SectionGap, 0) : new Thickness(0, 8, 0, 0);
        ToneSection.Padding = wide ? new Thickness(20, 16, 20, 16) : new Thickness(8, 4, 8, 4);
        ToneSection.Width = wide ? ToneWidth : double.NaN;

        var need = 0.0;
        foreach (var (columns, meter, stacked, showReadings) in tries)
        {
            ArrangeSwitches(stacked, wide ? ToneWidth - 40 : width - 16);
            ToneBars.Columns = columns;
            foreach (var bar in new[] { BassBar, TrebleBar, WidthBar, BalanceBar })
                bar.Margin = columns == 1 ? new Thickness(0, 10, 0, 10) : new Thickness(4, 2, 4, 2);
            MeterBlock.Visibility = meter ? Visibility.Visible : Visibility.Collapsed;
            // a reading over each band also needs the width for it
            BandReadings.Visibility = showReadings && readings ? Visibility.Visible : Visibility.Collapsed;
            PreampReading.Visibility = showReadings ? Visibility.Visible : Visibility.Collapsed;
            // what this arrangement takes, measured with no limit on height (a parent keeps its
            // last size until told its children changed)
            Remeasure(ToneBars, ToneSection);
            Remeasure(MeterBlock, ToneSection);
            Remeasure(Switches, ToneSection);
            Remeasure(BandReadings, BandSection);
            Remeasure(PreampReading, BandSection);
            var bandWidth = wide ? width - ToneWidth - SectionGap : width;
            BandSection.Measure(new Size(Math.Max(0, bandWidth), double.PositiveInfinity));
            ToneSection.Measure(new Size(width, double.PositiveInfinity));
            double bands = BandSection.DesiredSize.Height, tone = ToneSection.DesiredSize.Height;
            need = wide ? Math.Max(bands, tone) : bands + tone;
            if (need <= height) break;
        }
        // set as the body's least height: within it the bands take what is left over
        Body.MinHeight = need;
    }

    /// <summary>The limiter, headroom and reverb switches: stacked, a row each with its reading
    /// beside it, or side by side; side by side, the readings stay only if each has the room.
    /// A pill keeps one width (all three the same) so the readings line up when stacked.</summary>
    private void ArrangeSwitches(bool stacked, double width)
    {
        Switches.Columns = stacked ? 1 : 3;
        var readings = stacked || width / 3 >= SwitchWithReadingWidth;
        // stacked, one width so the readings line up; side by side, each as wide as its name
        foreach (var pill in new[] { LimiterPill, HeadroomPill, ReverbPill })
            pill.Width = stacked ? PillWidth : double.NaN;
        var shown = readings ? Visibility.Visible : Visibility.Collapsed;
        LimiterReading.Visibility = shown;
        HeadroomReading.Visibility = shown;
        ReverbSummary.Visibility = shown;
    }

    /// <summary>A switch's pill, and a pill with the reading beside it.</summary>
    private const double PillWidth = 86, SwitchWithReadingWidth = 150;

    private static void Remeasure(DependencyObject from, UIElement upTo)
    {
        for (var at = from; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            (at as UIElement)?.InvalidateMeasure();
            if (at == upTo) break;
        }
    }

    private void BodyScroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    // --- the preset chips: arrows and faded edges when there are more than fit ---

    private void ChipScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => ShowChipArrows();

    private void ShowChipArrows()
    {
        var offset = ChipScroller.HorizontalOffset;
        var back = offset > 1;
        var on = offset < ChipScroller.ScrollableWidth - 1;
        ChipsBack.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        ChipsOn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var width = ChipScroller.ActualWidth;
        if (!back && !on || width <= 0)
        {
            ChipScroller.OpacityMask = null;
            return;
        }
        var fade = Math.Min(0.3, 64 / width);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(back ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, back ? fade : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, on ? 1 - fade : 1));
        mask.GradientStops.Add(new GradientStop(on ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        ChipScroller.OpacityMask = mask;
    }

    private void ChipsBack_Click(object sender, RoutedEventArgs e) =>
        ChipScroller.ScrollToHorizontalOffset(ChipScroller.HorizontalOffset - ChipScroller.ViewportWidth * 0.7);

    private void ChipsOn_Click(object sender, RoutedEventArgs e) =>
        ChipScroller.ScrollToHorizontalOffset(ChipScroller.HorizontalOffset + ChipScroller.ViewportWidth * 0.7);

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
        if (!_viewModel.Limiter)
        {
            // Switched off: nothing to show, not even the tail of a reduction that is fading out.
            reduction = 0;
            _limiterLitUntil = default;
        }
        if (reduction > 0.1)
        {
            _limiterLitUntil = DateTime.UtcNow.AddMilliseconds(700);
            LimiterReading.Text = $"−{reduction:0.0} dB";
            LimiterLamp.Tag = true;
        }
        else if (LimiterLamp.Tag is true && DateTime.UtcNow > _limiterLitUntil)
        {
            LimiterReading.Text = "";
            LimiterLamp.Tag = false;
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
