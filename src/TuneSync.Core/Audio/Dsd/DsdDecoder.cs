namespace TuneSync.Core.Audio.Dsd;

/// <summary>Reads a DSD file (<see cref="DsdFile"/>) for the player, one of two ways:
/// <list type="bullet">
/// <item>PCM: converted (<see cref="DsdDecimator"/>) to 176.4 kHz (192 kHz for the 48 kHz family),
/// so the equalizer, the volume and any device can take it.</item>
/// <item>DoP: the DSD itself, packed into 24-bit words at 1/16 of the DSD rate (DSD64 → 176.4 kHz)
/// as the DoP standard has it, for a DAC that unpacks it. Stereo only.</item>
/// </list></summary>
public sealed class DsdDecoder : IAudioDecoder
{
    private readonly DsdFile _file;
    private readonly DsdDecimator[]? _decimators;
    private readonly byte[][] _bytes;
    private readonly double[] _pcm;
    private bool _markerHigh;               // DoP: the next frame's marker is 0xFA (else 0x05)

    public DsdDecoder(string path, bool dop, int pcmRate = 0)
    {
        _file = new DsdFile(path);
        try
        {
            IsDoP = dop && _file.Channels == 2;
            Channels = _file.Channels;
            DsdRate = _file.SampleRate;
            if (IsDoP)
            {
                SampleRate = DsdRate / 16;
            }
            else
            {
                SampleRate = pcmRate > 0 ? pcmRate : PcmRate(DsdRate);
                _decimators = new DsdDecimator[Channels];
                for (var c = 0; c < Channels; c++) _decimators[c] = new DsdDecimator(DsdRate, SampleRate);
            }
            _bytes = new byte[Channels][];
            for (var c = 0; c < Channels; c++) _bytes[c] = new byte[16384];
            _pcm = new double[16384];
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    /// <summary>The PCM rate DSD is turned into: 176.4 kHz (or 192 kHz), whatever the DSD rate.</summary>
    public static int PcmRate(int dsdRate)
    {
        var rate = dsdRate / 16;
        while (rate > 192000) rate /= 2;
        return rate;
    }

    public int DsdRate { get; }
    public string Name => _file.Name;
    public int SampleRate { get; }
    public int Channels { get; }
    public int SourceBits => IsDoP ? 24 : 0;
    public TimeSpan Duration => _file.Duration;
    public bool IsConverted => !IsDoP;
    public bool IsDoP { get; }

    public void Seek(TimeSpan time)
    {
        var frame = (long)(Math.Max(0, time.TotalSeconds) * SampleRate);
        _file.Seek(frame * (DsdRate / 8 / SampleRate));
        _markerHigh = false;
        if (_decimators is not null) foreach (var d in _decimators) d.Reset();
    }

    public int Read(Span<float> target)
    {
        var frames = target.Length / Channels;
        return IsDoP ? ReadDoP(target, frames) : ReadPcm(target, frames);
    }

    private int ReadDoP(Span<float> target, int frames)
    {
        var written = 0;
        while (written < frames)
        {
            var want = Math.Min(frames - written, _bytes[0].Length / 2);
            var got = _file.Read(_bytes, want * 2);
            if (got == 0) break;
            if (got % 2 == 1)
            {
                // the last odd byte: completed with DSD silence
                foreach (var channel in _bytes) channel[got] = 0x69;
                got++;
            }
            for (var i = 0; i < got / 2; i++)
            {
                var marker = _markerHigh ? 0xFA : 0x05;
                _markerHigh = !_markerHigh;
                for (var c = 0; c < 2; c++)
                    target[(written + i) * 2 + c] = Dop.Sample(marker, _bytes[c][2 * i], _bytes[c][2 * i + 1]);
            }
            written += got / 2;
        }
        return written;
    }

    private int ReadPcm(Span<float> target, int frames)
    {
        var decimators = _decimators!;
        var perSample = DsdRate / 8 / SampleRate;           // bytes of DSD per PCM sample
        var written = 0;
        while (written < frames)
        {
            var want = Math.Min(frames - written, Math.Min(_pcm.Length / Channels, _bytes[0].Length / perSample));
            var got = _file.Read(_bytes, want * perSample);
            if (got == 0) break;
            var made = 0;
            for (var c = 0; c < Channels; c++)
            {
                made = decimators[c].Process(_bytes[c].AsSpan(0, got), _pcm.AsSpan(c), Channels);
            }
            for (var i = 0; i < made * Channels; i++) target[written * Channels + i] = (float)_pcm[i];
            written += made;
        }
        return written;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>DoP (DSD over PCM, v1.1): sixteen DSD bits per channel in the low two bytes of a
/// 24-bit PCM sample, the first byte on top, under a marker byte that alternates 0x05 / 0xFA from
/// frame to frame - which is how a DAC tells it from PCM. Carried here as the exact float of that
/// 24-bit integer (a float holds any 24-bit integer exactly), so the bit-perfect path delivers it.</summary>
public static class Dop
{
    public static float Sample(int marker, byte first, byte second)
    {
        var word = marker << 16 | first << 8 | second;
        var signed = (word ^ 0x800000) - 0x800000;
        return signed / 8388608f;
    }

    /// <summary>A frame of DSD silence (0x69 0x69) under the given marker.</summary>
    public static float Silence(int marker) => Sample(marker, 0x69, 0x69);
}
