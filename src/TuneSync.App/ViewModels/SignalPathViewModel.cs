using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Audio;
using TuneSync.Core.Models;
using Wpf.Ui.Controls;

namespace TuneSync.App.ViewModels;

public enum SignalStepState
{
    /// <summary>A step the sound passes through without being changed by it.</summary>
    Neutral,
    /// <summary>The sound is changed here (shown in the accent color, "변환").</summary>
    Changed,
    /// <summary>The sound is kept exactly as the file has it (bit-perfect / DoP: "원음 유지").</summary>
    Pure,
    /// <summary>A step the player isn't doing now: one dim line.</summary>
    Skipped,
}

/// <summary>One step of the 소리 경로: what it is, what it does to the sound now.</summary>
public sealed record SignalStep(SymbolRegular Icon, string Title, string Main, string? Sub, SignalStepState State,
    string? ActionLabel = null, IRelayCommand? Action = null)
{
    public bool IsChanged => State == SignalStepState.Changed;
    public bool IsPure => State == SignalStepState.Pure;
    public bool IsSkipped => State == SignalStepState.Skipped;
    public bool HasSub => !string.IsNullOrEmpty(Sub);
    public bool HasAction => Action is not null;
    public string? Tag => State switch { SignalStepState.Changed => "변환", SignalStepState.Pure => "원음 유지", _ => null };

    /// <summary>The last step draws no line down to a next one.</summary>
    public bool IsLast { get; init; }
}

/// <summary>The 소리 경로 panel (Poweramp's "audio info", in GoTune's way): every step the playing
/// song's sound takes from the file to the device, which of them change it, and a one-line
/// verdict on top - "원음 그대로", or how many places change the sound and which.</summary>
public sealed partial class SignalPathViewModel : ObservableObject
{
    private readonly PlaybackViewModel _playback;
    private readonly AppSettings _settings;

    public SignalPathViewModel(PlaybackViewModel playback, AppSettings settings)
    {
        _playback = playback;
        _settings = settings;
    }

    public ObservableCollection<SignalStep> Steps { get; } = [];

    [ObservableProperty] private string _verdictTitle = "";
    [ObservableProperty] private string _verdictText = "";
    [ObservableProperty] private string _verdictMark = "";
    [ObservableProperty] private bool _verdictPure;
    [ObservableProperty] private string _precision = "";
    [ObservableProperty] private string _underruns = "";
    [ObservableProperty] private bool _hasFooter;

    /// <summary>The note on top shows only when there's no chain of steps to read (nothing
    /// playing, the device opening, Windows' player): while playing, the steps' marks say it.</summary>
    [ObservableProperty] private bool _hasVerdict;

    /// <summary>Opens the equalizer (where the tone step's settings are).</summary>
    public Action? OpenEqualizer { get; set; }

    /// <summary>Opens 설정 › 음향 (the output's settings).</summary>
    public Action? OpenOutputSettings { get; set; }

    private static string Khz(int rate) => (rate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "kHz";

    private static string Percent(double volume) => $"{Math.Round(volume * 100)}%";

    /// <summary>Reads the player again (on opening, and while open as things change).</summary>
    public void Refresh() => Show(_playback.SignalPath, _playback.CurrentQuality, _playback.CurrentFile?.Path, _playback.IsToneBypassed);

    /// <summary>Lays out the panel for this state of the player (<paramref name="path"/>: the
    /// playing file, null when nothing plays).</summary>
    internal void Show(SignalPath? signal, AudioQuality? quality, string? path, bool bypassed)
    {
        HasVerdict = path is null || signal is null;
        var steps = new List<SignalStep>();
        if (path is null)
        {
            SetVerdict("재생 중인 곡 없음", "곡을 재생하면 소리가 어떤 과정을 거쳐 나오는지 보여 드립니다.", "·", pure: false);
            Fill(steps);
            HasFooter = false;
            return;
        }
        if (signal is null)
        {
            if (bypassed)
            {
                SetVerdict("Windows 플레이어로 재생 중", "GoTune이 직접 열 수 없는 형식이라 Windows 기본 플레이어로 재생합니다. 이퀄라이저와 출력 설정은 적용되지 않습니다.", "!", pure: false);
                steps.Add(FileStep(path, quality, null, pure: false));
                steps.Add(new(SymbolRegular.Headphones24, "출력", "Windows 기본 장치", "Windows 기본 플레이어가 직접 출력", SignalStepState.Neutral));
            }
            else
            {
                SetVerdict("장치를 여는 중…", "재생이 시작되면 경로를 보여 드립니다.", "·", pure: false);
            }
            Fill(steps);
            HasFooter = false;
            return;
        }

        var output = signal.Output;
        var pure = output.Path is OutputPath.BitPerfect or OutputPath.DoP;
        var changed = new List<string>();
        var eq = new RelayCommand(() => OpenEqualizer?.Invoke());
        var settings = new RelayCommand(() => OpenOutputSettings?.Invoke());

        // 1. the file
        steps.Add(FileStep(path, quality, signal, pure));

        // 2. decoding
        if (signal.DsdRate > 0 && output.Path == OutputPath.DoP)
            steps.Add(new(SymbolRegular.DeveloperBoard24, "디코더", signal.Decoder, "DSD 데이터를 변환 없이 그대로 읽음", SignalStepState.Pure));
        else if (signal.DsdRate > 0)
        {
            changed.Add("DSD → PCM 변환");
            steps.Add(new(SymbolRegular.DeveloperBoard24, "디코더", signal.Decoder,
                $"{signal.DsdName}를 PCM {Khz(signal.SourceRate)}로 변환 (32bit 부동소수점)", SignalStepState.Changed));
        }
        else
        {
            var sub = pure
                ? signal.SourceBits > 0 ? $"원본 {signal.SourceBits}bit 그대로 디코딩" : "디코딩한 신호 그대로"
                : signal.SourceBits is > 0 and <= 24
                    ? $"32bit 부동소수점으로 디코딩 · 원본 {signal.SourceBits}bit 정보는 손실 없음"
                    : "32bit 부동소수점으로 디코딩";
            steps.Add(new(SymbolRegular.DeveloperBoard24, "디코더", signal.Decoder, sub, pure ? SignalStepState.Pure : SignalStepState.Neutral));
        }

        // 3. tone (the equalizer and the rest of the chain)
        if (signal.Tone is { } tone)
        {
            changed.Add("이퀄라이저");
            steps.Add(new(SymbolRegular.DeviceEq24, "음색", ToneMain(), ToneSub(tone), SignalStepState.Changed, "이퀄라이저", eq));
        }
        else
        {
            var why = pure ? "음색 조절 안 함 (원음 출력 중)" : "음색 조절 꺼짐";
            steps.Add(new(SymbolRegular.DeviceEq24, why, "", null, SignalStepState.Skipped, "이퀄라이저", eq));
        }

        // 4. volume
        if (signal.HardwareVolume)
        {
            steps.Add(new(SymbolRegular.Speaker224, "볼륨", signal.Muted ? "장치 볼륨 · 음소거" : $"장치 볼륨 {Percent(signal.Volume)}",
                "장치에서 직접 조절 · 신호는 그대로", pure ? SignalStepState.Pure : SignalStepState.Neutral));
        }
        else if (signal.Muted || signal.Volume < 0.9995)
        {
            changed.Add(signal.Muted ? "음소거" : $"볼륨 {Percent(signal.Volume)}");
            steps.Add(new(SymbolRegular.Speaker224, "볼륨", signal.Muted ? "GoTune 볼륨 · 음소거" : $"GoTune 볼륨 {Percent(signal.Volume)}",
                "GoTune이 신호 크기를 줄여 조절", SignalStepState.Changed));
        }
        else
        {
            steps.Add(new(SymbolRegular.Speaker224, "볼륨", "GoTune 볼륨 100%", "신호 크기 변화 없음", pure ? SignalStepState.Pure : SignalStepState.Neutral));
        }

        // 5. sample-rate conversion
        if (output.Path == OutputPath.Remaster)
        {
            changed.Add("DSD 리마스터링");
            steps.Add(new(SymbolRegular.ArrowSwap24, "샘플 변환", $"PCM → {output.Dsd} 리마스터링", output.ConversionDetail, SignalStepState.Changed, "출력 설정", settings));
        }
        else if (signal.Resampling)
        {
            var up = signal.OutputRate > signal.SourceRate;
            var whole = up && signal.OutputRate % signal.SourceRate == 0;
            changed.Add(up ? "업샘플링" : "샘플 속도 변환");
            steps.Add(new(SymbolRegular.ArrowSwap24, "샘플 변환",
                $"{(up ? "업샘플링" : "샘플 속도 변환")} {Khz(signal.SourceRate).Replace("kHz", "")} → {Khz(signal.OutputRate)}",
                $"{signal.ResampleFilter}{(up ? whole ? $" · 정수배 (×{signal.OutputRate / signal.SourceRate})" : " · 비정수배 변환" : "")}",
                SignalStepState.Changed, "출력 설정", settings));
        }
        else
        {
            steps.Add(new(SymbolRegular.ArrowSwap24, "샘플 속도 변환 없음", "", null, SignalStepState.Skipped));
        }

        // 6. output
        steps.Add(OutputStep(signal, changed, settings));

        // 7. the device
        steps.Add(DeviceStep(signal, pure));

        if (pure)
        {
            var dop = output.Path == OutputPath.DoP;
            SetVerdict(dop ? "원음 그대로 (DoP)" : "원음 그대로",
                dop ? "DSD를 PCM으로 바꾸지 않고 DAC로 그대로 보냅니다." : "파일의 신호를 한 비트도 바꾸지 않고 장치로 보냅니다.", "✓", pure: true);
        }
        else if (changed.Count == 0)
        {
            SetVerdict("소리를 바꾸는 단계 없음", "음색 조절, 볼륨, 샘플 변환 없이 장치가 받는 형식으로만 바꿔 보냅니다.", "✓", pure: true);
        }
        else
        {
            SetVerdict($"{changed.Count}곳에서 소리가 바뀝니다", string.Join(" · ", changed), changed.Count.ToString(CultureInfo.InvariantCulture), pure: false);
        }

        var processing = signal.Tone is not null || signal.Resampling || output.Path == OutputPath.Remaster || (!signal.HardwareVolume && (signal.Muted || signal.Volume < 0.9995));
        Precision = pure ? "처리 없음" : processing ? "64bit 부동소수점" : "처리 없음";
        Underruns = signal.Underruns == 0 ? "없음" : $"{signal.Underruns}회";
        HasFooter = true;
        Fill(steps);
    }

    private static SignalStep FileStep(string path, AudioQuality? quality, SignalPath? signal, bool pure)
    {
        var main = quality?.Summary ?? System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        string? sub = null;
        if (signal is { DsdRate: > 0 }) sub = "1비트 DSD";
        else if (quality is { } q)
        {
            sub = q.IsHiRes ? "무손실 · Hi-Res" : q.IsLossless ? "무손실" : "손실 압축 · 비트 깊이 정보가 없어 16bit로 표시";
            if (q.Channels == 2) sub += " · 스테레오";
        }
        return new(SymbolRegular.MusicNote224, "파일", main, sub, pure ? SignalStepState.Pure : SignalStepState.Neutral);
    }

    private SignalStep OutputStep(SignalPath signal, List<string> changed, IRelayCommand settings)
    {
        var output = signal.Output;
        var layout = output.Layout != "스테레오" ? $" · {output.Layout}로 분배" : "";
        string main, sub;
        var state = SignalStepState.Neutral;
        switch (output.Path)
        {
            case OutputPath.DoP:
                main = $"DoP · {output.Dsd}";
                sub = $"독점 모드 · DSD를 {signal.WriteBits}bit {Khz(signal.OutputRate)} PCM에 담아 전송";
                state = SignalStepState.Pure;
                break;
            case OutputPath.BitPerfect:
                main = $"비트퍼펙트 · 독점 모드 · {output.Bits}bit · {Khz(output.Rate)}";
                sub = "Windows 믹서 거치지 않음";
                state = SignalStepState.Pure;
                break;
            case OutputPath.Remaster:
                main = $"DoP · {output.Dsd} (리마스터링)";
                sub = $"독점 모드 · {signal.WriteBits}bit {Khz(signal.OutputRate)}";
                break;
            case OutputPath.Exclusive:
                main = $"독점 모드 · {output.Bits}bit · {Khz(output.Rate)}";
                sub = (signal.Dither is { } dither ? $"{output.Bits}bit로 바꿀 때 {dither} 적용 · " : signal.WritesFloat ? "32bit 부동소수점 그대로 · " : "")
                      + "Windows 믹서 거치지 않음";
                break;
            default:
                changed.Add("Windows 믹서");
                main = $"공유 모드 · {Khz(output.Rate)}";
                sub = "32bit 부동소수점으로 Windows 믹서에 전달 · 다른 앱 소리와 함께 출력";
                state = SignalStepState.Changed;
                break;
        }
        sub += layout;
        if (output.Note is { } note) sub += $"\n설정과 다름: {note}";
        return new(SymbolRegular.PlugConnected24, "출력", main, sub, state, "출력 설정", settings);
    }

    private static SignalStep DeviceStep(SignalPath signal, bool pure)
    {
        var output = signal.Output;
        var name = string.IsNullOrWhiteSpace(signal.DeviceName) ? "기본 재생 장치" : signal.DeviceName!;
        var sub = output.Path switch
        {
            OutputPath.Shared => output.DeviceBits > 0
                ? $"Windows 기본 형식: {output.DeviceBits}bit · {Khz(output.Rate)}"
                : $"Windows 소리 설정의 기본 형식으로 출력 · {Khz(output.Rate)}",
            OutputPath.DoP or OutputPath.Remaster => $"DAC에서 {output.Dsd} ({output.Rate / 1e6:0.0}MHz)로 재생",
            _ => $"{output.Bits}bit · {Khz(output.Rate)}로 재생",
        };
        return new(SymbolRegular.Headphones24, "장치", name, sub, pure ? SignalStepState.Pure : SignalStepState.Neutral);
    }

    /// <summary>"이퀄라이저 10밴드 · 팝" (or "직접 조정" once the bands were moved by hand).</summary>
    private string ToneMain()
    {
        var tone = _settings.Tone;
        var preset = tone.Preset is null ? "직접 조정" : EqPresets.Find(tone.Preset)?.Name ?? tone.Preset;
        return $"이퀄라이저 {tone.BandCount}밴드 · {preset}";
    }

    /// <summary>The rest of the chain, as far as it does anything.</summary>
    private string ToneSub(ToneConfig config)
    {
        var tone = _settings.Tone;
        var parts = new List<string>();
        var gain = config.GainDb;
        parts.Add(config.HeadroomDb > 0.05
            ? $"프리앰프 {Signed(gain)}dB (자동 헤드룸 {Signed(-config.HeadroomDb)}dB 포함)"
            : $"프리앰프 {Signed(gain)}dB");
        if (Math.Abs(tone.BassDb) >= 0.05) parts.Add($"저음 {Signed(tone.BassDb)}dB");
        if (Math.Abs(tone.TrebleDb) >= 0.05) parts.Add($"고음 {Signed(tone.TrebleDb)}dB");
        var line1 = string.Join(" · ", parts);
        parts.Clear();
        if (Math.Abs(config.Width - 1) > 0.005) parts.Add($"스테레오 너비 {Math.Round(config.Width * 100)}%");
        if (Math.Abs(config.Balance) > 0.005)
            parts.Add($"밸런스 {(config.Balance < 0 ? "왼쪽" : "오른쪽")} {Math.Round(Math.Abs(config.Balance) * 100)}%");
        if (config.ReverbMix > 0) parts.Add($"리버브 {config.ReverbRoom.Name} {Math.Round(config.ReverbMix * 100)}%");
        parts.Add(config.Limiter ? "리미터 켜짐" : "리미터 꺼짐");
        return line1 + "\n" + string.Join(" · ", parts);
    }

    private static string Signed(double db) => (db >= 0 ? "+" : "−") + Math.Abs(db).ToString("0.0", CultureInfo.InvariantCulture);

    private void SetVerdict(string title, string text, string mark, bool pure)
    {
        VerdictTitle = title;
        VerdictText = text;
        VerdictMark = mark;
        VerdictPure = pure;
    }

    /// <summary>Puts the steps in, changing only those that differ (no flicker while open).</summary>
    private void Fill(List<SignalStep> steps)
    {
        if (steps.Count > 0) steps[^1] = steps[^1] with { IsLast = true };
        for (var i = 0; i < steps.Count; i++)
        {
            if (i < Steps.Count)
            {
                if (!SameStep(Steps[i], steps[i])) Steps[i] = steps[i];
            }
            else Steps.Add(steps[i]);
        }
        while (Steps.Count > steps.Count) Steps.RemoveAt(Steps.Count - 1);
    }

    private static bool SameStep(SignalStep a, SignalStep b) =>
        a.Icon == b.Icon && a.Title == b.Title && a.Main == b.Main && a.Sub == b.Sub && a.State == b.State && a.IsLast == b.IsLast;
}
