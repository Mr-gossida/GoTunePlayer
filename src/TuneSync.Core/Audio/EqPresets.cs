using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>A preset that comes with the app.</summary>
public sealed record BuiltInPreset(string Id, string Name, double[] BandsDb);

/// <summary>The equalizer curves that come with the app, and applying a curve to the settings.</summary>
public static class EqPresets
{
    public const string FlatId = "flat";

    // The shapes are the familiar ones for each kind of music (the "smile" for rock and
    // electronic, a raised middle for voices...), set by hand and strong on purpose - up to
    // 9 dB - so that picking one is heard at once, like turning the sliders of a hardware
    // equalizer. They are a starting point, not a measured standard.
    //                                                              31   62  125  250  500   1k   2k   4k   8k  16k
    public static readonly IReadOnlyList<BuiltInPreset> BuiltIn =
    [
        new(FlatId,       "플랫",          [ 0,   0,   0,   0,   0,   0,   0,   0,   0,   0]),
        new("bass",       "저음 강화",     [ 9,   8,   6,   3,   0,   0,   0,   0,   0,   0]),
        new("treble",     "고음 강화",     [ 0,   0,   0,   0,   0,   0,   2,   5,   8,   9]),
        new("vocal",      "보컬",          [-4,  -3,  -2,   2,   5,   6,   6,   4,   2,   0]),
        new("pop",        "팝",            [-2,   1,   4,   6,   5,   2,   0,  -1,   2,   3]),
        new("rock",       "록",            [ 8,   6,   3,  -2,  -4,  -2,   2,   5,   7,   8]),
        new("jazz",       "재즈",          [ 5,   4,   2,   3,  -2,  -2,   0,   2,   4,   5]),
        new("classical",  "클래식",        [ 5,   4,   3,   1,   0,   0,   0,   2,   3,   5]),
        new("electronic", "일렉트로닉",    [ 8,   7,   3,   0,  -4,   2,   0,   3,   6,   8]),
        new("hiphop",     "힙합",          [ 9,   8,   3,   4,  -2,  -2,   2,   0,   3,   5]),
        new("acoustic",   "어쿠스틱",      [ 5,   5,   3,   1,   2,   2,   3,   4,   4,   3]),
        // What the ear loses at low volume: the two ends of the range.
        new("loudness",   "라우드니스",    [ 9,   7,   2,   0,  -2,  -2,   0,   2,   5,   8]),
        // Quiet listening that doesn't carry through walls: less rumble, clearer voices.
        new("latenight",  "심야",          [-9,  -7,  -3,   0,   3,   5,   5,   3,   0,  -3]),
    ];

    public static BuiltInPreset? Find(string? id) => BuiltIn.FirstOrDefault(p => p.Id == id);

    /// <summary>Sets the bands (and preamp) from a built-in or saved preset. False when there is
    /// no preset of that id/name.</summary>
    public static bool Apply(ToneSettings settings, string preset)
    {
        if (Find(preset) is { } builtIn)
        {
            settings.BandsDb = [.. builtIn.BandsDb];
            settings.PreampDb = 0;
        }
        else if (settings.UserPresets.FirstOrDefault(p => p.Name == preset) is { } saved)
        {
            settings.BandsDb = [.. saved.BandsDb];
            settings.PreampDb = saved.PreampDb;
        }
        else
        {
            return false;
        }
        settings.Preset = preset;
        settings.Normalize();
        return true;
    }

    /// <summary>Keeps the current bands under a name (replacing a saved preset of that name).
    /// A built-in's name can't be taken: false, nothing saved.</summary>
    public static bool Save(ToneSettings settings, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || IsBuiltInName(name)) return false;
        settings.UserPresets.RemoveAll(p => p.Name == name);
        settings.UserPresets.Add(new EqPreset { Name = name, PreampDb = settings.PreampDb, BandsDb = [.. settings.BandsDb] });
        settings.Preset = name;
        return true;
    }

    public static bool Rename(ToneSettings settings, string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || IsBuiltInName(newName) || settings.UserPresets.Any(p => p.Name == newName)) return false;
        if (settings.UserPresets.FirstOrDefault(p => p.Name == oldName) is not { } preset) return false;
        preset.Name = newName;
        if (settings.Preset == oldName) settings.Preset = newName;
        return true;
    }

    public static bool Delete(ToneSettings settings, string name)
    {
        if (settings.UserPresets.RemoveAll(p => p.Name == name) == 0) return false;
        if (settings.Preset == name) settings.Preset = null;
        return true;
    }

    public static bool IsBuiltInName(string name) => BuiltIn.Any(p => p.Id == name || p.Name == name);
}
