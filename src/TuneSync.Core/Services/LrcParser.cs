using System.Text.RegularExpressions;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Parses LRC-format lyrics (lines prefixed with one or more "[mm:ss.xx]" timestamps,
/// as LRCLIB returns and as TuneSync writes into the Lyrics tag) into timestamped lines sorted by
/// time. A line with no recognizable timestamp is simply skipped, so plain (unsynced) lyrics text
/// parses to an empty list - the caller falls back to showing the raw text in that case.</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{2}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    public static List<LyricsLine> Parse(string? lyricsText)
    {
        var lines = new List<LyricsLine>();
        if (string.IsNullOrWhiteSpace(lyricsText)) return lines;

        foreach (var rawLine in lyricsText.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = TimeTag().Matches(rawLine);
            if (matches.Count == 0) continue;

            var text = TimeTag().Replace(rawLine, "").Trim();
            foreach (Match m in matches)
                lines.Add(new LyricsLine(ParseTimestamp(m), text));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    public static bool IsSynced(string? lyricsText) => Parse(lyricsText).Count > 0;

    private static TimeSpan ParseTimestamp(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value);
        var seconds = int.Parse(m.Groups[2].Value);
        var fraction = m.Groups[3].Success ? m.Groups[3].Value : "0";

        // Normalize 1-3 digit fractions to milliseconds ("5" -> 500ms, "50" -> 500ms, "500" -> 500ms).
        var milliseconds = fraction.Length switch
        {
            1 => int.Parse(fraction) * 100,
            2 => int.Parse(fraction) * 10,
            _ => int.Parse(fraction),
        };

        return new TimeSpan(0, 0, minutes, seconds, milliseconds);
    }
}
