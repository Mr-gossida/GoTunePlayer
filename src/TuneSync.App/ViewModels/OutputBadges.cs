using TuneSync.Core.Audio;

namespace TuneSync.App.ViewModels;

public enum OutputBadgeKind
{
    /// <summary>The way the sound goes out (filled in the accent color).</summary>
    Path,
    /// <summary>A plain fact about it (outlined).</summary>
    Detail,
    /// <summary>It isn't quite as set (caution color).</summary>
    Caution,
}

/// <summary>One badge of the output line: "Exclusive", "32bit · 352.8kHz", "Upsampling ×8"...
/// Short English labels (like the "Hi-Res" / "Lossless" badges); the tooltip says it in Korean.</summary>
public sealed record OutputBadge(string Text, OutputBadgeKind Kind, string ToolTip)
{
    public bool IsPath => Kind == OutputBadgeKind.Path;
    public bool IsCaution => Kind == OutputBadgeKind.Caution;

    /// <summary>The badges for how the sound is going out now (empty while nothing plays
    /// through GoTune's own player).</summary>
    public static IReadOnlyList<OutputBadge> For(OutputInfo? info, string? description)
    {
        if (info is null) return [];
        var all = description ?? "";
        var badges = new List<OutputBadge>();
        var (path, pathTip) = info.Path switch
        {
            OutputPath.BitPerfect => ("Bit-Perfect", "비트퍼펙트: 파일의 소리를 한 비트도 바꾸지 않고 장치로 보내는 중"),
            OutputPath.DoP => ($"DoP · {info.Dsd}", "DoP: DSD를 변환하지 않고 DAC에 보내는 중"),
            OutputPath.Remaster => ($"{info.Dsd} Remaster", "DSD 리마스터링: PCM을 DSD로 바꿔 DoP로 DAC에 보내는 중"),
            OutputPath.Exclusive => ("Exclusive", "독점 모드: Windows 믹서를 거치지 않고 장치로 바로 보내는 중"),
            _ => ("Shared", "공유 모드: Windows 믹서를 거쳐 다른 프로그램 소리와 함께 나가는 중"),
        };
        badges.Add(new(path, OutputBadgeKind.Path, $"{pathTip}\n{all}"));

        if (info.Path is OutputPath.DoP or OutputPath.Remaster)
            badges.Add(new($"{info.Rate / 1e6:0.0}MHz", OutputBadgeKind.Detail, $"DSD 샘플 속도 {info.Rate / 1e6:0.0###}MHz (1비트)"));
        else if (info.Bits > 0)
            badges.Add(new($"{info.Bits}bit · {Khz(info.Rate)}", OutputBadgeKind.Detail, $"장치로 가는 형식: {info.Bits}비트 {Khz(info.Rate)}"));
        else if (info.DeviceBits > 0)
            badges.Add(new($"{info.DeviceBits}bit · {Khz(info.Rate)}", OutputBadgeKind.Detail,
                $"Windows 믹서로 {Khz(info.Rate)}로 보내는 중 · 장치는 Windows 소리 설정의 기본 형식({info.DeviceBits}비트)으로 재생"));
        else
            badges.Add(new(Khz(info.Rate), OutputBadgeKind.Detail, $"Windows 믹서로 {Khz(info.Rate)}로 보내는 중 (장치 형식은 Windows 소리 설정을 따름)"));

        if (info.Conversion is { } conversion)
            badges.Add(new(English(conversion), OutputBadgeKind.Detail, info.ConversionDetail ?? conversion));
        else if (info.Path == OutputPath.Remaster && info.ConversionDetail is { } remaster)
            badges.Add(new("Sigma-Delta", OutputBadgeKind.Detail, $"5차 시그마-델타 변조 (64비트 계산) · {remaster}"));
        else if (info.Path == OutputPath.DoP)
            badges.Add(new("Native", OutputBadgeKind.Detail, "DSD 그대로, 변환 없음"));

        if (info.Equalizer) badges.Add(new("EQ", OutputBadgeKind.Detail, "이퀄라이저·음색 조절이 적용되는 중"));
        if (info.HardwareVolume) badges.Add(new("HW Volume", OutputBadgeKind.Detail, "하드웨어 볼륨: 볼륨을 장치(DAC) 자체에서 조절하는 중"));
        if (info.Layout != "스테레오")
            badges.Add(new(info.Layout switch { "4채널" => "Quad", "5.1채널" => "5.1ch", "7.1채널" => "7.1ch", var other => other },
                OutputBadgeKind.Detail, $"{info.Layout} 스피커로 나눠 보내는 중"));
        if (info.Note is { } note) badges.Add(new("Adjusted", OutputBadgeKind.Caution, $"설정과 다르게 재생 중: {note}"));
        return badges;
    }

    private static string Khz(int rate) => $"{rate / 1000.0:0.#}kHz";

    /// <summary>The player's conversion name in the badges' English ("업샘플링 ×8" → "Upsampling ×8").</summary>
    private static string English(string conversion) => conversion
        .Replace("업샘플링", "Upsample")
        .Replace("샘플 속도 변환", "Resampling");
}
