using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Audio;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>One slider of the equalizer.</summary>
public sealed partial class EqBandViewModel(int index, string name, bool labeled, double gain, Action<EqBandViewModel> changed) : ObservableObject
{
    public int Index { get; } = index;

    /// <summary>"31", "1.25k", "16k".</summary>
    public string Name { get; } = name;

    /// <summary>The name under the slider: every band's of the ten; of the 31, only the ones an
    /// octave apart (31, 63, 125 ... 16k), as on a hardware 31-band equalizer - the others are
    /// read off while the pointer is on them.</summary>
    public string Label { get; } = labeled ? name : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText), nameof(HotText), nameof(ToolTip))]
    private double _gain = gain;

    public string GainText => EqualizerViewModel.FormatDb(Gain);

    /// <summary>"1.25k Hz  +3.0", over the 31 bands while the pointer is on one or it is being set.</summary>
    public string HotText => $"{Name}Hz  {GainText}";

    public string ToolTip => $"{Name}Hz · {GainText} dB (끌기 · 휠 · 더블클릭하면 0)";

    partial void OnGainChanged(double value) => changed(this);
}

/// <summary>An entry of the preset list: a built-in, or one the user saved.</summary>
public sealed record EqPresetChoice(string Key, string Name, bool IsBuiltIn);

/// <summary>The equalizer and tone controls (the 이퀄라이저 window and 설정 › 음향): holds what the
/// user set, saves it, and hands the playing song a new <see cref="ToneConfig"/> on every change.</summary>
public sealed partial class EqualizerViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ToneSettings _tone;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;

    public EqualizerViewModel(AppSettings settings)
    {
        _settings = settings;
        _tone = settings.Tone;
        _tone.Normalize();
        Config = ToneConfig.From(_tone);

        _isThirdOctave = _tone.BandCount == ToneSettings.ThirdOctaveBands;
        BuildBands();
        _isEnabled = _tone.Enabled;
        _preamp = _tone.PreampDb;
        _bass = _tone.BassDb;
        _treble = _tone.TrebleDb;
        _width = _tone.Width;
        _balance = _tone.Balance;
        _autoHeadroom = _tone.AutoHeadroom;
        _limiter = _tone.Limiter;
        _reverb = _tone.Reverb;
        _reverbRoom = ReverbRooms.Find(_tone.ReverbRoom);
        _reverbAmount = _tone.ReverbAmount;
        _reverbDecay = _tone.ReverbDecay;
        RebuildPresets();
        _selectedPreset = Presets.FirstOrDefault(p => p.Key == _tone.Preset);

        // A slider drag is dozens of changes: write the settings once it rests.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            AppSettingsStore.Save(_settings);
        };
    }

    /// <summary>What the player should be doing now.</summary>
    public ToneConfig Config { get; private set; }

    /// <summary>Raised (on the UI thread) whenever <see cref="Config"/> changes.</summary>
    public event Action<ToneConfig>? ConfigChanged;

    /// <summary>The settings as they are, for drawing the response curve.</summary>
    public ToneSettings Settings => _tone;

    public ObservableCollection<EqBandViewModel> Bands { get; } = [];

    /// <summary>31 bands a third of an octave apart instead of ten an octave apart (the switch
    /// in the panel's title row). The curve is kept across a switch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandCount), nameof(IsOctave))]
    private bool _isThirdOctave;

    public bool IsOctave => !IsThirdOctave;

    public int BandCount => IsThirdOctave ? ToneSettings.ThirdOctaveBands : ToneSettings.OctaveBands;

    [RelayCommand]
    private void SetBandCount(string count) => IsThirdOctave = count == "31";

    // The last switch: from what, and what it made. Going straight back, with nothing moved in
    // between, restores the bands as they were instead of reading them off the coarser curve
    // (31 -> 10 -> 31 would otherwise lose the detail between the ten).
    private (int Count, double[] Before, double[] After)? _lastSwitch;
    public ObservableCollection<EqPresetChoice> Presets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip), nameof(IsReverbWorking))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreampText))]
    private double _preamp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BassText))]
    private double _bass;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrebleText))]
    private double _treble;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WidthText))]
    private double _width;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceText), nameof(BalanceShortText))]
    private double _balance;

    [ObservableProperty]
    private bool _autoHeadroom;

    [ObservableProperty]
    private bool _limiter;

    /// <summary>The rooms the reverb offers (the chips in its card).</summary>
    public IReadOnlyList<ReverbRoom> ReverbRoomChoices => ReverbRooms.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReverbWorking))]
    private bool _reverb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReverbSummary), nameof(ReverbDecayText))]
    private ReverbRoom _reverbRoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReverbSummary), nameof(ReverbAmountText))]
    private double _reverbAmount;

    /// <summary>The room's decay, scaled (0.5..2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReverbDecayText))]
    private double _reverbDecay;

    /// <summary>The reverb is adding its room (its lamp is lit).</summary>
    public bool IsReverbWorking => IsEnabled && Reverb;

    public string ReverbAmountText => $"{ReverbAmount * 100:0}%";

    /// <summary>"1.7초" - how long the tail takes to die away.</summary>
    public string ReverbDecayText => $"{ReverbRoom.DecaySeconds * ReverbDecay:0.0}초";

    /// <summary>"홀 · 20%", beside the switch.</summary>
    public string ReverbSummary => $"{ReverbRoom.Name} · {ReverbAmountText}";

    /// <summary>Null while the bands don't match a preset ("사용자 지정").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresetName), nameof(CanEditPreset))]
    private EqPresetChoice? _selectedPreset;

    /// <summary>Goes up on every change that moves the curve (the curve control redraws on it).</summary>
    [ObservableProperty]
    private int _curveVersion;

    public string PreampText => FormatDb(Preamp);
    public string BassText => FormatDb(Bass);
    public string TrebleText => FormatDb(Treble);
    public string WidthText => Width < 0.005 ? "모노" : $"{Width * 100:0}%";
    public string BalanceText => Math.Abs(Balance) < 0.005 ? "가운데" : Balance < 0 ? $"왼쪽 {-Balance * 100:0}" : $"오른쪽 {Balance * 100:0}";
    public string PresetName => SelectedPreset?.Name ?? "사용자 지정";

    /// <summary>The balance as it fits beside its bar: 좌 40 / 가운데 / 우 40.</summary>
    public string BalanceShortText => Math.Abs(Balance) < 0.005 ? "가운데" : Balance < 0 ? $"좌 {-Balance * 100:0}" : $"우 {Balance * 100:0}";

    /// <summary>The equalizer is showing in place of the lyrics on the 플레이 page (the playback
    /// bar's button and 설정 › 음향 open it).</summary>
    [ObservableProperty]
    private bool _isPanelOpen;

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    /// <summary>The panel's own 닫기: back to what was beside the cover before.</summary>
    [RelayCommand]
    private void ClosePanel() => IsPanelOpen = false;

    /// <summary>Rename/delete apply to saved presets only.</summary>
    public bool CanEditPreset => SelectedPreset is { IsBuiltIn: false };

    /// <summary>The headroom is on and lowering the overall level (its lamp is lit).</summary>
    public bool IsHeadroomWorking => Config.HeadroomDb > 0.05;

    /// <summary>"−4.2 dB" - how much the headroom lowers the overall level to make room for the
    /// boosts; empty while it does nothing.</summary>
    public string HeadroomText => IsHeadroomWorking ? $"−{Config.HeadroomDb:0.0} dB" : "";

    public string ToolTip => IsEnabled ? $"이퀄라이저 · 켜짐 ({PresetName})" : "이퀄라이저";

    public static string FormatDb(double value) =>
        Math.Abs(value) < 0.05 ? "0" : value > 0 ? $"+{value:0.0}" : $"−{-value:0.0}";

    partial void OnIsEnabledChanged(bool value) { _tone.Enabled = value; Publish(); }
    partial void OnPreampChanged(double value) { _tone.PreampDb = value; BandsEdited(); }
    partial void OnBassChanged(double value) { _tone.BassDb = value; Publish(); }
    partial void OnTrebleChanged(double value) { _tone.TrebleDb = value; Publish(); }
    partial void OnWidthChanged(double value) { _tone.Width = value; Publish(); }
    partial void OnBalanceChanged(double value) { _tone.Balance = value; Publish(); }
    partial void OnAutoHeadroomChanged(bool value) { _tone.AutoHeadroom = value; Publish(); }
    partial void OnLimiterChanged(bool value) { _tone.Limiter = value; Publish(); }
    partial void OnReverbChanged(bool value) { _tone.Reverb = value; Publish(); }
    // Picking a room or moving a reverb control is asking to hear it: it switches the reverb on.
    partial void OnReverbRoomChanged(ReverbRoom value) { _tone.ReverbRoom = value.Id; ReverbEdited(); }
    partial void OnReverbAmountChanged(double value) { _tone.ReverbAmount = value; ReverbEdited(); }
    partial void OnReverbDecayChanged(double value) { _tone.ReverbDecay = value; ReverbEdited(); }

    private void ReverbEdited()
    {
        if (_loading) return;
        if (!Reverb) Reverb = true; // publishes
        else Publish();
    }

    partial void OnIsThirdOctaveChanged(bool value)
    {
        var count = BandCount;
        if (_tone.BandCount == count) return;
        var before = (Count: _tone.BandCount, Bands: _tone.BandsDb.ToArray());
        if (_lastSwitch is { } last && last.Count == count && last.After.AsSpan().SequenceEqual(_tone.BandsDb))
        {
            _tone.BandCount = count;
            _tone.BandsDb = [.. last.Before];
        }
        else
        {
            _tone.ChangeBandCount(count);
        }
        _lastSwitch = (before.Count, before.Bands, _tone.BandsDb.ToArray());
        BuildBands();
        // the preset (if one is chosen) stays: the bands are still its curve
        Publish();
    }

    /// <summary>A slider per band of the current set.</summary>
    private void BuildBands()
    {
        var third = _tone.BandCount == ToneSettings.ThirdOctaveBands;
        Bands.Clear();
        for (var i = 0; i < _tone.BandsDb.Length; i++)
        {
            var name = third ? ToneCurve.ThirdOctaveNames[i] : OctaveName(ToneCurve.BandFrequencies[i]);
            Bands.Add(new EqBandViewModel(i, name, labeled: !third || i % 3 == 2, _tone.BandsDb[i], OnBandChanged));
        }
    }

    private static string OctaveName(double frequency) =>
        frequency >= 1000 ? $"{frequency / 1000:0.#}k" : $"{Math.Round(frequency):0}";

    partial void OnSelectedPresetChanged(EqPresetChoice? value)
    {
        OnPropertyChanged(nameof(ToolTip));
        if (_loading || value is null) return;
        if (EqPresets.Apply(_tone, value.Key)) LoadBands();
        Publish();
    }

    private void OnBandChanged(EqBandViewModel band)
    {
        if (_loading) return;
        _tone.BandsDb[band.Index] = band.Gain;
        BandsEdited();
    }

    /// <summary>A band or the preamp was moved by hand: no longer the preset it started from.</summary>
    private void BandsEdited()
    {
        if (_loading) return;
        _tone.Preset = null;
        _loading = true;
        SelectedPreset = null;
        _loading = false;
        Publish();
    }

    /// <summary>The sliders, from the settings (after a preset was applied).</summary>
    private void LoadBands()
    {
        _loading = true;
        foreach (var band in Bands) band.Gain = _tone.BandsDb[band.Index];
        Preamp = _tone.PreampDb;
        _loading = false;
    }

    private void Publish()
    {
        if (_loading) return;
        Config = ToneConfig.From(_tone);
        CurveVersion++;
        OnPropertyChanged(nameof(IsHeadroomWorking));
        OnPropertyChanged(nameof(HeadroomText));
        ConfigChanged?.Invoke(Config);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void RebuildPresets()
    {
        Presets.Clear();
        foreach (var preset in EqPresets.BuiltIn) Presets.Add(new EqPresetChoice(preset.Id, preset.Name, true));
        foreach (var preset in _tone.UserPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Presets.Add(new EqPresetChoice(preset.Name, preset.Name, false));
    }

    private void SelectWithoutApplying(string? key)
    {
        _loading = true;
        RebuildPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Key == key);
        _loading = false;
        OnPropertyChanged(nameof(ToolTip));
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    [RelayCommand]
    private void ToggleEnabled() => IsEnabled = !IsEnabled;

    /// <summary>Everything back to the untouched sound (the switch stays as it is).</summary>
    [RelayCommand]
    private void Reset()
    {
        _loading = true;
        Bass = Treble = Balance = 0;
        Width = 1;
        ReverbRoom = ReverbRooms.Find(null);
        ReverbAmount = ToneSettings.DefaultReverbAmount;
        ReverbDecay = 1;
        Reverb = false;
        _loading = false;
        SelectedPreset = null; // so that picking 플랫 applies even when it was already selected
        SelectedPreset = Presets[0];
    }

    /// <summary>Keeps the current bands under a name. False (with nothing saved) when the name
    /// is empty or belongs to a built-in preset.</summary>
    public bool SavePreset(string name)
    {
        if (!EqPresets.Save(_tone, name)) return false;
        SelectWithoutApplying(_tone.Preset);
        return true;
    }

    public bool RenamePreset(string newName)
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset || !EqPresets.Rename(_tone, preset.Key, newName)) return false;
        SelectWithoutApplying(_tone.Preset);
        return true;
    }

    public void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset || !EqPresets.Delete(_tone, preset.Key)) return;
        SelectWithoutApplying(null);
    }

    /// <summary>Whether a saved preset of this name exists already (saving would replace it).</summary>
    public bool HasUserPreset(string name) => _tone.UserPresets.Any(p => p.Name == name.Trim());

}
