using NAudio.Dsp;

namespace TuneSync.Core.Audio;

/// <summary>Turns the sound going out (<see cref="OutputScope"/>) into what the equalizer's live
/// displays show, a frame at a time: a spectrum on the equalizer's own frequency scale and a
/// level meter per channel. Levels rise at once and fall back slowly, the way a meter's needle
/// does, so a picture redrawn sixty times a second reads calmly; with no sound they sink to
/// nothing. Meant for one thread (the screen's).</summary>
public sealed class LiveAnalyzer
{
    private const int FftOrder = 12;
    private const int FftSize = 1 << FftOrder;      // ~93 ms at 44.1 kHz: enough for the 31 Hz band
    private const double SpectrumFloorDb = -72;     // the bottom of the spectrum
    private const double TiltDbPerOctave = 3;       // music has less energy up high; lift it around 1 kHz
    public const double MeterFloorDb = -60;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _stereo = new float[FftSize * 2];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2 + 1];

    // Each band's bins (or, too narrow for a bin of its own, the fractional bin at its middle).
    private int _bandCount = -1, _bandRate;
    private double _bandLow, _bandHigh;
    private int[] _binFrom = [], _binTo = [];
    private float[] _binAt = [], _tilt = [];

    private long _lastHeard = -1;
    private double _stillFor;
    private float[] _peakHold = [];
    private float[] _holdTime = [];

    public LiveAnalyzer()
    {
        for (var i = 0; i < FftSize; i++) _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
    }

    /// <summary>Each band's level, 0 (the floor) .. 1 (full scale).</summary>
    public float[] Levels { get; private set; } = [];

    /// <summary>Each band's recent highest level, held a moment before it falls.</summary>
    public float[] Peaks => _peakHold;

    /// <summary>Per channel (0 left, 1 right), in dBFS down to <see cref="MeterFloorDb"/>: the
    /// average level, the momentary peak, and the highest peak of the last second or so.</summary>
    public double[] RmsDb { get; } = [MeterFloorDb, MeterFloorDb];
    public double[] PeakDb { get; } = [MeterFloorDb, MeterFloorDb];
    public double[] HoldDb { get; } = [MeterFloorDb, MeterFloorDb];
    private readonly double[] _meterHoldTime = new double[2];

    /// <summary>A channel reached full scale within the last moment.</summary>
    public bool[] Clipped { get; } = new bool[2];
    private readonly double[] _clipTime = new double[2];

    /// <summary>Everything has sunk to nothing (no need to redraw until sound comes back).</summary>
    public bool IsQuiet { get; private set; } = true;

    /// <summary>One frame: reads what's being heard (scope null or not moving means silence),
    /// <paramref name="seconds"/> after the last frame.</summary>
    public void Update(OutputScope? scope, int bands, double lowHz, double highHz, double seconds)
    {
        seconds = Math.Clamp(seconds, 0.001, 0.25);
        var rate = scope?.SampleRate ?? (_bandRate > 0 ? _bandRate : 44100);
        if (bands != _bandCount || rate != _bandRate || lowHz != _bandLow || highHz != _bandHigh) Layout(bands, rate, lowHz, highHz);

        // The sound arrives in bursts (the device takes it ~10 ms at a time): only a scope that
        // has stood still a little while means it stopped.
        var sounding = false;
        if (scope is not null)
        {
            var heard = scope.Read(_stereo);
            _stillFor = heard != _lastHeard ? 0 : _stillFor + seconds;
            sounding = heard > 0 && _stillFor < 0.1;
            _lastHeard = heard;
        }
        else _lastHeard = -1;

        Span<float> spectrum = stackalloc float[bands];
        if (sounding) Spectrum(spectrum);
        else spectrum.Clear();
        var anything = false;
        for (var b = 0; b < bands; b++)
        {
            var level = Levels[b];
            var target = spectrum[b];
            // up at once (a little smoothed), down at a steady fall: the full height in about a second
            level = target > level ? level + (target - level) * (float)Math.Min(1, seconds * 30) : Math.Max(target, level - (float)seconds * 0.9f);
            Levels[b] = level;
            if (level >= _peakHold[b])
            {
                _peakHold[b] = level;
                _holdTime[b] = 0.6f;
            }
            else if ((_holdTime[b] -= (float)seconds) <= 0)
                _peakHold[b] = Math.Max(level, _peakHold[b] - (float)seconds * 0.5f);
            anything |= _peakHold[b] > 0.001f;
        }

        // the meters: the last 50 ms of each channel
        var window = Math.Min(FftSize, Math.Max(256, rate / 20));
        for (var channel = 0; channel < 2; channel++)
        {
            double peak = 0, sum = 0;
            if (sounding)
            {
                for (var i = FftSize - window; i < FftSize; i++)
                {
                    double s = _stereo[i * 2 + channel];
                    peak = Math.Max(peak, Math.Abs(s));
                    sum += s * s;
                }
            }
            var peakDb = ToDb(peak);
            var rmsDb = ToDb(Math.Sqrt(sum / window) * Math.Sqrt(2)); // a full-scale sine reads 0 dB
            PeakDb[channel] = peakDb > PeakDb[channel] ? peakDb : Math.Max(peakDb, PeakDb[channel] - seconds * 30);
            RmsDb[channel] = rmsDb > RmsDb[channel]
                ? RmsDb[channel] + (rmsDb - RmsDb[channel]) * Math.Min(1, seconds * 20)
                : Math.Max(rmsDb, RmsDb[channel] - seconds * 20);
            if (PeakDb[channel] >= HoldDb[channel])
            {
                HoldDb[channel] = PeakDb[channel];
                _meterHoldTime[channel] = 1.2;
            }
            else if ((_meterHoldTime[channel] -= seconds) <= 0)
                HoldDb[channel] = Math.Max(PeakDb[channel], HoldDb[channel] - seconds * 15);
            if (peak >= 0.995) _clipTime[channel] = 1.5;
            else _clipTime[channel] -= seconds;
            Clipped[channel] = _clipTime[channel] > 0;
            anything |= HoldDb[channel] > MeterFloorDb + 0.01;
        }
        IsQuiet = !anything;
    }

    private static double ToDb(double amplitude) => amplitude <= 0 ? MeterFloorDb : Math.Max(MeterFloorDb, 20 * Math.Log10(amplitude));

    /// <summary>This moment's spectrum (the middle of left and right) into the bands, 0..1.</summary>
    private void Spectrum(Span<float> into)
    {
        for (var i = 0; i < FftSize; i++)
        {
            _fft[i].X = (_stereo[i * 2] + _stereo[i * 2 + 1]) * 0.5f * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftOrder, _fft); // scaled by 1/N
        for (var i = 0; i <= FftSize / 2; i++)
        {
            var c = _fft[Math.Min(i, FftSize - 1)];
            // ×4: the window's half gain and the spectrum's two halves, so a full-scale sine reads 1
            _magnitudes[i] = MathF.Sqrt(c.X * c.X + c.Y * c.Y) * 4;
        }
        for (var b = 0; b < into.Length; b++)
        {
            float magnitude;
            if (_binAt[b] < 0)
            {
                into[b] = 0;
                continue;
            }
            if (_binTo[b] > _binFrom[b])
            {
                magnitude = 0;
                for (var k = _binFrom[b]; k < _binTo[b]; k++) magnitude = Math.Max(magnitude, _magnitudes[k]);
            }
            else
            {
                var at = _binAt[b];
                var k = Math.Min((int)at, FftSize / 2 - 1);
                magnitude = _magnitudes[k] + (_magnitudes[k + 1] - _magnitudes[k]) * (at - k);
            }
            var db = 20 * Math.Log10(Math.Max(magnitude, 1e-9)) + _tilt[b];
            into[b] = (float)Math.Clamp((db - SpectrumFloorDb) / -SpectrumFloorDb, 0, 1);
        }
    }

    private void Layout(int bands, int rate, double lowHz, double highHz)
    {
        _bandCount = bands;
        _bandRate = rate;
        _bandLow = lowHz;
        _bandHigh = highHz;
        _binFrom = new int[bands];
        _binTo = new int[bands];
        _binAt = new float[bands];
        _tilt = new float[bands];
        Levels = new float[bands];
        _peakHold = new float[bands];
        _holdTime = new float[bands];
        var binHz = (double)rate / FftSize;
        for (var b = 0; b < bands; b++)
        {
            var f0 = lowHz * Math.Pow(highHz / lowHz, (double)b / bands);
            var f1 = lowHz * Math.Pow(highHz / lowHz, (double)(b + 1) / bands);
            var middle = Math.Sqrt(f0 * f1);
            _binFrom[b] = Math.Clamp((int)Math.Ceiling(f0 / binHz), 1, FftSize / 2);
            _binTo[b] = Math.Clamp((int)Math.Ceiling(f1 / binHz), 1, FftSize / 2);
            _binAt[b] = (float)Math.Clamp(middle / binHz, 1, FftSize / 2 - 1);
            _tilt[b] = (float)(TiltDbPerOctave * Math.Log2(middle / 1000));
            // past what the file can hold (half its rate): nothing
            if (f0 >= rate / 2.0) _binAt[b] = -1;
        }
    }
}
