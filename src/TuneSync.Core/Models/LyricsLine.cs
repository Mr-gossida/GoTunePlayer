namespace TuneSync.Core.Models;

/// <summary>One timestamped line from an LRC-format lyrics string.</summary>
public readonly record struct LyricsLine(TimeSpan Time, string Text);
