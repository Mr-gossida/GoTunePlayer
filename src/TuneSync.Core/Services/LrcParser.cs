using System.Text.RegularExpressions;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Parses LRC-format lyrics (lines prefixed with one or more "[mm:ss.xx]" timestamps,
/// as LRCLIB returns and as TuneSync writes into the Lyrics tag) into timestamped lines sorted by
/// time. A line with no recognizable timestamp is simply skipped, so plain (unsynced) lyrics text
/// parses to an empty list - the caller falls back to showing the raw text in that case.</summary>
public static partial class LrcParser
{
    // [0-9], not \d: \d also takes other scripts' digits ("[０１:２３.４５]", Arabic-Indic), which
    // int.Parse then refuses - one such line threw out of reading the whole file's tags.
    [GeneratedRegex(@"\[([0-9]{2}):([0-9]{2})(?:[.:]([0-9]{1,3}))?\]")]
    private static partial Regex TimeTag();

    /// <summary>Every kind of line break as "\n": Windows (CR LF), old Mac (a lone CR - some
    /// taggers write lyrics this way), and the Unicode line/paragraph separators.</summary>
    public static string NormalizeNewlines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n').Replace('\u0085', '\n');

    /// <summary>The text's lines, trimmed (blank lines kept, so stanza breaks survive).</summary>
    public static List<string> SplitLines(string? text) =>
        [.. NormalizeNewlines(text).Split('\n').Select(l => l.Trim())];

    public static List<LyricsLine> Parse(string? lyricsText)
    {
        var lines = new List<LyricsLine>();
        if (string.IsNullOrWhiteSpace(lyricsText)) return lines;

        foreach (var rawLine in NormalizeNewlines(lyricsText).Split('\n'))
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

    [GeneratedRegex(@"^\[[A-Za-z#][\w#-]*:[^\]]*\]$")]
    private static partial Regex TagLinePattern();

    /// <summary>An LRC tag line that isn't lyrics: "[ar:…]", "[ti:…]", "[offset:…]", ….</summary>
    public static bool IsTagLine(string line) => TagLinePattern().IsMatch(line.Trim());

    /// <summary>How many lyric lines have no timestamp in lyrics where others do (half-synced
    /// lyrics). <see cref="Parse"/> skips them, so re-timing the parsed lines alone and saving
    /// would drop them. 0 for plain lyrics.</summary>
    public static int UntimedLines(string? lyricsText)
    {
        if (Parse(lyricsText).Count == 0) return 0;
        return NormalizeNewlines(lyricsText).Split('\n')
            .Count(l => l.Trim().Length > 0 && !TimeTag().IsMatch(l) && !IsTagLine(l));
    }

    /// <summary>Every lyric line in the file's order: a timed one with its time (once per
    /// timestamp), an untimed one with none. Tag lines and blank untimed lines are left out.</summary>
    public static List<(TimeSpan? Time, string Text)> ParseInFileOrder(string? lyricsText)
    {
        var lines = new List<(TimeSpan?, string)>();
        foreach (var rawLine in NormalizeNewlines(lyricsText).Split('\n'))
        {
            var matches = TimeTag().Matches(rawLine);
            var text = TimeTag().Replace(rawLine, "").Trim();
            if (matches.Count > 0)
                foreach (Match m in matches) lines.Add((ParseTimestamp(m), text));
            else if (text.Length > 0 && !IsTagLine(text))
                lines.Add((null, text));
        }
        return lines;
    }

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
