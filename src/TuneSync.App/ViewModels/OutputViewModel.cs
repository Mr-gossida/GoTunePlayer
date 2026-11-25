using System.Collections.ObjectModel;
using System.ComponentModel;
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
        _dsdOutput = settings.Output.DsdOutput;
        _remaster = settings.Output.Remaster;
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
    [NotifyPropertyChangedFor(nameof(CanUpsample), nameof(UpsampleNote), nameof(CanRemaster), nameof(RemasterNote),
        nameof(RemasterActive), nameof(CanChooseFormat), nameof(CanChooseFilter), nameof(FilterNote))]
    private bool _exclusive;

    public IReadOnlyList<Choice<UpsampleMode>> UpsampleModes { get; } =
    [
        new(UpsampleMode.Off, "끔"),
        new(UpsampleMode.Double, "2배"),
        new(UpsampleMode.Quadruple, "4배"),
        new(UpsampleMode.Max, "최대"),
    ];

    public IReadOnlyList<Choice<ResampleFilter>> Filters { get; } =
    [
        new(ResampleFilter.Sharp, "선형 위상 (정확)"),
        new(ResampleFilter.MinimumPhase, "최소 위상 (프리링잉 없음)"),
        new(ResampleFilter.Soft, "부드러운 필터 (링잉 적음)"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpsampleNote), nameof(UpsampleExample), nameof(CanChooseFilter), nameof(FilterNote))]
    private UpsampleMode _upsampling;

    /// <summary>What the chosen upsampling makes of the usual rates, under the choices.</summary>
    public string UpsampleExample => Upsampling switch
    {
        UpsampleMode.Double => "44.1 → 88.2kHz · 48 → 96kHz",
        UpsampleMode.Quadruple => "44.1 → 176.4kHz · 48 → 192kHz",
        UpsampleMode.Max => "장치가 받는 최고 정수배 (예: 44.1 → 352.8kHz)",
        _ => "곡의 샘플 속도 그대로",
    };

    [ObservableProperty]
    private ResampleFilter _filter;

    [ObservableProperty]
    private bool _hardwareVolume;

    public IReadOnlyList<Choice<DsdOutput>> DsdOutputs { get; } =
    [
        new(DsdOutput.Pcm, "PCM 변환"),
        new(DsdOutput.DoP, "DoP 원음"),
    ];

    public IReadOnlyList<Choice<DsdRemaster>> Remasters { get; } =
    [
        new(DsdRemaster.Off, "끔"),
        new(DsdRemaster.Dsd64, "DSD64"),
        new(DsdRemaster.Dsd128, "DSD128"),
    ];

    [ObservableProperty]
    private DsdOutput _dsdOutput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemasterActive), nameof(CanChooseFormat), nameof(CanUpsample), nameof(UpsampleNote))]
    private DsdRemaster _remaster;

    private bool _dsdConfirmed;

    /// <summary>DSD remastering is on and will be used (it picks its own rate, ignoring the rate, bits and upsampling).</summary>
    public bool RemasterActive => Exclusive && !BitPerfect && Remaster != DsdRemaster.Off;

    /// <summary>Upsampling has nothing to do when the sound goes out bit-perfect, at a rate fixed by hand, or as DSD.</summary>
    public bool CanUpsample => !(Exclusive && (BitPerfect || SampleRate > 0 || RemasterActive));

    /// <summary>The conversion filter only works where GoTune converts the rate: never bit-perfect,
    /// and in shared mode only while upsampling is on (else Windows alone converts).</summary>
    public bool CanChooseFilter => Exclusive ? !BitPerfect : Upsampling != UpsampleMode.Off;

    public string FilterNote =>
        Exclusive && BitPerfect ? "비트퍼펙트가 켜져 있어 샘플 속도를 바꾸지 않으므로 쓰지 않습니다"
        : !Exclusive && Upsampling == UpsampleMode.Off ? "공유 모드에서 업샘플링이 꺼져 있으면 쓰지 않습니다"
        : "GoTune이 샘플 속도를 바꿀 때마다 쓰는 필터 · 대역 밖 잡음은 약 140dB 줄입니다";

    public string RemasterNote =>
        !Exclusive ? "독점 모드를 켜야 쓸 수 있습니다"
        : BitPerfect ? "비트퍼펙트가 켜져 있어 쓸 수 없습니다"
        : "독점 모드 전용 · DAC가 DoP를 지원해야 합니다";

    /// <summary>What upsampling does with the settings as they are now.</summary>
    public string UpsampleNote =>
        Exclusive && BitPerfect ? "비트퍼펙트가 켜져 있어 업샘플링하지 않습니다"
        : RemasterActive ? "DSD 리마스터링이 켜져 있어 DSD 속도로 보냅니다"
        : Exclusive && SampleRate > 0 ? $"독점 모드의 샘플 속도를 {SampleRate / 1000.0:0.#}kHz로 정해 두어 그 속도로 보냅니다"
        : Upsampling == UpsampleMode.Off ? "곡의 샘플 속도 그대로 보냅니다"
        : Exclusive ? "곡 속도의 정수배 중 장치가 받는 속도로 보냅니다"
        : _sharedRate > 0 ? $"공유 모드: Windows 형식({_sharedRate / 1000.0:0.#}kHz)으로 GoTune이 직접 변환합니다 · 배수는 독점 모드에서 적용됩니다"
        : "공유 모드: Windows 형식으로 GoTune이 직접 변환합니다";

    private int _sharedRate;

    /// <summary>The file's sound as it is (in exclusive mode): no processing, the song's own rate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseFormat), nameof(CanUpsample), nameof(UpsampleNote), nameof(CanRemaster), nameof(RemasterNote),
        nameof(RemasterActive), nameof(CanChooseFilter), nameof(FilterNote))]
    private bool _bitPerfect;

    /// <summary>Rate and bits are GoTune's to choose only when the sound isn't sent bit-perfect
    /// and isn't remastered to DSD (which sets its own).</summary>
    public bool CanChooseFormat => !BitPerfect && !RemasterActive;

    /// <summary>DoP (DSD playback as-is, DSD remastering) goes straight to the device, so it exists only in exclusive mode;
    /// remastering also needs the sound processed, which bit-perfect isn't.</summary>
    public bool CanRemaster => Exclusive && !BitPerfect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpsampleNote), nameof(CanUpsample))]
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

    /// <summary>The same as badges ([독점] [32bit · 352.8kHz] [업샘플링 ×8] ...), as on the play screen.</summary>
    public IReadOnlyList<OutputBadge> NowBadges => _playback.OutputBadges;

    public bool HasNowBadges => NowBadges.Count > 0;

    /// <summary>Nothing plays through GoTune's own player right now.</summary>
    public bool IsIdle => !HasNowBadges;

    /// <summary>Why the sound isn't quite as set, in words (the caution badge's reason), or "".</summary>
    public string NowNote => NowBadges.FirstOrDefault(b => b.IsCaution)?.ToolTip.Replace("설정과 다르게 재생 중: ", "") ?? "";

    public bool HasNowNote => NowNote.Length > 0;

    /// <summary>The device takes nothing in exclusive mode that we could ask about.</summary>
    public bool NoExclusiveFormats => !IsProbing && _formats.Count == 0;

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.OutputDescription) or nameof(PlaybackViewModel.CurrentFile) or nameof(PlaybackViewModel.OutputBadges))
        {
            OnPropertyChanged(nameof(Now));
            OnPropertyChanged(nameof(NowBadges));
            OnPropertyChanged(nameof(HasNowBadges));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(NowNote));
            OnPropertyChanged(nameof(HasNowNote));
        }
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
        SampleRates.Add(new(0, "자동"));
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
        if (value && !_hardwareVolumeConfirmed)
        {
            // asked first: the switch stays off until the warning has been read
            _loading = true;
            HardwareVolume = false;
            _loading = false;
            _ = ConfirmHardwareVolumeAsync();
            return;
        }
        _hardwareVolumeConfirmed = false;
        _settings.Output.HardwareVolume = value;
        Apply();
    }

    private bool _hardwareVolumeConfirmed;

    /// <summary>What a DAC that doesn't take DoP does with it - said before DoP is turned on.</summary>
    private const string DopWarning =
        "• DAC가 DoP를 지원하지 않으면 DSD가 아니라 '쉬익' 하는 큰 잡음으로 재생됩니다. 처음에는 이어폰·헤드폰을 귀에서 빼고, 볼륨을 낮춘 상태에서 확인하세요.\n" +
        "• DAC의 표시등이나 앱에서 DSD로 인식하는지 확인할 수 있습니다.\n";

    partial void OnDsdOutputChanged(DsdOutput value)
    {
        if (_loading) return;
        if (value == DsdOutput.DoP && !_dsdConfirmed)
        {
            _loading = true;
            DsdOutput = DsdOutput.Pcm;
            _loading = false;
            _ = ConfirmDsdAsync(() => DsdOutput = DsdOutput.DoP, "DoP로 DSD 보내기",
                "DSD 파일을 변환하지 않고 DoP로 DAC에 그대로 보냅니다.\n\n" +
                "• 독점 모드이고, '하드웨어 볼륨'이 켜져 있고, 장치에 하드웨어 볼륨이 있을 때만 DoP로 보냅니다. DoP는 소리 데이터를 줄일 수 없어 GoTune 볼륨이 장치 볼륨으로만 적용되기 때문입니다. 조건이 안 되면 PCM으로 변환해 재생합니다.\n" +
                DopWarning, "확인했고 켜기");
            return;
        }
        _dsdConfirmed = false;
        _settings.Output.DsdOutput = value;
        Apply();
    }

    partial void OnRemasterChanged(DsdRemaster value)
    {
        if (_loading) return;
        if (value != DsdRemaster.Off && _settings.Output.Remaster == DsdRemaster.Off && !_dsdConfirmed)
        {
            _loading = true;
            Remaster = DsdRemaster.Off;
            _loading = false;
            _ = ConfirmDsdAsync(() => Remaster = value, "DSD 리마스터링 켜기",
                "독점 모드에서 PCM 곡을 DSD로 바꿔 DoP로 DAC에 보냅니다. 볼륨과 이퀄라이저는 DSD로 바꾸기 전에 적용됩니다.\n\n" +
                DopWarning, "확인했고 켜기");
            return;
        }
        _dsdConfirmed = false;
        _settings.Output.Remaster = value;
        Apply();
    }

    private async Task ConfirmDsdAsync(Action turnOn, string title, string message, string confirm)
    {
        if (!await Dialogs.ConfirmAsync(title, message, confirm)) return;
        _dsdConfirmed = true;
        turnOn();
    }

    private async Task ConfirmHardwareVolumeAsync()
    {
        var ok = await Dialogs.ConfirmAsync("하드웨어 볼륨 켜기",
            "볼륨을 장치(DAC) 자체에서 조절합니다. 처음 확인할 때는 안전을 위해 다음을 지켜 주세요.\n\n" +
            "• 이어폰·헤드폰을 귀에서 빼고, GoTune 볼륨을 낮춘 상태에서 켜세요.\n" +
            "• GoTune 볼륨 100%는 켜기 전 Windows에 설정된 장치 볼륨과 같습니다. 그보다 커지지는 않습니다.\n" +
            "• 장치가 하드웨어 볼륨을 지원한다고 알려도 실제로는 줄지 않는 장치가 있습니다. 재생해 보고 볼륨을 움직였을 때 소리가 줄어드는지 먼저 확인하세요.\n" +
            "• 하드웨어 볼륨이 없는 장치에서는 GoTune 볼륨이 디지털로 적용됩니다.",
            "확인했고 켜기");
        if (!ok) return;
        _hardwareVolumeConfirmed = true;
        HardwareVolume = true;
    }

    private void Apply()
    {
        AppSettingsStore.Save(_settings);
        _playback.ApplyOutput();
    }

    [RelayCommand]
    private static void OpenWindowsSound() => SystemTools.OpenSoundSettings();
}
