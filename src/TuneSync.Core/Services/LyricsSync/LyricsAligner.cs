using System.Globalization;
using System.Text;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>A piece of recognized speech (a word, usually) and when it was heard.</summary>
public sealed record TimedText(TimeSpan Start, TimeSpan End, string Text);

/// <summary>A lyric line with the time it starts. <see cref="IsConfident"/> is false when the
/// time was estimated from the lines around it rather than heard - the editor marks those to check.</summary>
public sealed record AlignedLine(string Text, TimeSpan Time, bool IsConfident);

/// <summary>Puts times on plain lyrics by lining their characters up against what speech
/// recognition heard. Both sides are reduced to letters and digits, then aligned with an edit-
/// distance alignment (recognition errors, missed words and extra words all cost a little); a line
/// starts where its first matched character was heard. Lines with too little matched are filled in
/// between their neighbors in proportion to their length.</summary>
public static class LyricsAligner
{
    private const double MatchCost = 0;
    private const double SimilarCost = 0.35;  // same Korean initial consonant + vowel (모/못)
    private const double MismatchCost = 1;
    private const double SkipHeardCost = 0.5;  // recognition heard something not in the lyrics
    private const double SkipLyricCost = 1;    // a lyric character recognition missed

    /// <summary>Minimum share of a line's characters that must be found for its time to count.</summary>
    private const double ConfidentShare = 0.3;

    /// <summary>Longest a sung character plausibly takes (long notes included).</summary>
    private const double MaxCharTime = 0.7;

    /// <summary>Time per sung character when there's nothing better to go on.</summary>
    private static readonly TimeSpan DefaultCharTime = TimeSpan.FromSeconds(0.28);

    public static List<AlignedLine> Align(IReadOnlyList<string> lines, IReadOnlyList<TimedText> heard, TimeSpan duration)
    {
        // Lyrics, character by character, remembering the line each came from.
        var lyricChars = new List<(char C, int Line, int Pos)>();
        var lineLength = new int[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var pos = 0;
            foreach (var c in Normalize(lines[i])) lyricChars.Add((c, i, pos++));
            lineLength[i] = pos;
        }

        // What was heard, character by character, each with a time spread evenly over its word.
        // A recognized word often starts where the previous one ended, silence included: a word
        // can't take longer than MaxCharTime per character, so its start is pulled up to fit.
        var heardChars = new List<(char C, double Time)>();
        foreach (var piece in heard)
        {
            var text = Normalize(piece.Text);
            if (text.Length == 0) continue;
            var end = piece.End.TotalSeconds;
            var start = Math.Max(piece.Start.TotalSeconds, end - text.Length * MaxCharTime);
            var step = Math.Max(0, end - start) / text.Length;
            for (var k = 0; k < text.Length; k++) heardChars.Add((text[k], start + step * k));
        }

        var matched = Match(lyricChars, heardChars);

        // Each line: when its first found character was heard, moved back by the characters
        // before it, and how much of the line was found.
        var charTime = EstimateCharTime(lyricChars, matched, heardChars);

        // Per line: the found characters, each giving an estimate of where the line starts
        // (its time minus the characters before it).
        var starts = new List<double>[lines.Count];
        var first = new double[lines.Count];
        var last = new double[lines.Count];
        for (var i = 0; i < lines.Count; i++) { starts[i] = []; first[i] = double.MaxValue; last[i] = double.MinValue; }
        for (var k = 0; k < lyricChars.Count; k++)
        {
            if (matched[k] is not { } h) continue;
            var (_, line, pos) = lyricChars[k];
            var t = heardChars[h].Time;
            starts[line].Add(t - pos * charTime);
            first[line] = Math.Min(first[line], t);
            last[line] = Math.Max(last[line], t);
        }

        var times = new double?[lines.Count];
        var confident = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (starts[i].Count == 0 || lineLength[i] == 0) continue;
            // The median of the first few estimates: one stray early or late match can't drag it.
            var early = starts[i].Take(3).OrderBy(x => x).ToList();
            times[i] = Math.Max(0, early[early.Count / 2]);

            var enough = starts[i].Count >= Math.Max(1, lineLength[i] * ConfidentShare);
            // Found characters spread far wider than the line takes to sing: recognition lost its
            // place here (skipped lines, stretched words), so its times can't be trusted.
            var expected = lineLength[i] * charTime;
            var span = last[i] - first[i];
            var plausible = span <= Math.Max(expected * 2.5, expected + 3);
            confident[i] = enough && plausible;
        }

        // Times must move forward: a confident line that lands before an earlier confident one
        // (a repeated chorus matched to the wrong pass, say) is demoted to an estimate.
        var previous = -1.0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!confident[i]) continue;
            if (times[i]!.Value <= previous + 0.25) confident[i] = false;
            else previous = times[i]!.Value;
        }

        return Fill(lines, lineLength, times, confident, charTime, duration.TotalSeconds);
    }

    /// <summary>For each lyric character, the heard character it lines up with (or null).</summary>
    private static int?[] Match(List<(char C, int Line, int Pos)> lyric, List<(char C, double Time)> heard)
    {
        int n = lyric.Count, m = heard.Count;
        var result = new int?[n];
        if (n == 0 || m == 0) return result;

        // Edit-distance alignment. Heard characters before the lyrics start and after they end
        // are free (an intro the recognizer mistook for words, applause, ...).
        var cost = new float[(n + 1) * (m + 1)]; // float: a long song is a few million cells
        var move = new byte[(n + 1) * (m + 1)]; // 1 diagonal, 2 skip lyric, 3 skip heard
        int W = m + 1;
        for (var i = 1; i <= n; i++) { cost[i * W] = (float)(i * SkipLyricCost); move[i * W] = 2; }
        for (var j = 1; j <= m; j++) { cost[j] = 0; move[j] = 3; }
        for (var i = 1; i <= n; i++)
        {
            var a = lyric[i - 1].C;
            for (var j = 1; j <= m; j++)
            {
                var diag = cost[(i - 1) * W + j - 1] + (float)Substitution(a, heard[j - 1].C);
                var up = cost[(i - 1) * W + j] + (float)SkipLyricCost;
                var left = cost[i * W + j - 1] + (i == n ? 0f : (float)SkipHeardCost);
                if (diag <= up && diag <= left) { cost[i * W + j] = diag; move[i * W + j] = 1; }
                else if (up <= left) { cost[i * W + j] = up; move[i * W + j] = 2; }
                else { cost[i * W + j] = left; move[i * W + j] = 3; }
            }
        }

        for (int i = n, j = m; i > 0 && j >= 0;)
        {
            switch (move[i * W + j])
            {
                case 1:
                    if (Substitution(lyric[i - 1].C, heard[j - 1].C) <= SimilarCost) result[i - 1] = j - 1;
                    i--; j--;
                    break;
                case 2: i--; break;
                default: j--; break;
            }
        }
        return result;
    }

    private static double Substitution(char a, char b)
    {
        if (a == b) return MatchCost;
        if (IsHangul(a) && IsHangul(b) && (a - 0xAC00) / 28 == (b - 0xAC00) / 28) return SimilarCost; // same initial + vowel
        return MismatchCost;
    }

    private static bool IsHangul(char c) => c is >= '가' and <= '힣';

    /// <summary>Seconds per sung character, from the found characters (median of the gaps between
    /// consecutive found characters of the same line).</summary>
    private static double EstimateCharTime(List<(char C, int Line, int Pos)> lyric, int?[] matched, List<(char C, double Time)> heard)
    {
        var gaps = new List<double>();
        for (var k = 1; k < lyric.Count; k++)
        {
            if (matched[k] is not { } b || matched[k - 1] is not { } a || lyric[k].Line != lyric[k - 1].Line) continue;
            var gap = heard[b].Time - heard[a].Time;
            if (gap is > 0.02 and < 2) gaps.Add(gap);
        }
        if (gaps.Count < 5) return DefaultCharTime.TotalSeconds;
        gaps.Sort();
        return Math.Clamp(gaps[gaps.Count / 2], 0.08, 0.8);
    }

    /// <summary>Estimates for the lines that weren't heard well: spread between the confident
    /// lines around them by length; before the first / after the last, at the sung pace.</summary>
    private static List<AlignedLine> Fill(IReadOnlyList<string> lines, int[] lengths, double?[] times, bool[] confident, double charTime, double duration)
    {
        var result = new double[lines.Count];
        var anchors = Enumerable.Range(0, lines.Count).Where(i => confident[i]).ToList();
        // Weight of a line = its length plus a breath, so short lines still take some time.
        double Weight(int i) => Math.Max(lengths[i], 2) * charTime + 1.2;

        if (anchors.Count == 0)
        {
            // Nothing heard: spread everything over the song after a short intro.
            var start = Math.Min(10, duration * 0.08);
            var total = Enumerable.Range(0, lines.Count).Sum(Weight);
            var scale = total > 0 && duration > start ? Math.Min(1, (duration - start) * 0.92 / total) : 1;
            var t = start;
            for (var i = 0; i < lines.Count; i++) { result[i] = t; t += Weight(i) * scale; }
        }
        else
        {
            foreach (var i in anchors) result[i] = times[i]!.Value;
            // before the first anchor
            for (var i = anchors[0] - 1; i >= 0; i--) result[i] = Math.Max(0, result[i + 1] - Weight(i));
            // between anchors
            for (var a = 0; a + 1 < anchors.Count; a++)
            {
                int from = anchors[a], to = anchors[a + 1];
                if (to - from < 2) continue;
                var span = result[to] - result[from];
                var weights = Enumerable.Range(from, to - from).Select(Weight).ToList();
                var sum = weights.Sum();
                var t = result[from];
                for (var i = from + 1; i < to; i++) { t += span * weights[i - from - 1] / sum; result[i] = t; }
            }
            // after the last anchor
            for (var i = anchors[^1] + 1; i < lines.Count; i++)
                result[i] = duration > 0 ? Math.Min(result[i - 1] + Weight(i - 1), Math.Max(result[i - 1] + 0.5, duration - 1)) : result[i - 1] + Weight(i - 1);
        }

        return [.. lines.Select((text, i) => new AlignedLine(text, TimeSpan.FromSeconds(Math.Max(0, result[i])), confident[i]))];
    }

    /// <summary>Letters and digits only, lower case; Hangul compatibility jamo and full-width forms
    /// folded to their usual forms.</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
