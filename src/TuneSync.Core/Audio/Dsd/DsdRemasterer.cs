using TuneSync.Core.Models;

namespace TuneSync.Core.Audio.Dsd;

/// <summary>"DSD remastering": the player's PCM (after the equalizer and the volume) turned into
/// DSD and packed as DoP for the DAC. To the player it is one more rate converter (song frames
/// in, DoP frames out), so it slots in where the resampler goes.
/// <list type="number">
/// <item>Up to 1/8 of the DSD rate (352.8 kHz for DSD64) with the polyphase converter and the
/// chosen filter - all the band-limiting happens here, in 64-bit.</item>
/// <item>Up to the DSD rate itself by straight-line interpolation: whatever that leaves is far
/// above 300 kHz, where DSD's own noise already lives.</item>
/// <item>The fifth-order sigma-delta modulator (<see cref="DsdModulator"/>), one per channel;
/// PCM full scale becomes 50 % modulation, DSD's 0 dB.</item>
/// <item>Sixteen bits per channel per DoP frame (<see cref="Dop"/>).</item>
/// </list></summary>
public sealed class DsdRemasterer : IResampler
{
    private readonly IResampler _up;
    private readonly DsdModulator _left, _right;
    private double[] _mid = new double[8192];       // stage 1 output, stereo
    private int _midCount;                           // frames waiting (0 or 1 between calls)
    private double _lastLeft, _lastRight;
    private bool _markerHigh;

    public DsdRemasterer(int songRate, int dsdRate, ResampleFilter filter)
    {
        DsdRate = dsdRate;
        _up = Resamplers.Create(songRate, dsdRate / 8, filter);
        var oversampling = dsdRate / (dsdRate % 44100 == 0 ? 44100 : 48000);
        _left = new DsdModulator(oversampling);
        _right = new DsdModulator(oversampling);
    }

    public int DsdRate { get; }

    /// <summary>DoP frames per second (1/16 of the DSD rate).</summary>
    public int DopRate => DsdRate / 16;

    /// <summary>The DSD rate for a song: DSD64 (or DSD128) of the song's rate family.</summary>
    public static int DsdRateFor(int songRate, int multiple) => (songRate % 11025 == 0 ? 44100 : 48000) * multiple;

    public int LookaheadFrames => _up.LookaheadFrames;

    /// <summary>Times a modulator had to be reset (should stay 0).</summary>
    public int Resets => _left.Resets + _right.Resets;

    public int InputFor(int outFrames) => _up.InputFor(Math.Max(0, outFrames * 2 - _midCount));

    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        var maxOut = output.Length / 2;
        var room = maxOut * 2 - _midCount;
        if (_mid.Length < (maxOut * 2 + 2) * 2) Array.Resize(ref _mid, (maxOut * 2 + 2) * 2);
        var made = _up.Process(input, _mid.AsSpan(_midCount * 2, Math.Max(0, room) * 2));
        _midCount += made;

        var frames = Math.Min(maxOut, _midCount / 2);
        for (var f = 0; f < frames; f++)
        {
            int left0 = 0, left1 = 0, right0 = 0, right1 = 0;
            for (var half = 0; half < 2; half++)
            {
                var at = (f * 2 + half) * 2;
                double left = _mid[at], right = _mid[at + 1];
                int l = 0, r = 0;
                for (var j = 1; j <= 8; j++)
                {
                    var t = j / 8.0;
                    l = l << 1 | (_left.Next(Scale(_lastLeft + (left - _lastLeft) * t)) ? 1 : 0);
                    r = r << 1 | (_right.Next(Scale(_lastRight + (right - _lastRight) * t)) ? 1 : 0);
                }
                _lastLeft = left;
                _lastRight = right;
                if (half == 0) { left0 = l; right0 = r; }
                else { left1 = l; right1 = r; }
            }
            var marker = _markerHigh ? 0xFA : 0x05;
            _markerHigh = !_markerHigh;
            output[f * 2] = Dop.Sample(marker, (byte)left0, (byte)left1);
            output[f * 2 + 1] = Dop.Sample(marker, (byte)right0, (byte)right1);
        }
        // keep what didn't make a whole frame
        var used = frames * 2;
        var rest = _midCount - used;
        if (rest > 0) Array.Copy(_mid, used * 2, _mid, 0, rest * 2);
        _midCount = rest;
        return frames;
    }

    /// <summary>PCM to DSD units, kept within what the loop can take (a little over DSD's 0 dB).</summary>
    private static double Scale(double pcm) => Math.Clamp(pcm * DsdModulator.PcmFullScale, -0.6, 0.6);
}
