namespace TuneSync.Core.Models;

/// <summary>Which speakers GoTune's sound is laid out on. Stereo is the music as it is; the
/// others spread it over more channels (see <see cref="Audio.ChannelLayout"/>).</summary>
public enum SpeakerLayout
{
    Stereo,
    Quad,
    Surround51,
    Surround71,
}

/// <summary>How far the sound is upsampled (설정 › 음향 › 업샘플링).</summary>
public enum UpsampleMode
{
    Off,
    /// <summary>Twice the song's rate (44.1 → 88.2 kHz).</summary>
    Double,
    /// <summary>Four times (44.1 → 176.4 kHz).</summary>
    Quadruple,
    /// <summary>The highest whole multiple of the song's rate the device takes (44.1 → 352.8 or 705.6 kHz) - a
    /// higher rate that isn't a multiple (192 kHz for a CD) is not used.</summary>
    Max,
}

/// <summary>The low-pass filter of GoTune's sample rate converter.</summary>
public enum ResampleFilter
{
    /// <summary>Linear phase, steep: flat to just below the original's Nyquist.</summary>
    Sharp,
    /// <summary>Minimum phase: the same response, no ringing before a transient.</summary>
    MinimumPhase,
    /// <summary>Starts falling earlier, much less ringing (apodizing).</summary>
    Soft,
}

/// <summary>How DSD files (DSF, DFF) are played.</summary>
public enum DsdOutput
{
    /// <summary>Converted to PCM: plays anywhere, with the equalizer and the volume.</summary>
    Pcm,
    /// <summary>Sent as DSD (DoP) to a DAC that takes it - exclusive mode with the device's own
    /// volume only, since DoP can't be turned down on the way.</summary>
    DoP,
}

/// <summary>PCM songs turned into DSD and sent as DoP (exclusive mode).</summary>
public enum DsdRemaster
{
    Off,
    Dsd64,
    Dsd128,
}

/// <summary>Where and how the sound goes out (설정 › 음향 › 출력).</summary>
public sealed class OutputSettings
{
    /// <summary>The playback device's id, or null for Windows' default device (followed when it
    /// changes - headphones plugged in, say).</summary>
    public string? DeviceId { get; set; }

    public SpeakerLayout Layout { get; set; } = SpeakerLayout.Stereo;

    /// <summary>Takes the device for GoTune alone (WASAPI exclusive): the sound goes to it
    /// untouched by Windows' mixer and its format - bit-perfect when the equalizer is off and the
    /// volume is at 100. Other programs can't play sound meanwhile.</summary>
    public bool Exclusive { get; set; }

    /// <summary>In exclusive mode, the file's sound as it is: no equalizer, tone, reverb or
    /// limiter, always at the song's own rate - the samples reach the device unchanged while
    /// GoTune's volume is at 100 %. Below that the samples are turned down (GoTune's volume always
    /// counts), unless <see cref="HardwareVolume"/> turns the device down instead.</summary>
    public bool BitPerfect { get; set; }

    /// <summary>Exclusive mode's sample rate: 0 = each song's own rate.</summary>
    public int ExclusiveSampleRate { get; set; }

    /// <summary>Exclusive mode's bits per sample (16, 24 or 32): 0 = the most the device takes.</summary>
    public int ExclusiveBits { get; set; }

    /// <summary>Sends the song at a higher sample rate, converted by GoTune's own filter
    /// (<see cref="Filter"/>). Exclusive: a whole multiple of the song's rate the device takes.
    /// Shared: the rate Windows runs the device at, so Windows' converter isn't used.</summary>
    public UpsampleMode Upsampling { get; set; }

    /// <summary>The filter for every rate conversion GoTune makes.</summary>
    public ResampleFilter Filter { get; set; }

    /// <summary>In exclusive mode, the volume is set on the device itself (its hardware volume,
    /// when it has one) instead of scaling the samples - so bit-perfect sound can be turned down
    /// too. Windows' volume for the device is put back when GoTune lets go of it.</summary>
    public bool HardwareVolume { get; set; }

    public DsdOutput DsdOutput { get; set; }

    public DsdRemaster Remaster { get; set; }

    public OutputSettings Clone() => (OutputSettings)MemberwiseClone();

    public void Normalize()
    {
        if (!Enum.IsDefined(Layout)) Layout = SpeakerLayout.Stereo;
        if (!Enum.IsDefined(Upsampling)) Upsampling = UpsampleMode.Off;
        if (!Enum.IsDefined(Filter)) Filter = ResampleFilter.Sharp;
        if (!Enum.IsDefined(DsdOutput)) DsdOutput = DsdOutput.Pcm;
        if (!Enum.IsDefined(Remaster)) Remaster = DsdRemaster.Off;
        if (ExclusiveSampleRate is < 0 or > 768000) ExclusiveSampleRate = 0;
        if (ExclusiveBits is not (0 or 16 or 24 or 32)) ExclusiveBits = 0;
        if (string.IsNullOrWhiteSpace(DeviceId)) DeviceId = null;
    }
}
