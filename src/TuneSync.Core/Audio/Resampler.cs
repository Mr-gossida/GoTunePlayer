using System.Numerics;
using NAudio.Dsp;
using TuneSync.Core.Models;

namespace TuneSync.Core.Audio;

/// <summary>Converts stereo sound from one sample rate to another, a block at a time. The
/// player asks how many of the song's frames make the device frames it has room for
/// (<see cref="InputFor"/>), then hands over at most that many (<see cref="Process"/>).</summary>
public interface IResampler
{
    /// <summary>Song frames needed for exactly <paramref name="outFrames"/> more device frames.</summary>
    int InputFor(int outFrames);

    /// <summary>Takes in <paramref name="input"/> (stereo, interleaved; at most what
    /// <see cref="InputFor"/> asked for) and writes the device frames it makes into
    /// <paramref name="output"/>. Returns how many frames were written.</summary>
    int Process(ReadOnlySpan<double> input, Span<double> output);

    /// <summary>Input frames held back to look ahead (pushed out at the song's end with silence).</summary>
    int LookaheadFrames { get; }
}

public static class Resamplers
{
    /// <summary>The best converter for this pair of rates: the polyphase one whenever the ratio
    /// is a fraction with a manageable number of phases (every rate in use), else the general one.</summary>
    public static IResampler Create(int inRate, int outRate, ResampleFilter filter) =>
        PolyphaseResampler.CanConvert(inRate, outRate)
            ? new PolyphaseResampler(inRate, outRate, filter)
            : new WdlAdapter(inRate, outRate);

    public static string FilterName(ResampleFilter filter) => filter switch
    {
        ResampleFilter.MinimumPhase => "최소 위상",
        ResampleFilter.Soft => "부드러운 필터",
        _ => "선형 위상",
    };

    /// <summary>NAudio's resampler, for odd rate pairs the polyphase converter can't do exactly.</summary>
    private sealed class WdlAdapter : IResampler
    {
        private readonly WdlResampler _wdl = new();
        private float[]? _in;
        private int _offset;
        private float[] _out = [];

        public WdlAdapter(int inRate, int outRate)
        {
            _wdl.SetMode(true, 2, false);
            _wdl.SetFilterParms();
            _wdl.SetFeedMode(false);
            _wdl.SetRates(inRate, outRate);
        }

        public int LookaheadFrames => 0;

        public int InputFor(int outFrames)
        {
            var needed = _wdl.ResamplePrepare(outFrames, 2, out _in, out _offset);
            if (_out.Length < outFrames * 2) _out = new float[outFrames * 2];
            return needed;
        }

        public int Process(ReadOnlySpan<double> input, Span<double> output)
        {
            for (var i = 0; i < input.Length; i++) _in![_offset + i] = (float)input[i];
            var count = _wdl.ResampleOut(_out, 0, input.Length / 2, output.Length / 2, 2);
            for (var i = 0; i < count * 2; i++) output[i] = _out[i];
            return count;
        }
    }
}

/// <summary>A high-quality sample rate converter: windowed-sinc interpolation computed in 64-bit,
/// exactly, for a rational ratio L/M - the output instant m falls at input instant m·M/L, so
/// with L precomputed phases every output sample gets its own exact filter (no interpolation
/// between table entries, no drift). The filter is designed for 24-bit sound or better: about
/// 140 dB of stopband rejection, so nothing above the original's top (images, when upsampling;
/// aliases, when converting down) is left within reach of any DAC.
///
/// Three filters:
/// <list type="bullet">
/// <item>Linear phase (선형 위상): flat to just under the original Nyquist, then a steep fall; every
/// frequency delayed alike. The textbook-exact reconstruction. Its ringing is symmetric -
/// some of it comes before a sharp transient ("pre-ringing"), though only at the very top of the band.</item>
/// <item>Minimum phase (최소 위상): the same magnitude, with all the ringing after the transient
/// (none before), as many DACs' "minimum phase" setting. Phase near the top of the band shifts a little.</item>
/// <item>Soft (부드러운 필터): starts falling earlier (from about 0.8 of Nyquist, ~17.6 kHz for a
/// CD) and reaches full rejection at Nyquist itself - a shorter filter with much less ringing,
/// at the cost of the last octave's top few kHz rolling off gently.</item>
/// </list>
/// Filter tables are shared between converters with the same rates and filter.</summary>
public sealed class PolyphaseResampler : IResampler
{
    /// <summary>Stopband rejection the filters are designed for.</summary>
    private const double StopbandDb = 140;

    /// <summary>The most phases (L) a table may have: every pair of the usual rates needs at
    /// most 2560 (44.1 kHz to 768 kHz).</summary>
    private const int MaxPhases = 8192;

    private static readonly Dictionary<(int, int, ResampleFilter), Table> Tables = [];

    private readonly int _l, _m;
    private readonly Table _table;

    // The song's frames still needed, per channel, from _base (the input index of _left[0]) on.
    private double[] _left = new double[8192], _right = new double[8192];
    private int _count;          // frames held
    private long _base;          // input index of the first held frame
    private long _inTotal;       // input frames taken in all
    private long _outIndex;      // next output frame's number

    public PolyphaseResampler(int inRate, int outRate, ResampleFilter filter = ResampleFilter.Sharp)
    {
        var g = Gcd(inRate, outRate);
        _l = outRate / g;
        _m = inRate / g;
        lock (Tables)
        {
            if (!Tables.TryGetValue((inRate, outRate, filter), out var table))
            {
                table = Table.Build(_l, _m, filter);
                Tables[(inRate, outRate, filter)] = table;
            }
            _table = table;
        }
        // Silence before the song: the first outputs look back that far.
        _base = -_table.Back;
        _count = _table.Back;
    }

    public static bool CanConvert(int inRate, int outRate) =>
        inRate > 0 && outRate > 0 && outRate / Gcd(inRate, outRate) <= MaxPhases;

    public int LookaheadFrames => _table.Taps - 1 - _table.Back;

    /// <summary>Taps per output sample (for tests and the curious).</summary>
    public int Taps => _table.Taps;

    /// <summary>Output frame m reads input frames from n - Back to n - Back + Taps - 1, where
    /// n = floor(m·M/L); so it can be made once input frame n - Back + Taps - 1 is in.</summary>
    private long LastInputFor(long m) => m * _m / _l - _table.Back + _table.Taps - 1;

    public int InputFor(int outFrames)
    {
        if (outFrames <= 0) return 0;
        var needed = LastInputFor(_outIndex + outFrames - 1) + 1 - _inTotal;
        return (int)Math.Max(0, needed);
    }

    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        var frames = input.Length / 2;
        Append(input, frames);
        var maxOut = output.Length / 2;
        var taps = _table.Taps;
        var produced = 0;
        while (produced < maxOut && LastInputFor(_outIndex) < _inTotal)
        {
            var n = _outIndex * _m / _l;
            var phase = (int)(_outIndex * _m % _l);
            var start = (int)(n - _table.Back - _base);
            var coef = _table.Coefficients.AsSpan(phase * taps, taps);
            output[produced * 2] = Dot(coef, _left.AsSpan(start, taps));
            output[produced * 2 + 1] = Dot(coef, _right.AsSpan(start, taps));
            produced++;
            _outIndex++;
        }
        // Drop what no later output reads.
        var keepFrom = _outIndex * _m / _l - _table.Back;
        var drop = (int)Math.Clamp(keepFrom - _base, 0, _count);
        if (drop > 0)
        {
            Array.Copy(_left, drop, _left, 0, _count - drop);
            Array.Copy(_right, drop, _right, 0, _count - drop);
            _count -= drop;
            _base += drop;
        }
        return produced;
    }

    private void Append(ReadOnlySpan<double> input, int frames)
    {
        if (_count + frames > _left.Length)
        {
            var size = _left.Length;
            while (size < _count + frames) size *= 2;
            Array.Resize(ref _left, size);
            Array.Resize(ref _right, size);
        }
        for (var i = 0; i < frames; i++)
        {
            _left[_count + i] = input[2 * i];
            _right[_count + i] = input[2 * i + 1];
        }
        _count += frames;
        _inTotal += frames;
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var sum = 0.0;
        var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<double>.Count * 2)
        {
            var acc = Vector<double>.Zero;
            var width = Vector<double>.Count;
            for (; i <= a.Length - width; i += width)
                acc += new Vector<double>(a.Slice(i, width)) * new Vector<double>(b.Slice(i, width));
            sum = Vector.Sum(acc);
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    /// <summary>The coefficients for every phase: row p (output instant at fraction p/L past an
    /// input sample) holds Taps values, for input frames n - Back .. n - Back + Taps - 1.</summary>
    private sealed class Table
    {
        public required double[] Coefficients { get; init; }
        public required int Taps { get; init; }
        public required int Back { get; init; }

        public static Table Build(int l, int m, ResampleFilter filter)
        {
            // Everything in input samples. Converting down, the filter works below the output's
            // Nyquist instead, and grows longer by the same factor.
            var scale = Math.Min(1.0, (double)l / m);
            // Passband edge and stopband edge as fractions of the (lower) Nyquist.
            var (pass, stop) = filter == ResampleFilter.Soft ? (0.80, 1.0) : (0.91, 1.0);
            var cutoff = 0.5 * scale * (pass + stop) / 2;              // cycles per input sample
            var transition = 0.5 * scale * (stop - pass);               // cycles per input sample
            var beta = 0.1102 * (StopbandDb - 8.7);
            var length = (int)Math.Ceiling((StopbandDb - 8) / (2.285 * 2 * Math.PI * transition)) + 1;
            var half = (length + 1) / 2;                                // zero crossings' worth each side
            var taps = 2 * half;

            Func<double, double> response;
            int back;
            if (filter == ResampleFilter.MinimumPhase)
            {
                var minimum = MinimumPhase(cutoff, half, beta, out var oversample);
                response = t =>
                {
                    // continuous response from the finely sampled one (cubic between samples)
                    var x = t * oversample;
                    var i = (int)Math.Floor(x);
                    if (i < 0 || i >= minimum.Length - 1) return 0;
                    var f = x - i;
                    double p0 = i > 0 ? minimum[i - 1] : 0, p1 = minimum[i], p2 = minimum[i + 1], p3 = i + 2 < minimum.Length ? minimum[i + 2] : 0;
                    return p1 + 0.5 * f * (p2 - p0 + f * (2 * p0 - 5 * p1 + 4 * p2 - p3 + f * (3 * (p1 - p2) + p3 - p0)));
                };
                back = taps - 1; // causal: only the past
            }
            else
            {
                response = t => Windowed(t, cutoff, half, beta);
                back = half - 1;
            }

            var coefficients = new double[l * taps];
            for (var p = 0; p < l; p++)
            {
                var frac = (double)p / l;
                var row = coefficients.AsSpan(p * taps, taps);
                var sum = 0.0;
                for (var j = 0; j < taps; j++)
                {
                    // tap j reads input n - back + j, which lies (frac + back - j) samples before the output instant
                    row[j] = response(frac + back - j);
                    sum += row[j];
                }
                // exactly unity gain at DC for every phase (no ripple at the phase rate)
                if (Math.Abs(sum) > 1e-12)
                    for (var j = 0; j < taps; j++) row[j] /= sum;
            }
            return new Table { Coefficients = coefficients, Taps = taps, Back = back };
        }

        /// <summary>The ideal low-pass (sinc) at <paramref name="cutoff"/> under a Kaiser window
        /// reaching zero <paramref name="half"/> samples either side.</summary>
        private static double Windowed(double t, double cutoff, int half, double beta)
        {
            if (Math.Abs(t) >= half) return 0;
            var x = 2 * cutoff * t;
            var sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var r = t / half;
            var window = BesselI0(beta * Math.Sqrt(1 - r * r)) / BesselI0(beta);
            return 2 * cutoff * sinc * window;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, y = x * x / 4;
            for (var k = 1; k < 60; k++)
            {
                term *= y / (k * k);
                sum += term;
                if (term < sum * 1e-17) break;
            }
            return sum;
        }

        /// <summary>The minimum-phase twin of the linear-phase filter: the same magnitude response,
        /// all of its energy as early as possible (homomorphic method: fold the cepstrum of the
        /// log magnitude). Sampled <paramref name="oversample"/> times per input sample.</summary>
        private static double[] MinimumPhase(double cutoff, int half, double beta, out int oversample)
        {
            oversample = 64;
            var length = 2 * half * oversample;
            var size = 1;
            while (size < length * 8) size <<= 1;
            var re = new double[size];
            var im = new double[size];
            for (var k = 0; k < length; k++)
                re[k] = Windowed((k - length / 2.0) / oversample, cutoff, half, beta) / oversample;

            Fft.Transform(re, im, inverse: false);
            // log magnitude (floored far below the stopband), then the real cepstrum
            for (var k = 0; k < size; k++)
            {
                var magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                re[k] = Math.Log(Math.Max(magnitude, 1e-12));
                im[k] = 0;
            }
            Fft.Transform(re, im, inverse: true);
            // fold: keep c[0] and c[N/2], double the positive quefrencies, drop the negative ones
            for (var k = 1; k < size / 2; k++)
            {
                re[k] *= 2;
                im[k] *= 2;
            }
            for (var k = size / 2 + 1; k < size; k++) re[k] = im[k] = 0;
            Fft.Transform(re, im, inverse: false);
            for (var k = 0; k < size; k++)
            {
                var magnitude = Math.Exp(re[k]);
                var angle = im[k];
                re[k] = magnitude * Math.Cos(angle);
                im[k] = magnitude * Math.Sin(angle);
            }
            Fft.Transform(re, im, inverse: true);

            var result = new double[length];
            for (var k = 0; k < length; k++) result[k] = re[k] * oversample;
            // a gentle fade over the last fifth: the tail that's cut off ends at zero, not a step
            var fade = length / 5;
            for (var k = 0; k < fade; k++)
            {
                var w = 0.5 + 0.5 * Math.Cos(Math.PI * (k + 1) / fade);
                result[length - fade + k] *= w;
            }
            return result;
        }
    }
}

/// <summary>A plain in-place radix-2 FFT (the inverse one scaled by 1/N).</summary>
internal static class Fft
{
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
        if (inverse)
            for (var i = 0; i < n; i++)
            {
                re[i] /= n;
                im[i] /= n;
            }
    }
}
