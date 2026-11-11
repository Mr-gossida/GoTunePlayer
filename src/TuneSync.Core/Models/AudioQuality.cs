namespace TuneSync.Core.Models;

/// <summary>What a file's audio stream is, for the "FLAC · 24bit · 96kHz" line in the player.
/// Zero means the container didn't say (e.g. MP3 has no bit depth).</summary>
public sealed record AudioQuality(string Format, int BitrateKbps, int SampleRate, int BitsPerSample, int Channels, bool IsLossless)
{
    /// <summary>Above CD quality (16bit / 44.1-48kHz), as streaming services label it.</summary>
    public bool IsHiRes => IsLossless && (BitsPerSample > 16 || SampleRate > 48000);

    /// <summary>"FLAC · 24bit · 96kHz · 2,304kbps" / "MP3 · 320kbps · 44.1kHz".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Format };
            if (BitsPerSample > 0) parts.Add($"{BitsPerSample}bit");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (SampleRate > 0) parts.Add((SampleRate / 1000.0).ToString("0.#", inv) + "kHz");
            if (BitrateKbps > 0) parts.Add(BitrateKbps.ToString("N0", inv) + "kbps");
            if (Channels is 1) parts.Add("모노");
            else if (Channels > 2) parts.Add($"{Channels}채널");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>"Hi-Res", "Lossless", or null for lossy formats.</summary>
    public string? Badge => IsHiRes ? "Hi-Res" : IsLossless ? "Lossless" : null;
}
