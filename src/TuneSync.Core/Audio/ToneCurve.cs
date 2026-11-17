using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>Where the equalizer's filters sit, and what a set of levels does to each frequency -
/// worked out from the same filter formulas the player runs, so the drawn curve is the real one.</summary>
public static class ToneCurve
{
    /// <summary>The ten band centers, an octave apart (31 Hz to 16 kHz).</summary>
    public static readonly double[] BandFrequencies = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>The 31 band centers, a third of an octave apart (20 Hz to 20 kHz): the ISO
    /// series, worked out exactly (1 kHz times 2^(n/3)), so every third one is one of the ten.</summary>
    public static readonly double[] ThirdOctaveFrequencies =
        [.. Enumerable.Range(-17, ToneSettings.ThirdOctaveBands).Select(n => 1000 * Math.Pow(2, n / 3.0))];

    /// <summary>The 31 bands' names as the ISO series rounds them (what a graphic equalizer prints under its sliders).</summary>
    public static readonly string[] ThirdOctaveNames =
    [
        "20", "25", "31", "40", "50", "63", "80", "100", "125", "160", "200", "250", "315", "400", "500", "630",
        "800", "1k", "1.25k", "1.6k", "2k", "2.5k", "3.15k", "4k", "5k", "6.3k", "8k", "10k", "12.5k", "16k", "20k",
    ];

    /// <summary>The centers of a set of bands (10 or 31; anything else is taken as the ten).</summary>
    public static double[] Frequencies(int bandCount) =>
        bandCount == ToneSettings.ThirdOctaveBands ? ThirdOctaveFrequencies : BandFrequencies;

    /// <summary>How narrow one of the ten bands' bell is, by how far it is turned: broad for a
    /// small move (a gentle tilt of that region), tighter the further it goes (a marked peak or
    /// notch) - the way the sliders of a hardware graphic equalizer behave.</summary>
    public static double BandQ(double gainDb) => 0.9 + 0.5 * Math.Min(Math.Abs(gainDb), ToneSettings.MaxGainDb) / ToneSettings.MaxGainDb;

    /// <summary>The same for a set of bands: the 31 bands' bells are a third as wide (in octaves)
    /// as the ten's, so they overlap their neighbors just as much - the curve between two sliders
    /// is shaped the same way in either set, only three times finer.</summary>
    public static double BandQ(double gainDb, int bandCount)
    {
        var q = BandQ(gainDb);
        if (bandCount != ToneSettings.ThirdOctaveBands) return q;
        // Q to bandwidth in octaves (N = 2/ln2 asinh(1/2Q)), a third of it, and back.
        return 1 / (2 * Math.Sinh(Math.Asinh(1 / (2 * q)) / 3));
    }

    /// <summary>The most a single band's filter is turned to make the curve meet the sliders.</summary>
    public const double MaxFilterGainDb = 18;

    public const double BassFrequency = 120;
    public const double TrebleFrequency = 6500;

    /// <summary>A gentle slope, like the bass/treble knobs of an amplifier.</summary>
    public const double ShelfQ = 0.6;

    // The filters in the order they run: the bass shelf, the ten bands, the 31 bands, the treble
    // shelf. Only one set of bands is in use at a time (the other is at 0 dB, switched off and
    // costing nothing); keeping both in the chain lets a switch between them glide like any
    // other change - one set fades out while the other fades in - instead of clicking.
    private const int OctaveFirst = 1;
    private const int ThirdOctaveFirst = OctaveFirst + ToneSettings.OctaveBands;

    /// <summary>Filters in the chain (see above).</summary>
    public const int FilterCount = ThirdOctaveFirst + ToneSettings.ThirdOctaveBands + 1;

    /// <summary>The rate the curve is drawn (and the headroom worked out) for; a song's own rate
    /// changes the result only by a hair near the very top.</summary>
    public const double ReferenceSampleRate = 48000;

    /// <summary>Which set of bands an array of levels is for: 31 levels are the third-octave
    /// bands, anything else the ten.</summary>
    public static int BandCountOf(int length) =>
        length == ToneSettings.ThirdOctaveBands ? ToneSettings.ThirdOctaveBands : ToneSettings.OctaveBands;

    /// <summary>Where a set of bands starts in the chain (see <see cref="FilterCount"/>).</summary>
    public static int FirstFilterOf(int bandCount) =>
        bandCount == ToneSettings.ThirdOctaveBands ? ThirdOctaveFirst : OctaveFirst;

    /// <summary>The filter at <paramref name="index"/> (see <see cref="FilterCount"/>) set to a level.</summary>
    public static BiquadCoefficients Filter(int index, double gainDb, double sampleRate)
    {
        if (index == 0) return BiquadCoefficients.LowShelf(sampleRate, BassFrequency, ShelfQ, gainDb);
        if (index == FilterCount - 1) return BiquadCoefficients.HighShelf(sampleRate, TrebleFrequency, ShelfQ, gainDb);
        return index < ThirdOctaveFirst
            ? BiquadCoefficients.Peaking(sampleRate, BandFrequencies[index - OctaveFirst], BandQ(gainDb, ToneSettings.OctaveBands), gainDb)
            : BiquadCoefficients.Peaking(sampleRate, ThirdOctaveFrequencies[index - ThirdOctaveFirst], BandQ(gainDb, ToneSettings.ThirdOctaveBands), gainDb);
    }

    /// <summary>The level of each filter, in running order. The bands' filters are not simply set
    /// to their sliders: see <see cref="SolveBands"/>.</summary>
    public static void FilterGains(ToneSettings settings, Span<double> gains)
    {
        gains[..FilterCount].Clear();
        gains[0] = settings.BassDb;
        var bands = settings.BandsDb ?? [];
        var count = BandCountOf(bands.Length);
        SolveBands(bands, gains.Slice(FirstFilterOf(count), count));
        gains[FilterCount - 1] = settings.TrebleDb;
    }

    private static readonly object SolveLock = new();
    private static readonly double[] SolvedFor = new double[ToneSettings.ThirdOctaveBands];
    private static readonly double[] Solved = new double[ToneSettings.ThirdOctaveBands];
    private static int _solvedCount;

    private const int SolveRounds = 12;

    /// <summary>Solves m x = b (m is n by n, row by row) by Gaussian elimination with partial
    /// pivoting; x is left in <paramref name="b"/>, and <paramref name="m"/> is used up.</summary>
    private static void SolveLinear(Span<double> m, Span<double> b, int n)
    {
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(m[row * n + col]) > Math.Abs(m[pivot * n + col])) pivot = row;
            if (Math.Abs(m[pivot * n + col]) < 1e-12) { b[col] = 0; continue; }
            if (pivot != col)
            {
                for (var k = 0; k < n; k++) (m[col * n + k], m[pivot * n + k]) = (m[pivot * n + k], m[col * n + k]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (var row = col + 1; row < n; row++)
            {
                var factor = m[row * n + col] / m[col * n + col];
                if (factor == 0) continue;
                for (var k = col; k < n; k++) m[row * n + k] -= factor * m[col * n + k];
                b[row] -= factor * b[col];
            }
        }
        for (var row = n - 1; row >= 0; row--)
        {
            if (Math.Abs(m[row * n + row]) < 1e-12) { b[row] = 0; continue; }
            var sum = b[row];
            for (var k = row + 1; k < n; k++) sum -= m[row * n + k] * b[k];
            b[row] = sum / m[row * n + row];
        }
    }

    /// <summary>Works out how far each band's filter has to be turned so that the curve passes
    /// through every slider. Neighboring bells overlap: with each filter simply set to its
    /// slider, a row of raised sliders adds up to more than any of them says, and a lone one
    /// drags its neighbors along. Here the filters are corrected, a few rounds over, by what the
    /// curve is still off at each band's center - what the sliders show is what is heard.
    /// Which set of bands is <paramref name="filterGainsDb"/>'s length (10 or 31).
    /// (The last result is kept: drawing the curve asks for the same sliders many times over.)</summary>
    public static void SolveBands(ReadOnlySpan<double> slidersDb, Span<double> filterGainsDb)
    {
        var count = BandCountOf(filterGainsDb.Length);
        var first = FirstFilterOf(count);
        var centers = Frequencies(count);
        Span<double> wanted = stackalloc double[count];
        for (var i = 0; i < wanted.Length; i++) wanted[i] = i < slidersDb.Length ? slidersDb[i] : 0;

        lock (SolveLock)
        {
            if (count != _solvedCount || !wanted.SequenceEqual(SolvedFor.AsSpan(0, count)))
            {
                Span<double> gains = stackalloc double[count];
                Span<double> off = stackalloc double[count];
                // shape[at * count + band]: what one dB of a band's filter (as it is turned now)
                // does at another band's center
                Span<double> shape = stackalloc double[count * count];
                wanted.CopyTo(gains);
                var flat = true;
                foreach (var level in wanted) flat &= level == 0;
                for (var round = 0; round < SolveRounds && !flat; round++)
                {
                    for (var band = 0; band < count; band++)
                    {
                        // a filter at (next to) 0 dB has no shape of its own: that of a 1 dB one
                        var level = Math.Abs(gains[band]) < 0.05 ? 1 : gains[band];
                        var filter = Filter(first + band, level, ReferenceSampleRate);
                        for (var at = 0; at < count; at++)
                            shape[at * count + band] = filter.MagnitudeDb(ReferenceSampleRate, centers[at]) / level;
                    }
                    var worst = 0.0;
                    for (var at = 0; at < count; at++)
                    {
                        var curve = 0.0;
                        for (var band = 0; band < count; band++) curve += gains[band] * shape[at * count + band];
                        off[at] = wanted[at] - curve;
                        worst = Math.Max(worst, Math.Abs(off[at]));
                    }
                    if (worst < 0.005) break;
                    // The correction that would close the gap at every center at once, were the
                    // bells' shapes to stay as they are (they narrow a little as they grow, hence
                    // a few rounds). Simply adding each band's own gap, as the ten bands once did,
                    // overshoots back and forth where many bells overlap and never settles on 31.
                    SolveLinear(shape, off, count);
                    for (var band = 0; band < count; band++)
                        gains[band] = Math.Clamp(gains[band] + off[band], -MaxFilterGainDb, MaxFilterGainDb);
                }
                // A filter turned by a hair does nothing but cost time.
                for (var band = 0; band < count; band++)
                    if (Math.Abs(gains[band]) < 0.02) gains[band] = 0;
                wanted.CopyTo(SolvedFor);
                gains.CopyTo(Solved);
                _solvedCount = count;
            }
            Solved.AsSpan(0, count).CopyTo(filterGainsDb);
        }
    }

    /// <summary>The same curve on the other set of bands: what <paramref name="bandsDb"/> do, as
    /// the player plays them, read off at the centers of <paramref name="bandCount"/> bands (to a
    /// tenth of a dB, within the sliders' range). This is how ten-band settings and presets
    /// carry over to the 31 bands (and back): between and beyond the ten sliders the new ones
    /// follow the curve the ten made, not a straight line drawn between them.</summary>
    public static double[] Resample(ReadOnlySpan<double> bandsDb, int bandCount)
    {
        var from = BandCountOf(bandsDb.Length);
        var to = BandCountOf(bandCount);
        var result = new double[to];
        if (from == to)
        {
            for (var i = 0; i < to && i < bandsDb.Length; i++) result[i] = ToneSettings.ClampGain(bandsDb[i]);
            return result;
        }
        Span<double> gains = stackalloc double[from];
        SolveBands(bandsDb, gains);
        var first = FirstFilterOf(from);
        var centers = Frequencies(to);
        for (var i = 0; i < to; i++)
        {
            var level = 0.0;
            for (var band = 0; band < from; band++)
                if (gains[band] != 0)
                    level += Filter(first + band, gains[band], ReferenceSampleRate).MagnitudeDb(ReferenceSampleRate, centers[i]);
            // + 0.0 turns a rounded -0 into 0
            result[i] = Math.Round(ToneSettings.ClampGain(level) * 10) / 10 + 0.0;
        }
        return result;
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

    /// <summary><see cref="ResponseDb(ToneSettings, double, double)"/> for many frequencies at
    /// once, each filter built only once (drawing the curve asks for a few hundred, and with
    /// 31 bands building every filter anew for each was most of the work).</summary>
    public static void ResponseDb(ToneSettings settings, ReadOnlySpan<double> frequencies, Span<double> result, double sampleRate = ReferenceSampleRate)
    {
        Span<double> gains = stackalloc double[FilterCount];
        FilterGains(settings, gains);
        Span<BiquadCoefficients> filters = stackalloc BiquadCoefficients[FilterCount];
        var used = 0;
        for (var i = 0; i < FilterCount; i++)
            if (gains[i] != 0) filters[used++] = Filter(i, gains[i], sampleRate);
        for (var at = 0; at < frequencies.Length; at++)
        {
            var total = settings.PreampDb;
            for (var f = 0; f < used; f++) total += filters[f].MagnitudeDb(sampleRate, frequencies[at]);
            result[at] = total;
        }
    }

    /// <summary>The curve's highest point above 0 dB between 20 Hz and 20 kHz - how much the
    /// overall level has to come down so that no frequency ends up louder than in the file.</summary>
    public static double HeadroomDb(ToneSettings settings, double sampleRate = ReferenceSampleRate)
    {
        // twelve points per octave, plus every band's center (where the 31 narrow bells peak)
        const int points = 121;
        Span<double> frequencies = stackalloc double[points + ToneSettings.ThirdOctaveBands];
        var count = 0;
        for (var i = 0; i < points; i++) frequencies[count++] = 20 * Math.Pow(1000, i / (points - 1.0));
        foreach (var center in Frequencies(BandCountOf(settings.BandsDb?.Length ?? 0))) frequencies[count++] = center;
        var levels = frequencies[..count];
        var usable = 0;
        foreach (var frequency in levels)
            if (frequency < sampleRate * 0.47) levels[usable++] = frequency;
        levels = levels[..usable];
        Span<double> response = stackalloc double[usable];
        ResponseDb(settings, levels, response, sampleRate);
        var peak = 0.0;
        foreach (var level in response) peak = Math.Max(peak, level);
        return peak;
    }
}
