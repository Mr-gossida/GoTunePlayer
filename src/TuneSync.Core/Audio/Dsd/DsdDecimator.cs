using System.Numerics;

namespace TuneSync.Core.Audio.Dsd;

/// <summary>Turns one channel of DSD (bits, the first one in each byte's top bit) into PCM, in
/// two steps.
/// <list type="number">
/// <item>Bits to 1/8 of the DSD rate (352.8 kHz from DSD64): a 1-bit FIR evaluated a byte at a
/// time through lookup tables - for each byte position in the filter, the sum of its eight taps
/// for each of the 256 possible bytes - so a byte costs one table read and an add per eight taps.
/// It keeps 0-50 kHz and takes out (by ~110 dB) all that would fold back into it.</item>
/// <item>Down to the output rate (176.4 kHz) with a sharper FIR in 64-bit: flat to 30 kHz and
/// fully closed from 60 kHz, so the noise DSD keeps above the audio band (its noise shaping
/// rises steeply past 25-30 kHz) doesn't come along into the PCM.</item>
/// </list>
/// The level follows the usual convention for DSD to PCM: 50 % modulation (DSD's "0 dB") comes
/// out as PCM full scale, so the music isn't 6 dB quieter than its PCM release; the hottest SACDs
/// can go a little past that, which the player's limiter catches.</summary>
public sealed class DsdDecimator
{
    private const double Gain = 1 / DsdModulator.PcmFullScale;

    private readonly double[][] _tables;    // [byte position][byte value] -> sum of its 8 taps
    private readonly byte[] _bytes;         // the last bytes, twice over (so a window is contiguous)
    private int _byteAt;

    private readonly double[] _fir2;        // second stage, newest-first
    private readonly double[] _mid;         // first stage's output, twice over
    private readonly int _factor2;
    private int _midAt, _midCount;

    public DsdDecimator(int dsdRate, int outRate)
    {
        var rate1 = dsdRate / 8.0;
        _factor2 = (int)Math.Round(rate1 / outRate);
        if (_factor2 < 1 || Math.Abs(rate1 / _factor2 - outRate) > 1)
            throw new ArgumentException($"DSD at {dsdRate} Hz can't become {outRate} Hz PCM.");

        // stage 1: pass 50 kHz, stop where content would land within 50 kHz after the /8
        var taps1 = Design(dsdRate, 50_000, rate1 - 50_000, 110, multipleOf: 8);
        _tables = new double[taps1.Length / 8][];
        for (var k = 0; k < _tables.Length; k++)
        {
            var table = new double[256];
            for (var value = 0; value < 256; value++)
            {
                double sum = 0;
                // bit j counted from the bottom is the newest of the byte's eight
                for (var j = 0; j < 8; j++) sum += taps1[k * 8 + j] * (((value >> j) & 1) * 2 - 1);
                table[value] = sum * Gain;
            }
            _tables[k] = table;
        }
        _bytes = new byte[_tables.Length * 2];
        Array.Fill(_bytes, (byte)0x69); // DSD silence (equal ones and zeros)

        // stage 2: flat to 30 kHz, closed from 60 kHz
        _fir2 = _factor2 == 1 ? [1.0] : Design(rate1, 30_000, 60_000, 120, multipleOf: 1);
        _mid = new double[_fir2.Length * 2];
    }

    /// <summary>Input bytes per output sample.</summary>
    public int BytesPerSample => _factor2;

    /// <summary>Feeds bytes in; writes the PCM samples they complete into <paramref name="output"/>
    /// and returns how many. Bytes that don't complete a sample are kept for the next call.</summary>
    public int Process(ReadOnlySpan<byte> input, Span<double> output, int stride = 1)
    {
        var produced = 0;
        var history = _tables.Length;
        foreach (var value in input)
        {
            // newest byte at the window's end; both copies so the window never wraps
            _bytes[_byteAt] = _bytes[_byteAt + history] = value;
            _byteAt = (_byteAt + 1) % history;
            // window: _bytes[_byteAt .. _byteAt + history), oldest first
            double y = 0;
            for (var k = 0; k < history; k++) y += _tables[k][_bytes[_byteAt + history - 1 - k]];

            _mid[_midAt] = _mid[_midAt + _fir2.Length] = y;
            _midAt = (_midAt + 1) % _fir2.Length;
            if (++_midCount < _factor2) continue;
            _midCount = 0;
            output[produced * stride] = Dot(_fir2, _mid.AsSpan(_midAt, _fir2.Length));
            produced++;
        }
        return produced;
    }

    /// <summary>Forgets what came before (after a seek).</summary>
    public void Reset()
    {
        Array.Fill(_bytes, (byte)0x69);
        Array.Clear(_mid);
        _midCount = 0;
    }

    /// <summary>A Kaiser-windowed low-pass, symmetric, for <paramref name="rate"/>: flat to
    /// <paramref name="pass"/> Hz, <paramref name="stopDb"/> down from <paramref name="stop"/> Hz.
    /// Taps oldest-first (the same both ways), normalized to unity gain.</summary>
    private static double[] Design(double rate, double pass, double stop, double stopDb, int multipleOf)
    {
        var transition = (stop - pass) / rate;
        var length = (int)Math.Ceiling((stopDb - 8) / (2.285 * 2 * Math.PI * transition)) + 1;
        length = (length + multipleOf - 1) / multipleOf * multipleOf;
        var cutoff = (pass + stop) / 2 / rate;
        var beta = stopDb > 50 ? 0.1102 * (stopDb - 8.7) : 0.5842 * Math.Pow(stopDb - 21, 0.4) + 0.07886 * (stopDb - 21);
        var taps = new double[length];
        var middle = (length - 1) / 2.0;
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            var t = i - middle;
            var x = 2 * cutoff * t;
            var sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var r = t / (middle + 0.5);
            taps[i] = 2 * cutoff * sinc * BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r)));
            sum += taps[i];
        }
        for (var i = 0; i < length; i++) taps[i] /= sum;
        return taps;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, y = x * x / 4;
        for (var k = 1; k < 80; k++)
        {
            term *= y / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
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
}
