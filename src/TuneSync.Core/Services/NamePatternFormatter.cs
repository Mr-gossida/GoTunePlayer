using System.Text;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Token substitution engine shared by <see cref="RenameService"/> and
/// <see cref="MergeService"/>'s "이름 변경 후 둘 다 보존" conflict action, so the placeholder
/// syntax and forbidden-character handling are written and tested exactly once.</summary>
public static class NamePatternFormatter
{
    public static string Sanitize(string value, RenameRuleSet rules)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == ':') sb.Append(rules.ColonReplacement);
            else if (Array.IndexOf(RenameRuleSet.ForbiddenChars, ch) >= 0) sb.Append(rules.OtherForbiddenReplacement);
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Substitutes {Artist} {AlbumArtist} {Title} {Album} {Track} {Disc} {Year} in
    /// <paramref name="pattern"/> with <paramref name="tags"/>'s (sanitized) values. {Track}
    /// is zero-padded to 2 digits, prefixed with "{Disc}-" when there's more than one disc.</summary>
    public static string Format(string pattern, TrackTags tags, RenameRuleSet rules)
    {
        var artist = Sanitize(tags.Artist, rules);
        var albumArtist = Sanitize(tags.AlbumArtist, rules);
        var title = Sanitize(tags.Title, rules);
        var album = Sanitize(tags.Album, rules);

        var trackNo = tags.Disc > 1 ? $"{tags.Disc}-{tags.Track:D2}" : tags.Track.ToString("D2");

        return pattern
            .Replace("{Artist}", artist)
            .Replace("{AlbumArtist}", albumArtist)
            .Replace("{Title}", title)
            .Replace("{Album}", album)
            .Replace("{Track}", trackNo)
            .Replace("{Disc}", tags.Disc.ToString())
            .Replace("{Year}", tags.Year > 0 ? tags.Year.ToString() : "")
            .Trim();
    }
}
