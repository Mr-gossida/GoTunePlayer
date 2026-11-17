using System.Text.RegularExpressions;

namespace TuneSync.Core.Services;

/// <summary>One line of a lyrics comparison: its text as written, and whether it is missing
/// from the other side (removed on the before side, new on the after side).</summary>
public sealed record LyricsDiffLine(string Text, bool Changed);

/// <summary>Lyrics before and after a change, line by line (the save review's "교체 전 / 교체 후").
/// Lines are matched by their words: LRC times (and [ar:]-style tags) are ignored, so syncing
/// plain lyrics doesn't mark every line - only lines whose text differs.</summary>
public static partial class LyricsDiff
{
    public sealed record Result(IReadOnlyList<LyricsDiffLine> Before, IReadOnlyList<LyricsDiffLine> After)
    {
        public int Removed => Before.Count(l => l.Changed);
        public int Added => After.Count(l => l.Changed);
    }

    /// <summary>Longer than this (lines x lines), lines are compared position by position: the
    /// table would be too big, and real lyrics are far shorter.</summary>
    private const long MaxCells = 4_000_000;

    public static Result Compare(string? before, string? after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var ka = a.Select(Key).ToArray();
        var kb = b.Select(Key).ToArray();
        var keepA = new bool[a.Count];
        var keepB = new bool[b.Count];

        if ((long)a.Count * b.Count <= MaxCells)
        {
            // Longest common subsequence of the line keys.
            var table = new int[a.Count + 1, b.Count + 1];
            for (var i = a.Count - 1; i >= 0; i--)
                for (var j = b.Count - 1; j >= 0; j--)
                    table[i, j] = ka[i] == kb[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            for (int i = 0, j = 0; i < a.Count && j < b.Count;)
            {
                if (ka[i] == kb[j]) { keepA[i++] = true; keepB[j++] = true; }
                else if (table[i + 1, j] >= table[i, j + 1]) i++;
                else j++;
            }
        }
        else
        {
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
                keepA[i] = keepB[i] = ka[i] == kb[i];
        }

        // Blank lines never count as changes (they only space verses).
        return new Result(
            [.. a.Select((t, i) => new LyricsDiffLine(t, !keepA[i] && ka[i].Length > 0))],
            [.. b.Select((t, i) => new LyricsDiffLine(t, !keepB[i] && kb[i].Length > 0))]);
    }

    private static List<string> Lines(string? text)
    {
        var lines = LrcParser.NormalizeNewlines(text).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        return lines;
    }

    /// <summary>What a line says: without leading [..] tags, spaces collapsed.</summary>
    private static string Key(string line) => Spaces().Replace(Tags().Replace(line, ""), " ").Trim();

    [GeneratedRegex(@"^(\s*\[[^\]]*\])+")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
