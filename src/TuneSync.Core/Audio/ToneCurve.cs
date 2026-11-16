using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>Where the equalizer's filters sit, and what a set of levels does to each frequency -
/// worked out from the same filter formulas the player runs, so the drawn curve is the real one.</summary>
public static class ToneCurve
{
    /// <summary>Band centers, an octave apart.</summary>
    public static readonly double[] BandFrequencies = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>One octave wide: neighbors overlap enough to add up to a smooth curve.</summary>
    public const double BandQ = 1.41;

    public const double BassFrequency = 120;
    public const double TrebleFrequency = 6500;

    /// <summary>A gentle slope, like the bass/treble knobs of an amplifier.</summary>
    public const double ShelfQ = 0.6;

    /// <summary>Filters in the order they run: bass shelf, the ten bands, treble shelf.</summary>
    public const int FilterCount = ToneSettings.BandCount + 2;

    /// <summary>The rate the curve is drawn (and the headroom worked out) for; a song's own rate
    /// changes the result only by a hair near the very top.</summary>
    public const double ReferenceSampleRate = 48000;

    /// <summary>The filter at <paramref name="index"/> (see <see cref="FilterCount"/>) set to a level.</summary>
    public static BiquadCoefficients Filter(int index, double gainDb, double sampleRate)
    {
        if (index == 0) return BiquadCoefficients.LowShelf(sampleRate, BassFrequency, ShelfQ, gainDb);
        if (index == FilterCount - 1) return BiquadCoefficients.HighShelf(sampleRate, TrebleFrequency, ShelfQ, gainDb);
        return BiquadCoefficients.Peaking(sampleRate, BandFrequencies[index - 1], BandQ, gainDb);
    }

    /// <summary>The level of each filter, in running order.</summary>
    public static void FilterGains(ToneSettings settings, Span<double> gains)
    {
        gains[0] = settings.BassDb;
        for (var i = 0; i < ToneSettings.BandCount; i++) gains[i + 1] = i < settings.BandsDb.Length ? settings.BandsDb[i] : 0;
        gains[FilterCount - 1] = settings.TrebleDb;
    }

    /// <summary>What the filters and the preamp together do to a tone of this frequency, in dB
    /// (without the automatic headroom).</summary>
    public static double ResponseDb(ToneSettings settings, double frequency, double sampleRate = ReferenceSampleRate)
    {
        Span<double> gains = stackalloc double[FilterCount];
        FilterGains(settings, gains);
        var total = settings.PreampDb;
        for (var i = 0; i < FilterCount; i++)
            if (gains[i] != 0) total += Filter(i, gains[i], sampleRate).MagnitudeDb(sampleRate, frequency);
        return total;
    }

    /// <summary>The curve's highest point above 0 dB between 20 Hz and 20 kHz - how much the
    /// overall level has to come down so that no frequency ends up louder than in the file.</summary>
    public static double HeadroomDb(ToneSettings settings, double sampleRate = ReferenceSampleRate)
    {
        const int points = 121; // twelve per octave
        var peak = 0.0;
        for (var i = 0; i < points; i++)
        {
            var frequency = 20 * Math.Pow(1000, i / (points - 1.0));
            if (frequency >= sampleRate * 0.47) break;
            peak = Math.Max(peak, ResponseDb(settings, frequency, sampleRate));
        }
        return peak;
    }
}
