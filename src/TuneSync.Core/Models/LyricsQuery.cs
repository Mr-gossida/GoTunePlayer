namespace TuneSync.Core.Models;

/// <summary>What is known about a song when looking up its lyrics. Album and length are optional
/// but tell versions apart: the same title often exists as a live take, a remix or a
/// foreign-language release, each with its own lyrics and timing.</summary>
public sealed record LyricsQuery(string Artist, string Title, string? Album = null, TimeSpan? Duration = null)
{
    /// <summary>The length, when it's known and plausible.</summary>
    public double? Seconds => Duration is { TotalSeconds: > 1 } d ? d.TotalSeconds : null;
}
