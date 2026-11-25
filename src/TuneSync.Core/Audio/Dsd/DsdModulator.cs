using System.Numerics;

namespace TuneSync.Core.Audio.Dsd;

/// <summary>Turns PCM into DSD: a fifth-order sigma-delta modulator, one bit per sample at the
/// DSD rate (2.8224 MHz for DSD64). The rounding noise of a single bit is huge, so the loop
/// shapes it: the noise transfer function has five zeros spread over the audio band (placed for
/// the least noise across it) and five poles that hold its gain above the band to 1.5 - the
/// usual bound for a one-bit loop of this order to stay stable. At DSD64 that leaves the audio
/// band some 120 dB clean, with the noise pushed up above it, which is what DSD is.
///
/// The loop is written in error-feedback form: v = x + F(e), y = sign(v), e = y - v, with
/// F = (B - A) / A, so that y = x + (B / A)·e. Input is in DSD units: ±1 is a bit's full swing,
/// and 0.5 (50 % modulation) is what a 0 dBFS PCM sample becomes - DSD's own "0 dB". Should the
/// loop ever run away (a sustained input far too hot), it is reset instead of howling.</summary>
public sealed class DsdModulator
{
    public const int Order = 5;

    /// <summary>PCM full scale in DSD units (50 % modulation).</summary>
    public const double PcmFullScale = 0.5;

    private readonly double[] _c, _a;          // F's numerator (B - A) and denominator (A), from z^-1 on
    private readonly double[] _e = new double[Order], _s = new double[Order];
    private int _at;

    /// <summary>Times the loop had to be reset (should stay 0).</summary>
    public int Resets { get; private set; }

    /// <param name="oversampling">DSD rate over twice the audio band (64 for DSD64 with a
    /// 22.05 kHz band, 128 for DSD128).</param>
    public DsdModulator(int oversampling = 64)
    {
        var (b, a) = NoiseTransfer(oversampling);
        _c = new double[Order];
        _a = new double[Order];
        for (var k = 1; k <= Order; k++)
        {
            _c[k - 1] = b[k] - a[k];
            _a[k - 1] = a[k];
        }
    }

    /// <summary>One sample in (DSD units), one bit out (true = +1).</summary>
    public bool Next(double x)
    {
        // F(e): from the last Order errors and outputs of F
        double s = 0;
        for (var k = 0; k < Order; k++)
        {
            var at = (_at - 1 - k + Order) % Order;
            s += _c[k] * _e[at] - _a[k] * _s[at];
        }
        var v = x + s;
        var bit = v >= 0;
        var e = (bit ? 1.0 : -1.0) - v;
        // "not within" rather than "above": a NaN (one bad sample in, e.g.) compares false both
        // ways and would otherwise stay in the loop for good - every bit then comes out the same,
        // which a DAC takes as DSD at a constant level, i.e. silence
        if (!(Math.Abs(s) <= 50) || double.IsNaN(x))
        {
            // ran away: start again from rest
            Array.Clear(_e);
            Array.Clear(_s);
            Resets++;
            e = s = 0;
        }
        _e[_at] = e;
        _s[_at] = s;
        _at = (_at + 1) % Order;
        return bit;
    }

    /// <summary>Fills <paramref name="bytes"/> with bits for <paramref name="input"/> (8 samples
    /// per byte, the first one in the top bit - DSD's usual order).</summary>
    public void Modulate(ReadOnlySpan<double> input, Span<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = 0;
            for (var bit = 0; bit < 8; bit++)
                value = value << 1 | (Next(input[i * 8 + bit]) ? 1 : 0);
            bytes[i] = (byte)value;
        }
    }

    /// <summary>The noise transfer function B(z)/A(z) (coefficients of z^0..z^-Order, both
    /// starting with 1): zeros at the optimal spots across the band (Gauss points), poles of a
    /// Butterworth low-pass whose corner is chosen so the gain at Nyquist - the highest - is 1.5.</summary>
    public static (double[] B, double[] A) NoiseTransfer(int oversampling, double maxGain = 1.5)
    {
        var band = Math.PI / oversampling;
        double[] spots = [0, 0.5384693101056831, -0.5384693101056831, 0.9061798459386640, -0.9061798459386640];
        var b = Polynomial(spots.Select(t => Complex.FromPolarCoordinates(1, t * band)));

        double[] Poles(double corner)
        {
            // Butterworth low-pass poles through the bilinear transform (s = 2(1 - z^-1)/(1 + z^-1))
            var warped = 2 * Math.Tan(corner / 2);
            var poles = new List<Complex>();
            for (var k = 0; k < Order; k++)
            {
                var s = warped * Complex.Exp(Complex.ImaginaryOne * Math.PI * (2 * k + Order + 1) / (2 * Order));
                poles.Add((2 + s) / (2 - s));
            }
            return Polynomial(poles);
        }

        double GainAtNyquist(double[] numerator, double[] denominator)
        {
            double n = 0, d = 0;
            for (var k = 0; k <= Order; k++)
            {
                var sign = k % 2 == 0 ? 1 : -1;
                n += numerator[k] * sign;
                d += denominator[k] * sign;
            }
            return Math.Abs(n / d);
        }

        double low = 1e-5, high = Math.PI * 0.95;
        for (var i = 0; i < 100; i++)
        {
            var mid = (low + high) / 2;
            if (GainAtNyquist(b, Poles(mid)) > maxGain) high = mid;
            else low = mid;
        }
        return (b, Poles(low));
    }

    /// <summary>The real coefficients of the product of (1 - r·z^-1) over the roots.</summary>
    private static double[] Polynomial(IEnumerable<Complex> roots)
    {
        var p = new List<Complex> { Complex.One };
        foreach (var r in roots)
        {
            var next = new Complex[p.Count + 1];
            for (var k = 0; k < p.Count; k++)
            {
                next[k] += p[k];
                next[k + 1] -= r * p[k];
            }
            p = [.. next];
        }
        return [.. p.Select(c => c.Real)];
    }
}
