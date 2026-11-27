using System.Text;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Token substitution engine shared by <see cref="RenameService"/> and
/// <see cref="MergeService"/>'s "이름 변경 후 둘 다 보존" conflict action, so the placeholder
/// syntax and forbidden-character handling are written and tested exactly once.</summary>
public static class NamePatternFormatter
{
    /// <summary>The text with what a file name can't hold replaced (<c>:</c> and the other forbidden
    /// characters) and control characters (a tab, a line break pasted into a tag) left out.</summary>
    public static string Sanitize(string value, RenameRuleSet rules)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == ':') sb.Append(rules.ColonReplacement);
            else if (Array.IndexOf(RenameRuleSet.ForbiddenChars, ch) >= 0) sb.Append(rules.OtherForbiddenReplacement);
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static readonly string[] DeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>Substitutes {Artist} {AlbumArtist} {Title} {Album} {Track} {Disc} {Year} in
    /// <paramref name="pattern"/> with <paramref name="tags"/>'s (sanitized) values. {Track}
    /// is zero-padded to 2 digits, prefixed with the disc ("2-07") when there's more than one disc
    /// and the pattern doesn't place {Disc} itself; {Disc} is empty for a song without one. The
    /// result is a name Windows keeps as it is: what the pattern itself writes is cleaned like the
    /// tags, and trailing dots and spaces (which Windows drops - the file came out named otherwise
    /// than the preview) and device names (CON, NUL…) are avoided.</summary>
    public static string Format(string pattern, TrackTags tags, RenameRuleSet rules)
    {
        var artist = Sanitize(tags.Artist, rules);
        var albumArtist = Sanitize(tags.AlbumArtist, rules);
        var title = Sanitize(tags.Title, rules);
        var album = Sanitize(tags.Album, rules);

        var placesDisc = pattern.Contains("{Disc}", StringComparison.Ordinal);
        var trackNo = tags.Disc > 1 && !placesDisc ? $"{tags.Disc}-{tags.Track:D2}" : tags.Track.ToString("D2");

        var name = Sanitize(pattern, rules) // the tokens hold no such characters; the pattern's own text may
            .Replace("{Artist}", artist)
            .Replace("{AlbumArtist}", albumArtist)
            .Replace("{Title}", title)
            .Replace("{Album}", album)
            .Replace("{Track}", trackNo)
            .Replace("{Disc}", tags.Disc > 0 ? tags.Disc.ToString() : "")
            .Replace("{Year}", tags.Year > 0 ? tags.Year.ToString() : "")
            .Trim().TrimEnd('.', ' ');
        return DeviceNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? name + "_" : name;
    }
}
