using System.Numerics;

namespace TuneSync.Core.Audio;

/// <summary>One second-order filter section, normalized so a0 = 1:
/// y[n] = B0 x[n] + B1 x[n-1] + B2 x[n-2] - A1 y[n-1] - A2 y[n-2].
/// The shelves use the widely used "Audio EQ Cookbook" formulas (Robert Bristow-Johnson); the
/// bell is a "matched" design that keeps its shape right up to the top of the hearing range.</summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Passes the signal through untouched.</summary>
    public static readonly BiquadCoefficients Identity = new(1, 0, 0, 0, 0);

    /// <summary>A filter this close to half the sample rate can't be built faithfully (and one
    /// above it not at all): such a band is simply left out for that song.</summary>
    private const double HighestUsableFraction = 0.47;

    private static bool Unusable(double sampleRate, double frequency, double gainDb) =>
        Math.Abs(gainDb) < 1e-9 || frequency <= 0 || frequency >= sampleRate * HighestUsableFraction;

    /// <summary>A bell: raises or lowers the sound around <paramref name="frequency"/>.
    /// The cookbook bell gets squeezed as it nears half the sample rate (at 44.1 kHz a 16 kHz
    /// band came out narrow and lopsided, several dB off above its center). This one follows
    /// Martin Vicanek's "Matched Second Order Digital Filters" (2016): the poles are those of the
    /// analog bell (impulse invariance) and the zeros are placed so that the level is right at
    /// 0 Hz, at the center and at half the sample rate - within a fraction of a dB of the analog
    /// curve over the whole range.</summary>
    public static BiquadCoefficients Peaking(double sampleRate, double frequency, double q, double gainDb)
    {
        if (Unusable(sampleRate, frequency, gainDb)) return Identity;
        if (gainDb < 0)
        {
            // A cut is the boost of the same size turned upside down (numerator and denominator
            // swapped): the two then cancel exactly, and the cut is as true to shape as the boost.
            var boost = Peaking(sampleRate, frequency, q, -gainDb);
            var cut = new BiquadCoefficients(1 / boost.B0, boost.A1 / boost.B0, boost.A2 / boost.B0, boost.B1 / boost.B0, boost.B2 / boost.B0);
            if (Math.Abs(cut.A2) < 1 && Math.Abs(cut.A1) < 1 + cut.A2) return cut;
        }
        var a = Math.Pow(10, gainDb / 40);
        var gain = a * a;
        var w = 2 * Math.PI * frequency / sampleRate;

        // Poles.
        var damping = 1 / (2 * q * a);
        var decay = Math.Exp(-damping * w);
        var a1 = damping <= 1
            ? -2 * decay * Math.Cos(Math.Sqrt(1 - damping * damping) * w)
            : -2 * decay * Math.Cosh(Math.Sqrt(damping * damping - 1) * w);
        var a2 = decay * decay;

        // Zeros, from the squared magnitude wanted at the three points.
        var sin = Math.Sin(w / 2);
        double p1 = sin * sin, p0 = 1 - p1, p2 = 4 * p0 * p1;
        double d0 = (1 + a1 + a2) * (1 + a1 + a2), d1 = (1 - a1 + a2) * (1 - a1 + a2), d2 = -4 * a2;
        var r1 = (d0 * p0 + d1 * p1 + d2 * p2) * gain * gain;
        var r2 = (-d0 + d1 + 4 * (p0 - p1) * d2) * gain * gain;
        var n0 = d0;
        var n2 = (r1 - r2 * p1 - n0) / (4 * p1 * p1);
        var n1 = r2 + n0 + 4 * (p1 - p0) * n2;
        var half = 0.5 * (Math.Sqrt(n0) + Math.Sqrt(n1));
        var b0 = 0.5 * (half + Math.Sqrt(half * half + n2));
        var b1 = 0.5 * (Math.Sqrt(n0) - Math.Sqrt(n1));
        var b2 = -n2 / (4 * b0);
        if (double.IsFinite(b0) && double.IsFinite(b1) && double.IsFinite(b2) && b0 != 0) return new(b0, b1, b2, a1, a2);

        // Out of the design's reach (never seen with the equalizer's own bands): the cookbook bell.
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
