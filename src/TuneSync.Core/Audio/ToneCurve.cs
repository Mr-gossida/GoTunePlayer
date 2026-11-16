using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>Where the equalizer's filters sit, and what a set of levels does to each frequency -
/// worked out from the same filter formulas the player runs, so the drawn curve is the real one.</summary>
public static class ToneCurve
{
    /// <summary>Band centers, an octave apart.</summary>
    public static readonly double[] BandFrequencies = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>How narrow a band's bell is, by how far it is turned: broad for a small move (a
    /// gentle tilt of that region), tighter the further it goes (a marked peak or notch) - the
    /// way the sliders of a hardware graphic equalizer behave.</summary>
    public static double BandQ(double gainDb) => 0.9 + 0.5 * Math.Min(Math.Abs(gainDb), ToneSettings.MaxGainDb) / ToneSettings.MaxGainDb;

    /// <summary>The most a single band's filter is turned to make the curve meet the sliders.</summary>
    public const double MaxFilterGainDb = 18;

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
        return BiquadCoefficients.Peaking(sampleRate, BandFrequencies[index - 1], BandQ(gainDb), gainDb);
    }

    /// <summary>The level of each filter, in running order. The bands' filters are not simply set
    /// to their sliders: see <see cref="SolveBands"/>.</summary>
    public static void FilterGains(ToneSettings settings, Span<double> gains)
    {
        gains[0] = settings.BassDb;
        SolveBands(settings.BandsDb, gains.Slice(1, ToneSettings.BandCount));
        gains[FilterCount - 1] = settings.TrebleDb;
    }

    private static readonly object SolveLock = new();
    private static readonly double[] SolvedFor = new double[ToneSettings.BandCount];
    private static readonly double[] Solved = new double[ToneSettings.BandCount];

    /// <summary>Works out how far each band's filter has to be turned so that the curve passes
    /// through every slider. Neighboring bells overlap: with each filter simply set to its
    /// slider, a row of raised sliders adds up to more than any of them says, and a lone one
    /// drags its neighbors along. Here the filters are corrected, a few rounds over, by what the
    /// curve is still off at each band's center - what the sliders show is what is heard.
    /// (The last result is kept: drawing the curve asks for the same sliders many times over.)</summary>
    public static void SolveBands(ReadOnlySpan<double> slidersDb, Span<double> filterGainsDb)
    {
        Span<double> wanted = stackalloc double[ToneSettings.BandCount];
        for (var i = 0; i < wanted.Length; i++) wanted[i] = i < slidersDb.Length ? slidersDb[i] : 0;

        lock (SolveLock)
        {
            if (!wanted.SequenceEqual(SolvedFor))
            {
                Span<double> gains = stackalloc double[ToneSettings.BandCount];
                Span<double> off = stackalloc double[ToneSettings.BandCount];
                wanted.CopyTo(gains);
                var flat = true;
                foreach (var level in wanted) flat &= level == 0;
                for (var round = 0; round < 14 && !flat; round++)
                {
                    for (var at = 0; at < wanted.Length; at++)
                    {
                        var curve = 0.0;
                        for (var band = 0; band < gains.Length; band++)
                            if (gains[band] != 0)
                                curve += Filter(band + 1, gains[band], ReferenceSampleRate).MagnitudeDb(ReferenceSampleRate, BandFrequencies[at]);
                        off[at] = wanted[at] - curve;
                    }
                    for (var band = 0; band < gains.Length; band++)
                        gains[band] = Math.Clamp(gains[band] + off[band], -MaxFilterGainDb, MaxFilterGainDb);
                }
                // A filter turned by a hair does nothing but cost time.
                for (var band = 0; band < gains.Length; band++)
                    if (Math.Abs(gains[band]) < 0.02) gains[band] = 0;
                wanted.CopyTo(SolvedFor);
                gains.CopyTo(Solved);
            }
            Solved.AsSpan().CopyTo(filterGainsDb);
        }
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
