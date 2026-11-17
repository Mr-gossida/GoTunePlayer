namespace TuneSync.Core.Models;

/// <summary>The equalizer and tone controls as the user set them (설정 › 음향 / the 이퀄라이저 panel).
/// All levels are in decibels; 0 everywhere is the untouched sound.</summary>
public sealed class ToneSettings
{
    /// <summary>The two sets of bands to choose from: ten an octave apart, or 31 a third of an
    /// octave apart (see <see cref="Audio.ToneCurve"/>).</summary>
    public const int OctaveBands = 10, ThirdOctaveBands = 31;
    public const double MaxGainDb = 12;

    /// <summary>The whole chain's on/off switch. Off, the music plays exactly as it is in the file.</summary>
    public bool Enabled { get; set; }

    /// <summary>Overall level before the filters.</summary>
    public double PreampDb { get; set; }

    /// <summary>How many bands the equalizer has: <see cref="OctaveBands"/> or
    /// <see cref="ThirdOctaveBands"/>. Settings from before there was a choice have ten.</summary>
    public int BandCount { get; set; } = OctaveBands;

    /// <summary>The bands' levels, low to high: as many as <see cref="BandCount"/>.</summary>
    public double[] BandsDb { get; set; } = new double[OctaveBands];

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
        BandCount = BandCount == ThirdOctaveBands ? ThirdOctaveBands : OctaveBands;
        BandsDb = FitBands(BandsDb, BandCount);
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

    /// <summary>Switches between the ten and the 31 bands, keeping the curve: the new sliders are
    /// set to what the old ones did at their frequencies (<see cref="Audio.ToneCurve.Resample"/>).</summary>
    public void ChangeBandCount(int bandCount)
    {
        bandCount = bandCount == ThirdOctaveBands ? ThirdOctaveBands : OctaveBands;
        if (bandCount == BandCount && BandsDb?.Length == bandCount) return;
        BandCount = bandCount;
        BandsDb = FitBands(BandsDb, bandCount);
    }

    /// <summary>Levels for <paramref name="bandCount"/> bands from whatever was saved: a whole
    /// set made for the other bands is carried over by its curve; anything else (a hand-edited
    /// file) is cut or padded with zeros. Always a new array, every level within range.</summary>
    internal static double[] FitBands(double[]? bands, int bandCount)
    {
        bands ??= [];
        if (bands.Length != bandCount && bands.Length is OctaveBands or ThirdOctaveBands)
            return Audio.ToneCurve.Resample([.. bands.Select(ClampGain)], bandCount);
        var fitted = new double[bandCount];
        for (var i = 0; i < bandCount && i < bands.Length; i++) fitted[i] = ClampGain(bands[i]);
        return fitted;
    }

    internal static double ClampGain(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -MaxGainDb, MaxGainDb) : 0;
}

/// <summary>A named set of band levels: ten or 31, as many as the equalizer had when it was
/// saved. It applies to either: <see cref="Audio.EqPresets.Apply"/> carries the curve over.</summary>
public sealed class EqPreset
{
    public string Name { get; set; } = "";
    public double PreampDb { get; set; }
    public double[] BandsDb { get; set; } = new double[ToneSettings.OctaveBands];

    public void Normalize()
    {
        BandsDb = ToneSettings.FitBands(BandsDb, Audio.ToneCurve.BandCountOf(BandsDb?.Length ?? 0));
        PreampDb = ToneSettings.ClampGain(PreampDb);
    }
}
