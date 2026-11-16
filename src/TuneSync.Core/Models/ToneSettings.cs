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
    /// so a boost makes room for itself instead of pushing peaks into the limiter. Off unless
    /// asked for: it makes every boost sound quieter and duller than the slider promises.
    /// (Saved under a new name: it used to be on by default, and a settings file written then
    /// holds that default, not a choice.)</summary>
    [System.Text.Json.Serialization.JsonPropertyName("Headroom")]
    public bool AutoHeadroom { get; set; }

    /// <summary>Holds the sound just under full scale when a boost would push it past (see
    /// <see cref="Audio.PeakLimiter"/>). Off, such peaks go out as they are and Windows clips them.</summary>
    public bool Limiter { get; set; } = true;

    /// <summary>A room around the music (see <see cref="Audio.Reverb"/>). Off unless asked for.</summary>
    public bool Reverb { get; set; }

    /// <summary>Which room: an id from <see cref="Audio.ReverbRooms"/>.</summary>
    public string ReverbRoom { get; set; } = Audio.ReverbRooms.DefaultId;

    /// <summary>How much of the room is added, 0..1. A little goes a long way: the default is
    /// a touch of air, not an effect.</summary>
    public double ReverbAmount { get; set; } = DefaultReverbAmount;

    /// <summary>The room's decay time, scaled (0.5..2; 1 = as the room is).</summary>
    public double ReverbDecay { get; set; } = 1;

    public const double DefaultReverbAmount = 0.2;

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
        ReverbRoom = Audio.ReverbRooms.Find(ReverbRoom).Id;
        ReverbAmount = double.IsFinite(ReverbAmount) ? Math.Clamp(ReverbAmount, 0, 1) : DefaultReverbAmount;
        ReverbDecay = double.IsFinite(ReverbDecay) ? Math.Clamp(ReverbDecay, Audio.ReverbRooms.MinDecayScale, Audio.ReverbRooms.MaxDecayScale) : 1;
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
