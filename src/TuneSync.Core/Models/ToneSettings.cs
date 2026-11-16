namespace TuneSync.Core.Models;

/// <summary>The equalizer and tone controls as the user set them (설정 › 음향 / the 이퀄라이저 panel).
/// All levels are in decibels; 0 everywhere is the untouched sound.</summary>
public sealed class ToneSettings
{
    public const int BandCount = 10;
    public const double MaxGainDb = 12;

    /// <summary>The whole chain's on/off switch. Off, the music plays exactly as it is in the file.</summary>
    public bool Enabled { get; set; }

    /// <summary>Overall level before the filters.</summary>
    public double PreampDb { get; set; }

    /// <summary>The ten bands, 31 Hz to 16 kHz.</summary>
    public double[] BandsDb { get; set; } = new double[BandCount];

    public double BassDb { get; set; }
    public double TrebleDb { get; set; }

    /// <summary>Stereo width: 0 mono, 1 as recorded, 2 widest.</summary>
    public double Width { get; set; } = 1;

    /// <summary>-1 all left, 0 centered, +1 all right.</summary>
    public double Balance { get; set; }

    /// <summary>Lowers the overall level by as much as the curve boosts its loudest frequency,
    /// so a boost makes room for itself instead of pushing peaks into the limiter.</summary>
    public bool AutoHeadroom { get; set; } = true;

    /// <summary>The preset the bands were last set from: a built-in's id, a saved preset's name,
    /// or null once the bands were moved by hand.</summary>
    public string? Preset { get; set; } = "flat";

    public List<EqPreset> UserPresets { get; set; } = [];

    /// <summary>The values in the ranges the controls allow, whatever a hand-edited settings file says.</summary>
    public void Normalize()
    {
        var bands = new double[BandCount];
        for (var i = 0; i < BandCount && i < (BandsDb?.Length ?? 0); i++) bands[i] = ClampGain(BandsDb![i]);
        BandsDb = bands;
        PreampDb = ClampGain(PreampDb);
        BassDb = ClampGain(BassDb);
        TrebleDb = ClampGain(TrebleDb);
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 0, 2) : 1;
        Balance = double.IsFinite(Balance) ? Math.Clamp(Balance, -1, 1) : 0;
        UserPresets ??= [];
        UserPresets.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name));
        foreach (var preset in UserPresets) preset.Normalize();
    }

    internal static double ClampGain(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -MaxGainDb, MaxGainDb) : 0;
}

/// <summary>A named set of band levels.</summary>
public sealed class EqPreset
{
    public string Name { get; set; } = "";
    public double PreampDb { get; set; }
    public double[] BandsDb { get; set; } = new double[ToneSettings.BandCount];

    public void Normalize()
    {
        var bands = new double[ToneSettings.BandCount];
        for (var i = 0; i < bands.Length && i < (BandsDb?.Length ?? 0); i++) bands[i] = ToneSettings.ClampGain(BandsDb![i]);
        BandsDb = bands;
        PreampDb = ToneSettings.ClampGain(PreampDb);
    }
}
