using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Audio;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>설정 › 음향 › 출력: the playback device, the speaker layout, exclusive mode and its
/// sample rate and bit depth. Only what the chosen device takes is offered (asked of the device
/// when the page opens and when another device is picked). A change is heard at once: the playing
/// song moves to the new output from where it was.</summary>
public sealed partial class OutputViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly PlaybackViewModel _playback;
    private bool _loading;
    private int _deviceChannels = 2;
    private IReadOnlyList<DeviceFormat> _formats = [];
    private readonly Dictionary<string, IReadOnlyList<DeviceFormat>> _formatCache = [];

    public OutputViewModel(AppSettings settings, PlaybackViewModel playback)
    {
        _settings = settings;
        _playback = playback;
        _deviceId = settings.Output.DeviceId ?? DefaultKey;
        _layout = settings.Output.Layout;
        _exclusive = settings.Output.Exclusive;
        _bitPerfect = settings.Output.BitPerfect;
        _sampleRate = settings.Output.ExclusiveSampleRate;
        _bits = settings.Output.ExclusiveBits;
        _upsampling = settings.Output.Upsampling;
        _filter = settings.Output.Filter;
        _hardwareVolume = settings.Output.HardwareVolume;
        playback.PropertyChanged += Playback_PropertyChanged;
    }

    /// <summary>The device list's key for "Windows' default device" (a combo box can't select
    /// an item whose value is null).</summary>
    private const string DefaultKey = "";

    public ObservableCollection<Choice<string>> Devices { get; } = [];
    public ObservableCollection<Choice<SpeakerLayout>> Layouts { get; } = [];
    public ObservableCollection<Choice<int>> SampleRates { get; } = [];
    public ObservableCollection<Choice<int>> BitDepths { get; } = [];

    [ObservableProperty]
    private string _deviceId;

    [ObservableProperty]
    private SpeakerLayout _layout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpsample), nameof(UpsampleNote))]
    private bool _exclusive;

    public IReadOnlyList<Choice<UpsampleMode>> UpsampleModes { get; } =
    [
        new(UpsampleMode.Off, "끔"),
        new(UpsampleMode.Double, "2배 (44.1 → 88.2kHz)"),
        new(UpsampleMode.Quadruple, "4배 (44.1 → 176.4kHz)"),
        new(UpsampleMode.Max, "가장 높은 정수배 (예: 44.1 → 352.8kHz)"),
    ];

    public IReadOnlyList<Choice<ResampleFilter>> Filters { get; } =
    [
        new(ResampleFilter.Sharp, "선형 위상 (정확)"),
        new(ResampleFilter.MinimumPhase, "최소 위상 (프리링잉 없음)"),
        new(ResampleFilter.Soft, "부드러운 필터 (링잉 적음)"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpsampleNote))]
    private UpsampleMode _upsampling;

    [ObservableProperty]
    private ResampleFilter _filter;

    [ObservableProperty]
    private bool _hardwareVolume;

    /// <summary>Upsampling has nothing to do when the sound goes out bit-perfect.</summary>
    public bool CanUpsample => !(Exclusive && BitPerfect);

    /// <summary>What upsampling does with the settings as they are now.</summary>
    public string UpsampleNote =>
        Exclusive && BitPerfect ? "비트퍼펙트가 켜져 있어 업샘플링하지 않습니다"
        : Exclusive && SampleRate > 0 ? $"독점 모드의 샘플 속도를 {SampleRate / 1000.0:0.#}kHz로 정해 두어 그 속도로 보냅니다"
        : Upsampling == UpsampleMode.Off ? "곡의 샘플 속도 그대로 보냅니다"
        : Exclusive ? "곡 속도의 정수배 중 장치가 받는 속도로 보냅니다"
        : _sharedRate > 0 ? $"공유 모드: Windows 형식({_sharedRate / 1000.0:0.#}kHz)으로 GoTune이 직접 변환합니다 · 배수는 독점 모드에서 적용됩니다"
        : "공유 모드: Windows 형식으로 GoTune이 직접 변환합니다";

    private int _sharedRate;

    /// <summary>The file's sound as it is (in exclusive mode): no processing, the song's own rate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseFormat), nameof(CanUpsample), nameof(UpsampleNote))]
    private bool _bitPerfect;

    /// <summary>Rate and bits are GoTune's to choose only when the sound isn't sent bit-perfect.</summary>
    public bool CanChooseFormat => !BitPerfect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpsampleNote))]
    private int _sampleRate;

    [ObservableProperty]
    private int _bits;

    /// <summary>"Windows 공유 형식: 24비트 · 192kHz · 2채널" - the device's own format in shared mode.</summary>
    [ObservableProperty]
    private string _windowsFormat = "";

    /// <summary>Asking the device what it takes (a moment).</summary>
    [ObservableProperty]
    private bool _isProbing;

    /// <summary>How the sound is going out right now.</summary>
    public string Now => _playback.OutputDescription is { } now ? now : "재생 중인 곡이 없습니다";

    /// <summary>The device takes nothing in exclusive mode that we could ask about.</summary>
    public bool NoExclusiveFormats => !IsProbing && _formats.Count == 0;

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.OutputDescription) or nameof(PlaybackViewModel.CurrentFile))
            OnPropertyChanged(nameof(Now));
    }

    private OutputDeviceWatcher? _watcher;
    private SynchronizationContext? _ui;
    private int _changeSerial;

    /// <summary>From the first time the page opens, follow Windows: a device plugged in or out, a
    /// new default, or the shared format changed in Windows' sound settings shows here at once.</summary>
    private void Watch()
    {
        if (_watcher is not null) return;
        _ui = SynchronizationContext.Current;
        try
        {
            _watcher = new OutputDeviceWatcher();
            _watcher.Changed += () => _ui?.Post(_ => RefreshSoon(), null);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // audio service unavailable: the page refreshes when opened
        }
    }

    /// <summary>One change comes as a burst of notices: refresh once they settle.</summary>
    private async void RefreshSoon()
    {
        var serial = ++_changeSerial;
        await Task.Delay(400);
        if (serial == _changeSerial) Refresh();
    }

    /// <summary>The page was opened (or Windows changed a device): list the devices again.</summary>
    public async void Refresh()
    {
        Watch();
        var devices = await Task.Run(OutputDevices.List);
        _loading = true;
        var chosen = _settings.Output.DeviceId;
        Devices.Clear();
        var fallback = devices.FirstOrDefault(d => d.IsDefault);
        Devices.Add(new Choice<string>(DefaultKey, fallback is null ? "Windows 기본 장치" : $"Windows 기본 장치 ({fallback.Name})"));
        foreach (var device in devices) Devices.Add(new Choice<string>(device.Id, device.Name));
        // a chosen device that is unplugged stays listed, so the choice isn't lost
        if (chosen is not null && devices.All(d => d.Id != chosen))
            Devices.Add(new Choice<string>(chosen, "고른 장치 (연결되어 있지 않음)"));
        // (clearing the list made the combo box push an empty choice back here: the settings say what it is)
        DeviceId = chosen ?? DefaultKey;
        _loading = false;
        await ProbeAsync(devices);
    }

    private async Task ProbeAsync(IReadOnlyList<OutputDevice>? devices = null)
    {
        IsProbing = true;
        OnPropertyChanged(nameof(NoExclusiveFormats));
        var id = _settings.Output.DeviceId;
        devices ??= await Task.Run(OutputDevices.List);
        var device = id is null ? devices.FirstOrDefault(d => d.IsDefault) : devices.FirstOrDefault(d => d.Id == id);
        _deviceChannels = device?.Channels ?? 2;
        _sharedRate = device?.SampleRate ?? 0;
        OnPropertyChanged(nameof(UpsampleNote));
        WindowsFormat = device is null ? "" : $"Windows 공유 형식: {device.Bits}비트 · {device.SampleRate / 1000.0:0.#}kHz · {device.Channels}채널";
        var formats = await Task.Run(() => OutputDevices.ExclusiveFormats(id));
        // While GoTune itself holds the device in exclusive mode it can't be asked again: keep
        // what it said before.
        var key = device?.Id ?? "";
        if (formats.Count > 0) _formatCache[key] = formats;
        else if (_formatCache.TryGetValue(key, out var cached)) formats = cached;
        _formats = formats;
        IsProbing = false;
        FillChoices();
    }

    /// <summary>The layouts the device has speakers for, and the rates and bit depths it takes.</summary>
    private void FillChoices()
    {
        _loading = true;
        var output = _settings.Output;
        Layouts.Clear();
        Layouts.Add(new(SpeakerLayout.Stereo, "스테레오 (원음 그대로)"));
        if (_deviceChannels >= 4) Layouts.Add(new(SpeakerLayout.Quad, "4채널 (앞·뒤)"));
        if (_deviceChannels >= 6) Layouts.Add(new(SpeakerLayout.Surround51, "5.1채널 서라운드"));
        if (_deviceChannels >= 8) Layouts.Add(new(SpeakerLayout.Surround71, "7.1채널 서라운드"));
        Layout = Layouts.Any(l => l.Value == output.Layout) ? output.Layout : SpeakerLayout.Stereo;

        SampleRates.Clear();
        SampleRates.Add(new(0, "자동 (곡 속도 · 업샘플링 설정대로)"));
        foreach (var rate in _formats.Select(f => f.SampleRate).Distinct().Order())
            SampleRates.Add(new(rate, $"{rate / 1000.0:0.#}kHz"));
        SampleRate = SampleRates.Any(r => r.Value == output.ExclusiveSampleRate) ? output.ExclusiveSampleRate : 0;
        Exclusive = output.Exclusive;
        FillBits();
        _loading = false;
        OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(SampleRate));
        OnPropertyChanged(nameof(Bits));
        OnPropertyChanged(nameof(NoExclusiveFormats));
    }

    private void FillBits()
    {
        var wasLoading = _loading;
        _loading = true;
        BitDepths.Clear();
        BitDepths.Add(new(0, "가장 높게"));
        var bits = _formats.Where(f => SampleRate == 0 || f.SampleRate == SampleRate).Select(f => f.Bits).Distinct().Order();
        foreach (var b in bits) BitDepths.Add(new(b, $"{b}비트"));
        var wanted = _settings.Output.ExclusiveBits;
        Bits = BitDepths.Any(b => b.Value == wanted) ? wanted : 0;
        _loading = wasLoading;
        OnPropertyChanged(nameof(Bits));
    }

    partial void OnDeviceIdChanged(string value)
    {
        if (_loading || value is null) return;
        _settings.Output.DeviceId = value == DefaultKey ? null : value;
        Apply();
        _ = ProbeAsync();
    }

    partial void OnLayoutChanged(SpeakerLayout value)
    {
        if (_loading) return;
        _settings.Output.Layout = value;
        Apply();
    }

    partial void OnExclusiveChanged(bool value)
    {
        if (_loading) return;
        _settings.Output.Exclusive = value;
        Apply();
    }

    partial void OnBitPerfectChanged(bool value)
    {
        if (_loading) return;
        _settings.Output.BitPerfect = value;
        Apply();
    }

    partial void OnSampleRateChanged(int value)
    {
        if (_loading) return;
        _settings.Output.ExclusiveSampleRate = value;
        FillBits();
        _settings.Output.ExclusiveBits = Bits;
        Apply();
    }

    partial void OnBitsChanged(int value)
    {
        if (_loading) return;
        _settings.Output.ExclusiveBits = value;
        Apply();
    }

    partial void OnUpsamplingChanged(UpsampleMode value)
    {
        if (_loading) return;
        _settings.Output.Upsampling = value;
        Apply();
    }

    partial void OnFilterChanged(ResampleFilter value)
    {
        if (_loading) return;
        _settings.Output.Filter = value;
        Apply();
    }

    partial void OnHardwareVolumeChanged(bool value)
    {
        if (_loading) return;
        _settings.Output.HardwareVolume = value;
        Apply();
    }

    private void Apply()
    {
        AppSettingsStore.Save(_settings);
        _playback.ApplyOutput();
    }

    [RelayCommand]
    private static void OpenWindowsSound()
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); }
        catch { /* Windows' sound settings unavailable */ }
    }
}
