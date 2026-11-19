using TuneSync.Core.Audio.Dsd;

namespace TuneSync.Core.Audio;

/// <summary>A song decoded to floating-point frames (all channels interleaved), as the player
/// reads it: Windows' codecs (<see cref="MfDecoder"/>) or DSD (<see cref="DsdDecoder"/>).</summary>
public interface IAudioDecoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Bits per sample of a lossless original (0 when there is none to keep exact).</summary>
    int SourceBits { get; }

    /// <summary>Zero when it can't be told.</summary>
    TimeSpan Duration { get; }

    /// <summary>The samples were made by converting the file (DSD to PCM): never "bit-perfect".</summary>
    bool IsConverted { get; }

    /// <summary>The samples are DoP words (DSD carried in 24-bit PCM): they must reach the device
    /// untouched - no volume, no processing, no conversion.</summary>
    bool IsDoP { get; }

    /// <summary>The next read starts exactly at <paramref name="time"/>.</summary>
    void Seek(TimeSpan time);

    /// <summary>Fills <paramref name="target"/> with whole frames. Returns the number of frames;
    /// fewer than asked for only at the end of the song.</summary>
    int Read(Span<float> target);
}

public static class AudioDecoders
{
    /// <summary>The decoder for a file or address: DSD files by GoTune itself (as DoP when
    /// <paramref name="dop"/>, else converted to PCM), everything else by Windows' codecs.</summary>
    public static IAudioDecoder Open(string pathOrUrl, bool dop = false) =>
        DsdFile.IsDsdPath(pathOrUrl) ? new DsdDecoder(pathOrUrl, dop) : new MfDecoder(pathOrUrl);
}
