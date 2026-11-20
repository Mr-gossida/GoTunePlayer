namespace TuneSync.Core.Audio;

/// <summary>The way the playing song's sound takes from the file to the device, step by step,
/// as the player is actually doing it (for the 소리 경로 panel). The steps, in the order the
/// player runs them: decode, tone (equalizer and the rest), volume, sample-rate conversion,
/// writing the device's format, the device.</summary>
public sealed record SignalPath
{
    /// <summary>The output as the badges show it (path, rate, conversion, layout, notes).</summary>
    public required OutputInfo Output { get; init; }

    /// <summary>"Windows Media Foundation", or GoTune's own DSD reader.</summary>
    public string Decoder { get; init; } = "";

    /// <summary>PCM decoded to 32-bit float samples (not DSD read as it is).</summary>
    public bool DecodesToFloat { get; init; }

    /// <summary>Bits of a lossless original (0 for lossy: the file holds none).</summary>
    public int SourceBits { get; init; }

    public int SourceRate { get; init; }
    public int SourceChannels { get; init; }

    /// <summary>A DSD song's own rate (e.g. 2,822,400) and name ("DSD64"); 0 / null for PCM.</summary>
    public int DsdRate { get; init; }
    public string? DsdName { get; init; }

    /// <summary>The tone chain at work, or null when it leaves the sound alone (off, or bypassed
    /// for bit-perfect / DoP).</summary>
    public ToneConfig? Tone { get; init; }

    public double Volume { get; init; }
    public bool Muted { get; init; }

    /// <summary>The device's own volume control does the turning down (the samples stay whole).</summary>
    public bool HardwareVolume { get; init; }

    public bool Resampling { get; init; }
    public int OutputRate { get; init; }
    public string? ResampleFilter { get; init; }

    public bool Exclusive { get; init; }

    /// <summary>The samples as written for the device (shared mode: 32-bit float to the mixer).</summary>
    public bool WritesFloat { get; init; }
    public int WriteBits { get; init; }

    /// <summary>The dither on the last bit, when processed sound goes to integer samples.</summary>
    public string? Dither { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>Times the device ran dry since the song started (a glitch was heard).</summary>
    public int Underruns { get; init; }
}
