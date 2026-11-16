using System.Numerics;

namespace TuneSync.Core.Audio;

/// <summary>One second-order filter section, normalized so a0 = 1:
/// y[n] = B0 x[n] + B1 x[n-1] + B2 x[n-2] - A1 y[n-1] - A2 y[n-2].
/// The formulas are the widely used "Audio EQ Cookbook" ones (Robert Bristow-Johnson).</summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Passes the signal through untouched.</summary>
    public static readonly BiquadCoefficients Identity = new(1, 0, 0, 0, 0);

    /// <summary>A filter this close to half the sample rate can't be built faithfully (and one
    /// above it not at all): such a band is simply left out for that song.</summary>
    private const double HighestUsableFraction = 0.47;

    private static bool Unusable(double sampleRate, double frequency, double gainDb) =>
        Math.Abs(gainDb) < 1e-9 || frequency <= 0 || frequency >= sampleRate * HighestUsableFraction;

    /// <summary>A bell: raises or lowers the sound around <paramref name="frequency"/>.</summary>
    public static BiquadCoefficients Peaking(double sampleRate, double frequency, double q, double gainDb)
    {
        if (Unusable(sampleRate, frequency, gainDb)) return Identity;
        var a = Math.Pow(10, gainDb / 40);
        var w = 2 * Math.PI * frequency / sampleRate;
        var cos = Math.Cos(w);
        var alpha = Math.Sin(w) / (2 * q);
        var a0 = 1 + alpha / a;
        return new((1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0);
    }

    /// <summary>Raises or lowers everything below <paramref name="frequency"/> (the middle of the slope).</summary>
    public static BiquadCoefficients LowShelf(double sampleRate, double frequency, double q, double gainDb)
    {
        if (Unusable(sampleRate, frequency, gainDb)) return Identity;
        var a = Math.Pow(10, gainDb / 40);
        var w = 2 * Math.PI * frequency / sampleRate;
        var cos = Math.Cos(w);
        var root = 2 * Math.Sqrt(a) * Math.Sin(w) / (2 * q);
        var a0 = (a + 1) + (a - 1) * cos + root;
        return new(
            a * ((a + 1) - (a - 1) * cos + root) / a0,
            2 * a * ((a - 1) - (a + 1) * cos) / a0,
            a * ((a + 1) - (a - 1) * cos - root) / a0,
            -2 * ((a - 1) + (a + 1) * cos) / a0,
            ((a + 1) + (a - 1) * cos - root) / a0);
    }

    /// <summary>Raises or lowers everything above <paramref name="frequency"/> (the middle of the slope).</summary>
    public static BiquadCoefficients HighShelf(double sampleRate, double frequency, double q, double gainDb)
    {
        if (Unusable(sampleRate, frequency, gainDb)) return Identity;
        var a = Math.Pow(10, gainDb / 40);
        var w = 2 * Math.PI * frequency / sampleRate;
        var cos = Math.Cos(w);
        var root = 2 * Math.Sqrt(a) * Math.Sin(w) / (2 * q);
        var a0 = (a + 1) - (a - 1) * cos + root;
        return new(
            a * ((a + 1) + (a - 1) * cos + root) / a0,
            -2 * a * ((a - 1) + (a + 1) * cos) / a0,
            a * ((a + 1) + (a - 1) * cos - root) / a0,
            2 * ((a - 1) - (a + 1) * cos) / a0,
            ((a + 1) - (a - 1) * cos - root) / a0);
    }

    /// <summary>How much this section raises (+) or lowers (-) a tone of the given frequency, in dB.</summary>
    public double MagnitudeDb(double sampleRate, double frequency)
    {
        var w = 2 * Math.PI * frequency / sampleRate;
        var z1 = Complex.FromPolarCoordinates(1, -w);
        var z2 = Complex.FromPolarCoordinates(1, -2 * w);
        var h = (B0 + B1 * z1 + B2 * z2) / (1 + A1 * z1 + A2 * z2);
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-12));
    }
}
