using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>How much of a song's lyrics is worth showing. Real lyrics are a few kilobytes and at
/// most a few hundred lines, but a tag can hold megabytes (a damaged or hostile file), and every
/// line shown becomes elements on the screen - the lyrics lists aren't virtualized, since their
/// scrolling needs every line laid out. Only what is shown is cut: text written back to a file
/// must always be the whole of it.</summary>
public static class LyricsLimits
{
    /// <summary>Characters of the lyrics looked at for showing.</summary>
    public const int MaxChars = 256 * 1024;

    /// <summary>Lines shown, synced or plain. Needed besides the character limit: a short
    /// timestamp repeated makes a line every few characters.</summary>
    public const int MaxLines = 2000;

    /// <summary>The line breaks <see cref="LrcParser.NormalizeNewlines"/> knows: LF, CR, the
    /// Unicode line and paragraph separators, and NEL.</summary>
    private static readonly char[] LineBreaks = ['\n', '\r', (char)0x2028, (char)0x2029, (char)0x85];

    /// <summary>The part of <paramref name="text"/> that is looked at: all of it within
    /// <see cref="MaxChars"/>, otherwise up to the last line break before the limit (so no half
    /// line shows).</summary>
    public static string Clip(string text)
    {
        if (text.Length <= MaxChars) return text;
        var cut = text.LastIndexOfAny(LineBreaks, MaxChars);
        if (cut <= 0)
        {
            // One enormous line: cut it at the limit, but not through a character's two halves.
            cut = char.IsHighSurrogate(text[MaxChars - 1]) ? MaxChars - 1 : MaxChars;
        }
        return text[..cut];
    }

    /// <summary>The synced lines to show (see <see cref="LrcParser.Parse"/>): the earliest
    /// <see cref="MaxLines"/>, from the first <see cref="MaxChars"/> characters.</summary>
    public static List<LyricsLine> Parse(string? text)
    {
        var lines = LrcParser.Parse(text is null ? null : Clip(text));
        if (lines.Count > MaxLines) lines.RemoveRange(MaxLines, lines.Count - MaxLines);
        return lines;
    }

    /// <summary>The plain lines to show (see <see cref="LrcParser.SplitLines"/>): the first
    /// <see cref="MaxLines"/>, from the first <see cref="MaxChars"/> characters.</summary>
    public static List<string> SplitLines(string? text)
    {
        var lines = LrcParser.SplitLines(text is null ? null : Clip(text));
        if (lines.Count > MaxLines) lines.RemoveRange(MaxLines, lines.Count - MaxLines);
        return lines;
    }
}
