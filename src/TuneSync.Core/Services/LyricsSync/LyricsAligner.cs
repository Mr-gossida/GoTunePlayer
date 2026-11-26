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
/// between their neighbors in proportion to their length - over the singing only, when the
/// vocals say where that is.
///
/// Repeated lines (choruses) need a second look: the same words fit every repeat equally well,
/// and left to itself the alignment gave a heard chorus to the last repeat it could, so whole
/// choruses landed one repeat too early. The second round prefers, slightly, the repeat where
/// the lines that occur only once say it should be.</summary>
public static class LyricsAligner
{
    private const double MatchCost = 0;
    private const double MismatchCost = 1;
    private const double SkipHeardCost = 0.5;  // recognition heard something not in the lyrics
    private const double SkipLyricCost = 1;    // a lyric character recognition missed

    /// <summary>Lines sharing at least this share of their letter pairs count as repeats.</summary>
    private const double SimilarLines = 0.7;

    /// <summary>How far from its expected time a character may be heard at no cost (the expected
    /// times are rough), what each further tolerance's worth costs, and the most it costs.</summary>
    private const double OffTolerance = 5;
    private const double OffCost = 0.1;
    private const double OffCostCap = 0.3;

    /// <summary>Minimum share of a line's characters that must be found for its time to count.</summary>
    private const double ConfidentShare = 0.3;

    /// <summary>A line whose beginning was heard exactly - this many letters, a Hangul syllable
    /// counting two - is placed by it however little of the rest was heard (a backing chorus
    /// singing the line's end, "걱정 마 I believe", isn't heard from the vocals).</summary>
    private const double StartRunNeeded = 6;

    /// <summary>Longest a sung character plausibly takes (long notes included).</summary>
    private const double MaxCharTime = 0.7;

    /// <summary>Time per sung character when there's nothing better to go on.</summary>
    private static readonly TimeSpan DefaultCharTime = TimeSpan.FromSeconds(0.28);

    /// <param name="voice">Where the singer sings (from the separated vocals), or null: lines that
    /// weren't heard are then spread over the singing, not over instrumentals.</param>
    public static List<AlignedLine> Align(IReadOnlyList<string> lines, IReadOnlyList<TimedText> heard, TimeSpan duration, VoiceActivity? voice = null)
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
        foreach (var piece in DropLoops(heard, lines))
        {
            var text = Normalize(piece.Text);
            if (text.Length == 0) continue;
            var end = piece.End.TotalSeconds;
            var start = Math.Max(piece.Start.TotalSeconds, end - text.Length * MaxCharTime);
            var step = Math.Max(0, end - start) / text.Length;
            for (var k = 0; k < text.Length; k++) heardChars.Add((text[k], start + step * k));
        }

        var matched = Match(lyricChars, heardChars, null);
        var (times, confident, charTime) = Place(lines.Count, lyricChars, lineLength, heardChars, matched);

        // Repeated lines (a chorus) look the same to the alignment wherever they are, so a heard
        // chorus may be lined up with the wrong one of its repeats - all choices cost the same.
        // Line it up again, now with a slight preference for where each line should be by the
        // lines that can't be mistaken for others.
        if (ExpectedTimes(lines, lineLength, lyricChars, times, confident, charTime, duration.TotalSeconds, voice) is { } expected)
        {
            matched = Match(lyricChars, heardChars, expected);
            (times, confident, charTime) = Place(lines.Count, lyricChars, lineLength, heardChars, matched);
        }

        ShiftAmongRepeats(lines, times, confident);
        return Fill(lines, lineLength, times, confident, charTime, duration.TotalSeconds, voice);
    }

    /// <summary>What was heard without Whisper's loops: the same phrase written again and again in
    /// a row, which it does when it loses its place (after a rap in English, B.B's "하늘땅 별땅"
    /// got "다 같이 하는 걸 좋아한다고 했지만" eight times over the whole second verse). Such a loop
    /// shares letters with the lines sung there, and the alignment took it for them: five lines
    /// marked heard, 2-7 s early, and they outvoted the pass that had heard them right. A phrase
    /// repeated <see cref="LoopRepeats"/> times or more is dropped only when it looks like no line
    /// of the lyrics - a chant the song really repeats stays, and so does one Whisper misheard
    /// ("Just be there, be there" for Beat It's "Just beat it, beat it": at the right times, the
    /// alignment's best clue there).</summary>
    public static List<TimedText> DropLoops(IReadOnlyList<TimedText> heard, IReadOnlyList<string> lines)
    {
        var normalized = lines.Select(Normalize).ToList();
        var lyrics = string.Concat(normalized);
        var linePairs = normalized.Select(LetterPairs).ToList();
        // the share of the phrase's letter pairs that the lyric line most like it has
        double LikeALine(string phrase)
        {
            var pairs = LetterPairs(phrase);
            return pairs.Count == 0 ? 0 : linePairs.Max(line => pairs.Count(line.Contains) / (double)pairs.Count);
        }
        var keys = heard.Select(h => Normalize(h.Text)).ToList();
        var drop = new bool[heard.Count];
        for (var i = 0; i < heard.Count;)
        {
            // the phrase length (in pieces) whose repeats from here cover the most
            int bestLength = 0, bestRepeats = 0;
            for (var length = 1; length <= MaxLoopPieces && i + length * LoopRepeats <= heard.Count; length++)
            {
                if (keys.Skip(i).Take(length).Any(k => k.Length == 0)) break;
                var repeats = 1;
                while (i + (repeats + 1) * length <= heard.Count
                       && Enumerable.Range(0, length).All(k => keys[i + repeats * length + k] == keys[i + k]))
                    repeats++;
                if (repeats >= LoopRepeats && repeats * length > bestRepeats * bestLength) (bestLength, bestRepeats) = (length, repeats);
            }
            var phrase = bestLength == 0 ? "" : string.Concat(keys.Skip(i).Take(bestLength));
            if (phrase.Length < 2) { i++; continue; }
            // Past the whole loop either way: one kept as singing would otherwise be found again a
            // word further on ("be there just" rather than "just be there"), looking less like the line.
            if (!lyrics.Contains(phrase, StringComparison.Ordinal) && LikeALine(phrase) < LoopLikeALine)
                for (var k = 0; k < bestLength * bestRepeats; k++) drop[i + k] = true;
            i += bestLength * bestRepeats;
        }
        return [.. heard.Where((_, k) => !drop[k])];
    }

    /// <summary>A phrase written this many times in a row (or more) is a loop, not singing.</summary>
    private const int LoopRepeats = 4;

    /// <summary>The longest phrase, in Whisper's pieces (words), looked for as a loop.</summary>
    private const int MaxLoopPieces = 12;

    /// <summary>A loop sharing at least this share of its letter pairs with one lyric line is a
    /// chant misheard, not made up. In the transcripts of 18 benchmark songs the made-up loops
    /// shared 0-0.38 ("Gap Playlist", "(music)", the 하늘땅 별땅 one 0.38), the misheard chants
    /// 0.55-1 ("Just be there", "Be the", "die for you I will").</summary>
    private const double LoopLikeALine = 0.5;

    private static HashSet<(char, char)> LetterPairs(string s) =>
        [.. Enumerable.Range(0, Math.Max(0, s.Length - 1)).Select(i => (s[i], s[i + 1]))];

    /// <summary>A line sung twice in a row of which only one was heard: the alignment can give
    /// what was heard to either (both cost the same), and it tends to take the second. Which one
    /// it was is told by the pace: the heard one is moved to its unheard twin when that spreads
    /// the singing between the heard lines around them more evenly over the lines. ("왜 날
    /// 설레이게 해" twice at 3:12 and 3:19 of 백예린's "왜? 날": the first was heard but given to
    /// the second, and the first was squeezed into the line before, 5 s early.)</summary>
    public static void ShiftAmongRepeats(IReadOnlyList<string> lines, double?[] times, bool[] confident)
    {
        // the song's usual time per line, from neighboring heard lines: per line, not per letter
        // (lines follow the bars of the music, and a held note makes a short line long - "나는
        // 견딜 수 없어", 7 letters over 9 s, made the letters say nothing here), and on the song's
        // own clock, not the singing's (the breath after a line is part of its bar: without it,
        // both choices here looked equally even)
        var paces = new List<double>();
        for (var k = 0; k + 1 < lines.Count; k++)
            if (confident[k] && confident[k + 1])
                paces.Add(times[k + 1]!.Value - times[k]!.Value);
        if (paces.Count < 3) return;
        paces.Sort();
        var pace = paces[paces.Count / 2];
        if (pace <= 0) return;

        var normalized = lines.Select(Normalize).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!confident[i]) continue;
            foreach (var j in new[] { i - 1, i + 1 })
            {
                if (j < 0 || j >= lines.Count || confident[j] || !Similar(normalized[i], normalized[j])) continue;
                int lo = Math.Min(i, j), hi = Math.Max(i, j);
                var before = Enumerable.Range(0, lo).LastOrDefault(k => confident[k], -1);
                var after = Enumerable.Range(hi + 1, lines.Count - hi - 1).FirstOrDefault(k => confident[k], -1);
                if (before < 0 || after < 0) continue;
                // an instrumental in the stretch (Beat It's guitar solo between two "Beat it"
                // lines) makes the pace say nothing: that's no place to judge by it
                if (times[after]!.Value - times[before]!.Value > (after - before) * pace * MaxStretch) continue;
                var at = times[i]!.Value;
                // how far the singing on either side of the heard line is from the usual pace
                double Uneven(int heardLine)
                {
                    double Off(double span, int from, int to) =>
                        span <= 0 ? double.MaxValue : Math.Abs(Math.Log(span / ((to - from) * pace)));
                    return Off(at - times[before]!.Value, before, heardLine) + Off(times[after]!.Value - at, heardLine, after);
                }
                if (Uneven(j) + EvenerBy >= Uneven(i)) continue;
                times[j] = times[i];
                confident[j] = true;
                confident[i] = false;
                break;
            }
        }
    }

    /// <summary>How much more even (in summed log pace ratios) the pace must get to move a heard
    /// line to its twin: a clear difference, not a coin toss.</summary>
    private const double EvenerBy = 0.25;

    /// <summary>A stretch taking more than this many times the usual time per line has an
    /// instrumental in it.</summary>
    private const double MaxStretch = 2;

    /// <summary>The estimates again, for lines put together from several passes: the heard lines
    /// may be others than in any one pass, so the lines between them are spread anew (a heard
    /// line out of order is taken as an estimate too).</summary>
    /// <param name="heard">Lines some pass heard, if at a time the others didn't agree on: their
    /// time is kept (still marked to check) where it fits between the lines around them.</param>
    public static List<AlignedLine> Refill(IReadOnlyList<AlignedLine> lines, TimeSpan duration, VoiceActivity? voice, bool[]? heard = null)
    {
        var texts = lines.Select(l => l.Text).ToList();
        var lengths = texts.Select(t => Normalize(t).Length).ToArray();
        var confident = lines.Select(l => l.IsConfident).ToArray();
        var times = lines.Select(l => (double?)l.Time.TotalSeconds).ToArray();
        var previous = -1.0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!confident[i]) continue;
            if (times[i]!.Value <= previous + 0.25) confident[i] = false;
            else previous = times[i]!.Value;
        }

        // Seconds per character, from neighboring heard lines (breaths included, so a little
        // generous - only the proportions matter between heard lines).
        var paces = new List<double>();
        for (var i = 0; i + 1 < lines.Count; i++)
            if (confident[i] && confident[i + 1] && lengths[i] >= 4)
                paces.Add((times[i + 1]!.Value - times[i]!.Value) / lengths[i]);
        paces.Sort();
        var charTime = paces.Count >= 5 ? Math.Clamp(paces[paces.Count / 2], 0.08, 0.8) : DefaultCharTime.TotalSeconds;

        // Lines heard at disputed times: kept where they're in order with everything kept.
        var keep = (bool[])confident.Clone();
        if (heard is not null)
        {
            var before = -1.0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (confident[i]) { before = times[i]!.Value; continue; }
                if (!heard[i]) continue;
                var next = Enumerable.Range(i + 1, lines.Count - i - 1).Where(k => confident[k]).Select(k => times[k]!.Value).DefaultIfEmpty(double.MaxValue).First();
                var t = times[i]!.Value;
                if (t > before + 0.25 && t < next - 0.25) { keep[i] = true; before = t; }
            }
        }
        var filled = Fill(texts, lengths, times, keep, charTime, duration.TotalSeconds, voice);
        return [.. filled.Select((l, i) => l with { IsConfident = confident[i] })];
    }

    /// <summary>Each line's time, and whether it was heard well enough to trust, from the
    /// characters found for it.</summary>
    private static (double?[] Times, bool[] Confident, double CharTime) Place(int lineCount,
        List<(char C, int Line, int Pos)> lyricChars, int[] lineLength, List<(char C, double Time)> heardChars, int?[] matched)
    {
        // Each line: when its first found character was heard, moved back by the characters
        // before it, and how much of the line was found.
        var charTime = EstimateCharTime(lyricChars, matched, heardChars);

        // Per line: the found characters, each giving an estimate of where the line starts
        // (its time minus the characters before it).
        var starts = new List<double>[lineCount];
        var first = new double[lineCount];
        var last = new double[lineCount];
        var firstPos = new int[lineCount];
        for (var i = 0; i < lineCount; i++) { starts[i] = []; first[i] = double.MaxValue; last[i] = double.MinValue; }
        for (var k = 0; k < lyricChars.Count; k++)
        {
            if (matched[k] is not { } h) continue;
            var (_, line, pos) = lyricChars[k];
            var t = heardChars[h].Time;
            starts[line].Add(t - pos * charTime);
            if (starts[line].Count == 1) firstPos[line] = pos;
            first[line] = Math.Min(first[line], t);
            last[line] = Math.Max(last[line], t);
        }

        // How long this song's lines take, per character: from the first to the last found
        // character of the lines that were mostly found (the slower ones of them). A slow ballad
        // holds its notes far longer than the gaps between neighboring characters suggest - by
        // those, its well-heard lines looked stretched and were all thrown out.
        var paces = new List<double>();
        for (var i = 0; i < lineCount; i++)
            if (lineLength[i] >= 4 && starts[i].Count >= lineLength[i] * 0.5 && last[i] > first[i]) paces.Add((last[i] - first[i]) / lineLength[i]);
        paces.Sort();
        var pace = paces.Count >= 3 ? Math.Max(charTime, paces[paces.Count * 3 / 4]) : charTime;

        // How much of each line's beginning was found unbroken, a Hangul syllable counting as
        // two letters (it says about as much as two Latin letters do).
        var startRun = new double[lineCount];
        var broken = new bool[lineCount];
        for (var k = 0; k < lyricChars.Count; k++)
        {
            var (c, line, _) = lyricChars[k];
            if (broken[line]) continue;
            if (matched[k] is not { } h || Substitution(c, heardChars[h].C) > 0) broken[line] = true;
            else startRun[line] += IsHangul(c) ? 2 : 1;
        }
        bool StartHeard(int line) => startRun[line] >= StartRunNeeded;

        var times = new double?[lineCount];
        var confident = new bool[lineCount];
        for (var i = 0; i < lineCount; i++)
        {
            if (starts[i].Count == 0 || lineLength[i] == 0) continue;
            // The line's first character, when it was found, says it directly. Otherwise the
            // median of the first few estimates: one stray early or late match can't drag it.
            var early = starts[i].Take(3).OrderBy(x => x).ToList();
            times[i] = Math.Max(0, firstPos[i] == 0 ? first[i] : early[early.Count / 2]);

            var enough = starts[i].Count >= Math.Max(1, lineLength[i] * ConfidentShare);
            // Found characters spread far wider than a line of this song takes to sing:
            // recognition lost its place here (skipped lines, stretched words), so its times
            // can't be trusted.
            var expected = lineLength[i] * pace;
            var span = last[i] - first[i];
            var plausible = span <= Math.Max(expected * 2.5, expected + 3);
            confident[i] = (enough || StartHeard(i)) && plausible;
        }

        // Times must move forward: a confident line that lands before an earlier confident one
        // (a repeated chorus matched to the wrong pass, say) is demoted to an estimate.
        var previous = -1.0;
        for (var i = 0; i < lineCount; i++)
        {
            if (!confident[i]) continue;
            if (times[i]!.Value <= previous + 0.25) confident[i] = false;
            else previous = times[i]!.Value;
        }

        return (times, confident, charTime);
    }

    /// <summary>Where each lyric character should be heard, going by the lines heard well that
    /// occur only once in the song (the rest spread between them as usual); null when there
    /// are no repeated lines, or too few such lines to go by.</summary>
    private static double[]? ExpectedTimes(IReadOnlyList<string> lines, int[] lineLength,
        List<(char C, int Line, int Pos)> lyricChars, double?[] times, bool[] confident, double charTime, double duration, VoiceActivity? voice)
    {
        var normalized = lines.Select(Normalize).ToList();
        var repeated = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            for (var j = 0; j < lines.Count && !repeated[i]; j++)
                repeated[i] = j != i && Similar(normalized[i], normalized[j]);
        if (!repeated.Any(r => r)) return null;

        var anchors = Enumerable.Range(0, lines.Count).Select(i => confident[i] && !repeated[i]).ToArray();
        if (anchors.Count(a => a) < 2) return null;
        var placed = Fill(lines, lineLength, times, anchors, charTime, duration, voice);
        return [.. lyricChars.Select(c => placed[c.Line].Time.TotalSeconds + c.Pos * charTime)];
    }

    /// <summary>Two lines that would be easy to mistake for each other: most of their letter
    /// pairs are shared ("Just beat it (beat it)" and "Just beat it (beat it, uh)").</summary>
    internal static bool Similar(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b;
        var pairs = new Dictionary<(char, char), int>();
        for (var k = 0; k + 1 < a.Length; k++) pairs[(a[k], a[k + 1])] = pairs.GetValueOrDefault((a[k], a[k + 1])) + 1;
        var shared = 0;
        for (var k = 0; k + 1 < b.Length; k++)
            if (pairs.GetValueOrDefault((b[k], b[k + 1])) is > 0 and var left) { pairs[(b[k], b[k + 1])] = left - 1; shared++; }
        return 2.0 * shared / (a.Length - 1 + b.Length - 1) >= SimilarLines;
    }

    /// <summary>For each lyric character, the heard character it lines up with (or null).</summary>
    /// <param name="expected">Where each lyric character should be heard, or null: lining one up
    /// with a character heard far from there costs a little, never more than a recognition slip,
    /// so it decides only between otherwise equal choices.</param>
    private static int?[] Match(List<(char C, int Line, int Pos)> lyric, List<(char C, double Time)> heard, double[]? expected)
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
                if (expected is not null)
                {
                    var off = Math.Abs(heard[j - 1].Time - expected[i - 1]) - OffTolerance;
                    if (off > 0) diag += (float)Math.Min(OffCostCap, OffCost * off / OffTolerance);
                }
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
                    if (Substitution(lyric[i - 1].C, heard[j - 1].C) <= KeepCost) result[i - 1] = j - 1;
                    i--; j--;
                    break;
                case 2: i--; break;
                default: j--; break;
            }
        }
        return result;
    }

    /// <summary>A lined-up pair of characters this alike counts as found (two Korean syllables
    /// differing in one part only: the initial consonant, the vowel or the final consonant).</summary>
    private const double KeepCost = 0.45;

    private static double Substitution(char a, char b)
    {
        if (a == b) return MatchCost;
        return IsHangul(a) && IsHangul(b) ? SoundCost(a, b) : MismatchCost;
    }

    /// <summary>How differently two Hangul syllables sound, 0..1: the initial consonant and the
    /// vowel count most, the final consonant least (it often moves onto the next syllable when
    /// sung - 했어 is heard as 해써). Letters that sound alike count as the same: ㅐ/ㅔ,
    /// ㅙ/ㅞ/ㅚ, tense and plain consonants, and final consonants by how they're pronounced
    /// (ㅅㅆㅈㅊㅌㅎ → ㄷ, ㄲㅋ → ㄱ, ㅍ → ㅂ).</summary>
    internal static double SoundCost(char a, char b)
    {
        var (ia, va, fa) = Split(a);
        var (ib, vb, fb) = Split(b);
        var cost = 0.0;
        if (Initial(ia) != Initial(ib)) cost += 0.45;
        if (Vowel(va) != Vowel(vb)) cost += 0.45;
        if (Final(fa) != Final(fb)) cost += 0.2;
        return Math.Min(cost, MismatchCost);

        static (int I, int V, int F) Split(char c) { var s = c - 0xAC00; return (s / 588, s % 588 / 28, s % 28); }
        // ㄲ→ㄱ, ㄸ→ㄷ, ㅃ→ㅂ, ㅆ→ㅅ, ㅉ→ㅈ
        static int Initial(int i) => i switch { 1 => 0, 4 => 3, 8 => 7, 10 => 9, 13 => 12, _ => i };
        // ㅔ→ㅐ, ㅖ→ㅒ, ㅚ/ㅞ→ㅙ
        static int Vowel(int v) => v switch { 5 => 1, 7 => 3, 11 or 15 => 10, _ => v };
        // finals as pronounced: ㄱ ㄴ ㄷ ㄹ ㅁ ㅂ ㅇ
        static int Final(int f) => f switch
        {
            2 or 3 or 9 or 24 => 1,
            5 or 6 => 4,
            19 or 20 or 22 or 23 or 25 or 27 => 7,
            11 or 12 or 13 or 15 => 8,
            10 => 16,
            14 or 18 or 26 => 17,
            _ => f,
        };
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
    private static List<AlignedLine> Fill(IReadOnlyList<string> lines, int[] lengths, double?[] times, bool[] confident, double charTime, double duration, VoiceActivity? voice)
    {
        if (voice is { Regions.Count: > 0 }) return FillSinging(lines, lengths, times, confident, charTime, voice);

        var result = new double[lines.Count];
        var anchors = Enumerable.Range(0, lines.Count).Where(i => confident[i]).ToList();
        // Weight of a line = its length plus a breath, so short lines still take some time.
        double Weight(int i) => Math.Max(lengths[i], 2) * charTime + 1.2;

        // The singing usually stops a little before the song does.
        var singingEnds = duration > 0 ? duration - Math.Min(12, duration * 0.06) : 0;

        if (anchors.Count == 0)
        {
            // Nothing heard: spread everything over the song after a short intro - slow songs
            // get more time per line, not just the usual pace (that bunched everything up early).
            var start = Math.Min(10, duration * 0.08);
            var total = Enumerable.Range(0, lines.Count).Sum(Weight);
            var scale = total > 0 && singingEnds > start ? Math.Clamp((singingEnds - start) / total, 0.5, 2.5) : 1;
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
            // after the last anchor: over what's left of the singing, at a pace between half
            // and twice the usual (a slow song's last verse isn't sung at the usual pace)
            var lastAnchor = anchors[^1];
            if (lastAnchor + 1 < lines.Count)
            {
                var needed = Enumerable.Range(lastAnchor, lines.Count - 1 - lastAnchor).Sum(Weight);
                var room = singingEnds - result[lastAnchor] - Weight(lines.Count - 1);
                var scale = duration > 0 && needed > 0 && room > 0 ? Math.Clamp(room / needed, 0.5, 2.0) : 1;
                for (var i = lastAnchor + 1; i < lines.Count; i++)
                {
                    result[i] = result[i - 1] + Weight(i - 1) * scale;
                    if (duration > 0) result[i] = Math.Min(result[i], Math.Max(result[i - 1] + 0.5, duration - 1));
                }
            }
        }

        return [.. lines.Select((text, i) => new AlignedLine(text, TimeSpan.FromSeconds(Math.Max(0, result[i])), confident[i]))];
    }

    /// <summary><see cref="Fill"/> on a clock that runs only while the singer sings: lines that
    /// weren't heard are spread over the singing around them, skipping instrumentals, and one
    /// that falls on a pause starts where the singing starts again.</summary>
    private static List<AlignedLine> FillSinging(IReadOnlyList<string> lines, int[] lengths, double?[] times, bool[] confident, double charTime, VoiceActivity voice)
    {
        var at = new double[lines.Count];
        var anchors = Enumerable.Range(0, lines.Count).Where(i => confident[i]).ToList();
        double Weight(int i) => Math.Max(lengths[i], 2) * charTime;
        var total = voice.TotalSinging;

        // Lines spread from one singing moment to another, each by its weight; the last line
        // gets its share too when it isn't an anchor.
        void Spread(int from, int to, double start, double end, bool lastTakesShare)
        {
            var weights = Enumerable.Range(from, to - from + (lastTakesShare ? 1 : 0)).Select(Weight).ToList();
            var sum = weights.Sum();
            var t = start;
            for (var i = from; i <= to && i < lines.Count; i++)
            {
                at[i] = t;
                if (i - from < weights.Count) t += (end - start) * weights[i - from] / sum;
            }
        }

        if (anchors.Count == 0) Spread(0, lines.Count - 1, 0, total, true);
        else
        {
            foreach (var i in anchors) at[i] = voice.SingingBefore(times[i]!.Value);
            // before the first anchor: back from it at the usual pace, or squeezed into the
            // singing there is
            var first = anchors[0];
            if (first > 0)
            {
                var needed = Enumerable.Range(0, first).Sum(Weight);
                var start = Math.Max(0, at[first] - needed);
                Spread(0, first, start, at[first], false);
            }
            for (var a = 0; a + 1 < anchors.Count; a++)
                if (anchors[a + 1] - anchors[a] >= 2) Spread(anchors[a], anchors[a + 1], at[anchors[a]], at[anchors[a + 1]], false);
            // after the last anchor: over the rest of the singing, at a pace between half and
            // three times the usual
            var last = anchors[^1];
            if (last + 1 < lines.Count)
            {
                var needed = Enumerable.Range(last, lines.Count - last).Sum(Weight);
                var room = Math.Clamp(total - at[last], needed * 0.5, needed * 3);
                Spread(last, lines.Count - 1, at[last], at[last] + room, true);
            }
        }

        return [.. lines.Select((text, i) => new AlignedLine(text,
            TimeSpan.FromSeconds(Math.Max(0, confident[i] ? times[i]!.Value : voice.TimeAt(at[i]))), confident[i]))];
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
