namespace TuneSync.Core.Models;

/// <summary>A track's loudness over time: one value per <see cref="BucketSeconds"/> slice,
/// normalized to 0 (silence) .. 1 (the track's loud parts).</summary>
public sealed record WaveformData(float[] Levels, double BucketSeconds)
{
    public double Duration => Levels.Length * BucketSeconds;

    /// <summary>Loudness at a playback position, interpolated between slices.</summary>
    public float LevelAt(double seconds)
    {
        if (Levels.Length == 0 || seconds < 0) return 0;
        var position = seconds / BucketSeconds;
        var index = (int)position;
        if (index >= Levels.Length - 1) return index < Levels.Length ? Levels[index] : 0;
        var t = (float)(position - index);
        return Levels[index] * (1 - t) + Levels[index + 1] * t;
    }
}
