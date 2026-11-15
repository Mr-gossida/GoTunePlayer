using System.Globalization;
using System.Text;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Writes timed lines as LRC text ("[01:23.45]line"), the format the player reads.</summary>
public static class LrcWriter
{
    public static string Write(IEnumerable<(TimeSpan Time, string Text)> lines)
    {
        var sb = new StringBuilder();
        foreach (var (time, text) in lines)
        {
            var t = time < TimeSpan.Zero ? TimeSpan.Zero : time;
            var centis = (int)Math.Round(t.TotalMilliseconds / 10);
            sb.Append('[')
              .Append((centis / 6000).ToString("00", CultureInfo.InvariantCulture)).Append(':')
              .Append((centis / 100 % 60).ToString("00", CultureInfo.InvariantCulture)).Append('.')
              .Append((centis % 100).ToString("00", CultureInfo.InvariantCulture)).Append(']')
              .Append(text.Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>LRC tag lines that aren't lyrics ("[ar:…]", "[ti:…]", "[offset:…]", …), in order,
    /// so editing the timing of synced lyrics keeps them.</summary>
    public static List<string> HeaderLines(string lyrics) =>
        [.. LrcParser.NormalizeNewlines(lyrics).Split('\n').Select(l => l.Trim())
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[[A-Za-z#][\w#-]*:[^\]]*\]$"))];

    /// <summary><see cref="Write"/> with <paramref name="header"/> lines first.</summary>
    public static string Write(IEnumerable<string> header, IEnumerable<(TimeSpan Time, string Text)> lines)
    {
        var head = string.Join("\n", header);
        var body = Write(lines);
        return head.Length == 0 ? body : body.Length == 0 ? head : head + "\n" + body;
    }

    /// <summary>The lines of plain lyrics worth timing (blank lines and any timestamps dropped).</summary>
    public static List<string> PlainLines(string lyrics) =>
        [.. LrcParser.NormalizeNewlines(lyrics).Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\s*(\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]\s*)+", "").Trim())
            .Where(l => l.Length > 0)];
}
