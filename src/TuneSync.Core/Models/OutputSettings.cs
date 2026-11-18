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

    /// <summary>In exclusive mode, the file's sound as it is: no equalizer, tone, reverb,
    /// limiter or volume (the receiver sets the loudness), always at the song's own rate - the
    /// samples reach the device unchanged.</summary>
    public bool BitPerfect { get; set; }

    /// <summary>Exclusive mode's sample rate: 0 = each song's own rate.</summary>
    public int ExclusiveSampleRate { get; set; }

    /// <summary>Exclusive mode's bits per sample (16, 24 or 32): 0 = the most the device takes.</summary>
    public int ExclusiveBits { get; set; }

    public OutputSettings Clone() => (OutputSettings)MemberwiseClone();

    public void Normalize()
    {
        if (!Enum.IsDefined(Layout)) Layout = SpeakerLayout.Stereo;
        if (ExclusiveSampleRate is < 0 or > 768000) ExclusiveSampleRate = 0;
        if (ExclusiveBits is not (0 or 16 or 24 or 32)) ExclusiveBits = 0;
        if (string.IsNullOrWhiteSpace(DeviceId)) DeviceId = null;
    }
}
